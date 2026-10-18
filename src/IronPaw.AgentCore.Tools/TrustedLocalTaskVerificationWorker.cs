using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

/// <summary>
/// Explicitly trusted command execution with the current user's OS authority. This is not an OS
/// sandbox: repository builds and tests can use network, credentials and files available to that user.
/// </summary>
public sealed class TrustedLocalTaskVerificationWorker : ITaskVerificationWorker
{
    private const string WorkerId = "trusted-local-process/v1";
    private readonly ImmutableDictionary<string, string> _profiles;

    public TrustedLocalTaskVerificationWorker(IEnumerable<TaskVerificationProfile> allowedProfiles,
        TaskVerificationAuthority authority)
    {
        if (authority != TaskVerificationAuthority.TrustedLocalProcess)
            throw new UnauthorizedAccessException("Explicit trusted local process authority is required.");
        ArgumentNullException.ThrowIfNull(allowedProfiles);
        var profiles = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var profile in allowedProfiles)
        {
            ValidateShape(profile);
            if (!profiles.TryAdd(profile.ProfileId, TaskVerificationIdentity.ProfileFingerprint(profile)))
                throw new ArgumentException("Verification profile identities must be unique.", nameof(allowedProfiles));
        }
        _profiles = profiles.ToImmutable();
    }

    public void ValidateProfile(TaskVerificationProfile profile, CallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.IsOwner) throw new UnauthorizedAccessException("Only an owner can authorize trusted local verification.");
        ValidateShape(profile);
        if (!_profiles.TryGetValue(profile.ProfileId, out var fingerprint) || fingerprint != TaskVerificationIdentity.ProfileFingerprint(profile))
            throw new UnauthorizedAccessException("The exact verification profile was not approved by this worker host.");
    }

    public async Task<TaskVerificationCompletion> ExecuteAsync(TaskVerificationJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        Process? process = null;
        FileStream? executable = null;
        FileStream? owner = null;
        var started = false;
        var evidenceValidated = false;
        var output = new OutputCapture(job.Profile.MaxOutputBytes);
        var error = new OutputCapture(job.Profile.MaxOutputBytes);
        using var readStop = new CancellationTokenSource();
        Task? outputTask = null;
        Task? errorTask = null;
        TaskVerificationCompletion completion;
        try
        {
            ValidateShape(job.Profile);
            if (!_profiles.TryGetValue(job.Profile.ProfileId, out var allowed) || allowed != job.ProfileFingerprint ||
                allowed != TaskVerificationIdentity.ProfileFingerprint(job.Profile))
                throw new UnauthorizedAccessException("The exact verification command is not allowed by this worker.");
            ct.ThrowIfCancellationRequested();
            await GitTaskWorkspaceManager.ValidateVerificationCopyAsync(job, checkCopy: false, ct).ConfigureAwait(false);
            evidenceValidated = true;
            var lockPath = Path.Combine(job.Copy.EvidencePath, "worker.lock");
            TaskWorkspacePaths.NoLinks(lockPath);
            if (File.Exists(lockPath))
                return Completion(TaskCommandOutcome.NeedsReconciliation, "This copy already has a worker admission; its effects require reconciliation.");
            owner = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var workingDirectory = TaskWorkspacePaths.Resolve(job.Copy.WorkspacePath, job.Profile.WorkingDirectory);
            if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("The configured verification working directory is missing.");
            string? reportPath = null;
            if (job.Profile.Kind == TaskVerificationKind.Tests)
            {
                reportPath = TaskWorkspacePaths.Resolve(job.Copy.WorkspacePath, job.Profile.TestResultsPath!);
                if (File.Exists(reportPath) || Directory.Exists(reportPath))
                    throw new InvalidOperationException("Test verification requires a fresh report; the configured result path already exists.");
            }
            var info = new ProcessStartInfo(job.Profile.ExecutablePath)
            {
                WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            };
            foreach (var argument in job.Profile.Arguments) info.ArgumentList.Add(argument);
            ConfigureEnvironment(info, job.Copy.EvidencePath);
            TaskWorkspacePaths.NoLinks(job.Profile.ExecutablePath);
            executable = new FileStream(job.Profile.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if ("sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(executable, ct).ConfigureAwait(false)) != job.Profile.ExecutableDigest)
                throw new InvalidOperationException("The configured executable content has changed.");
            // Recheck the copy after preparation and immediately before the only process admission.
            await GitTaskWorkspaceManager.ValidateVerificationCopyAsync(job, checkCopy: true, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            process = new Process { StartInfo = info };
            if (!process.Start()) throw new InvalidOperationException("The verification process could not be started.");
            started = true;
            process.StandardInput.Close();
            outputTask = output.DrainAsync(process.StandardOutput.BaseStream, readStop.Token);
            errorTask = error.DrainAsync(process.StandardError.BaseStream, readStop.Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(job.Profile.Timeout);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (output.ReadError is not null || error.ReadError is not null)
                completion = Completion(TaskCommandOutcome.NeedsReconciliation, "The process exited but a redirected output stream could not be completely observed.", process.ExitCode);
            else
            {
                await GitTaskWorkspaceManager.ValidateVerificationCopyAsync(job, checkCopy: false, CancellationToken.None).ConfigureAwait(false);
                if (process.ExitCode != 0)
                    completion = Completion(TaskCommandOutcome.Failed, "The approved verification command returned a nonzero exit code.", process.ExitCode);
                else if (reportPath is not null)
                {
                    var tests = await ReadTestsAsync(reportPath, CancellationToken.None).ConfigureAwait(false);
                    completion = Completion(TaskCommandOutcome.Passed, "The approved test command exited successfully and its fresh TRX report contains passing tests.", 0, tests);
                }
                else completion = Completion(TaskCommandOutcome.Passed, "The approved build command exited successfully for the exact prepared revision.", 0);
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            if (started)
            {
                try { if (!process!.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception killError) when (killError is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                try { await process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception waitError) when (waitError is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                if (failure is TestReportException || failure is TaskInputConflictException && process!.HasExited)
                    completion = Completion(TaskCommandOutcome.Failed, failure.Message, process!.HasExited ? process.ExitCode : null);
                else completion = Completion(TaskCommandOutcome.NeedsReconciliation,
                    "The started verification process was interrupted or could not be completely observed; process-tree termination cannot prove detached effects stopped. " + failure.Message,
                    process!.HasExited ? process.ExitCode : null);
            }
            else completion = Completion(TaskCommandOutcome.Failed, "Verification did not start: " + failure.Message);
        }
        finally
        {
            readStop.Cancel();
            if (outputTask is not null && errorTask is not null)
            {
                try { await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception drainError) when (drainError is IOException or OperationCanceledException or TimeoutException or ObjectDisposedException) { }
            }
            process?.Dispose();
            executable?.Dispose();
            owner?.Dispose();
        }
        // Output tasks may finish during cleanup; preserve their final bounded prefixes and full-stream digests.
        completion = completion with { StandardOutput = output.Log(), StandardError = error.Log() };
        try
        {
            if (evidenceValidated) await SaveEvidenceAsync(job.Copy.EvidencePath, completion).ConfigureAwait(false);
        }
        catch (Exception evidenceError) when (evidenceError is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            completion = completion with { Outcome = started ? TaskCommandOutcome.NeedsReconciliation : TaskCommandOutcome.Failed,
                Explanation = completion.Explanation + " Evidence persistence failed: " + evidenceError.Message };
        }
        return completion;

        TaskVerificationCompletion Completion(TaskCommandOutcome outcome, string explanation, int? exit = null,
            TaskVerificationTestSummary? tests = null) => new(outcome, WorkerId, explanation, exit, output.Log(), error.Log(), tests);
    }

    private static void ValidateShape(TaskVerificationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.ProfileId) || profile.ProfileId != profile.ProfileId.Trim() || profile.ProfileId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(profile.ExecutablePath) || !Path.IsPathFullyQualified(profile.ExecutablePath) ||
            profile.ExecutablePath.Any(char.IsControl) || profile.ExecutablePath != Path.GetFullPath(profile.ExecutablePath) ||
            profile.ExecutableDigest is not { Length: 71 } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !digest[7..].All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            profile.Arguments.IsDefault || profile.Arguments.Any(value => value is null || value.Contains('\0')) ||
            profile.Timeout <= TimeSpan.Zero || profile.Timeout > TimeSpan.FromDays(1) ||
            profile.MaxOutputBytes is < 1 or > 1_048_576 || !Enum.IsDefined(profile.Kind))
            throw new ArgumentException("The trusted verification profile is malformed.", nameof(profile));
        TaskWorkspacePaths.Relative(profile.WorkingDirectory);
        if (profile.Kind == TaskVerificationKind.Tests) TaskWorkspacePaths.Relative(profile.TestResultsPath!, allowRoot: false);
        else if (profile.TestResultsPath is not null) throw new ArgumentException("Build profiles cannot declare test reports.", nameof(profile));
    }

    private static void ConfigureEnvironment(ProcessStartInfo info, string evidencePath)
    {
        var environmentRoot = Path.Combine(evidencePath, "environment");
        TaskWorkspacePaths.NoLinks(environmentRoot);
        if (Directory.Exists(environmentRoot) || File.Exists(environmentRoot))
            throw new InvalidOperationException("The verification environment already exists.");
        var home = Path.Combine(environmentRoot, "home");
        var temp = Path.Combine(environmentRoot, "temp");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(temp);
        info.Environment.Clear();
        // Only OS discovery and the explicitly selected executable directory are retained. No parent
        // provider credentials, user PATH entries or user profile environment values are inherited.
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        var binaryRoot = Path.GetDirectoryName(info.FileName)!;
        if (OperatingSystem.IsWindows())
        {
            if (systemRoot is null || !Path.IsPathFullyQualified(systemRoot)) throw new InvalidOperationException("The Windows system root is unavailable.");
            info.Environment["SystemRoot"] = systemRoot;
            info.Environment["WINDIR"] = systemRoot;
            info.Environment["PATH"] = binaryRoot + Path.PathSeparator + Path.Combine(systemRoot, "System32");
            // SDK/NuGet resolve machine-wide installation/configuration locations through these
            // OS variables. Preserve these explicit machine paths, never arbitrary parent values.
            foreach (var key in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles",
                "CommonProgramFiles(x86)", "CommonProgramW6432", "ProgramData", "ALLUSERSPROFILE" })
                if (Environment.GetEnvironmentVariable(key) is { } value && Path.IsPathFullyQualified(value))
                    info.Environment[key] = value;
        }
        else info.Environment["PATH"] = binaryRoot + Path.PathSeparator + "/usr/bin:/bin";
        info.Environment["HOME"] = home;
        info.Environment["USERPROFILE"] = home;
        info.Environment["APPDATA"] = Path.Combine(home, "AppData", "Roaming");
        info.Environment["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local");
        Directory.CreateDirectory(info.Environment["APPDATA"]!);
        Directory.CreateDirectory(info.Environment["LOCALAPPDATA"]!);
        info.Environment["TMP"] = temp;
        info.Environment["TEMP"] = temp;
        info.Environment["TMPDIR"] = temp;
        info.Environment["DOTNET_CLI_HOME"] = home;
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        info.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        info.Environment["NUGET_PACKAGES"] = Path.Combine(home, ".nuget", "packages");
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.Environment["DOTNET_ROOT"] = binaryRoot;
    }

    private static async Task<TaskVerificationTestSummary> ReadTestsAsync(string path, CancellationToken ct)
    {
        try
        {
            TaskWorkspacePaths.NoLinks(path);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is < 1 or > 1_048_576)
                throw new TestReportException("The fresh TRX report is missing, empty or larger than 1 MiB.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 1_048_576, IgnoreComments = true });
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct).ConfigureAwait(false);
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            if (document.Root?.Name != ns + "TestRun") throw new TestReportException("The test report is not a supported TRX document.");
            var summaries = document.Root.Elements(ns + "ResultSummary").ToArray();
            if (summaries.Length != 1 || summaries[0].Attribute("outcome")?.Value is not ("Passed" or "Completed"))
                throw new TestReportException("The TRX summary is missing, ambiguous or nonpassing.");
            var counters = summaries[0].Elements(ns + "Counters").ToArray();
            if (counters.Length != 1) throw new TestReportException("The TRX counters are missing or ambiguous.");
            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var attribute in counters[0].Attributes())
            {
                if (attribute.IsNamespaceDeclaration) continue;
                if (!int.TryParse(attribute.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    throw new TestReportException("TRX counters must be nonnegative integers.");
                values.Add(attribute.Name.LocalName, value);
            }
            if (!values.TryGetValue("total", out var total) || !values.TryGetValue("passed", out var passed) ||
                !values.TryGetValue("executed", out var executed) || !values.TryGetValue("failed", out var failed) ||
                !values.TryGetValue("notExecuted", out var skipped) || total <= 0 || passed <= 0 || failed != 0 ||
                executed != passed || (long)total != (long)passed + skipped ||
                values.Any(pair => pair.Key is not ("total" or "passed" or "executed" or "notExecuted") && pair.Value != 0))
                throw new TestReportException("The TRX report does not establish a nonempty, consistent passing test run.");
            var resultSets = document.Root.Elements(ns + "Results").ToArray();
            if (resultSets.Length != 1) throw new TestReportException("The TRX test results are missing or ambiguous.");
            var results = resultSets[0].Elements().ToArray();
            if (results.Any(item => item.Name != ns + "UnitTestResult" || item.Attribute("outcome")?.Value is not ("Passed" or "NotExecuted")) ||
                results.Count(item => item.Attribute("outcome")?.Value == "Passed") != passed ||
                results.Count(item => item.Attribute("outcome")?.Value == "NotExecuted") > skipped)
                throw new TestReportException("The TRX result outcomes disagree with its passing counters.");
            return new(total, passed, failed, skipped);
        }
        catch (TestReportException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or XmlException or ArgumentException)
        { throw new TestReportException("The fresh TRX report could not be verified: " + error.Message); }
    }

    private static async Task SaveEvidenceAsync(string evidencePath, TaskVerificationCompletion completion)
    {
        TaskWorkspacePaths.NoLinks(evidencePath);
        foreach (var (name, bytes) in new[]
        {
            ("stdout.txt", Encoding.UTF8.GetBytes(completion.StandardOutput.Text)),
            ("stderr.txt", Encoding.UTF8.GetBytes(completion.StandardError.Text)),
            ("completion.json", JsonSerializer.SerializeToUtf8Bytes(completion))
        })
        {
            var path = Path.Combine(evidencePath, name);
            TaskWorkspacePaths.NoLinks(path);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await output.WriteAsync(bytes).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
    }

    private sealed class TestReportException(string message) : InvalidOperationException(message);

    private sealed class OutputCapture(int maxBytes)
    {
        private readonly byte[] _prefix = new byte[Math.Clamp(maxBytes, 1, 1_048_576)];
        private int _captured;
        private long _count;
        private string? _digest;
        internal Exception? ReadError { get; private set; }
        internal async Task DrainAsync(Stream stream, CancellationToken ct)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(16_384);
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    hash.AppendData(buffer, 0, read);
                    _count = checked(_count + read);
                    var retain = Math.Min(read, _prefix.Length - _captured);
                    buffer.AsSpan(0, retain).CopyTo(_prefix.AsSpan(_captured));
                    _captured += retain;
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or OverflowException)
            { ReadError = error; }
            finally { _digest = "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()); ArrayPool<byte>.Shared.Return(buffer); }
        }

        internal TaskVerificationLog Log()
        {
            var valid = 0;
            while (valid < _captured && Rune.DecodeFromUtf8(_prefix.AsSpan(valid, _captured - valid), out _, out var consumed) == OperationStatus.Done)
                valid += consumed;
            return new(Encoding.UTF8.GetString(_prefix, 0, valid), _digest ?? TaskVerificationIdentity.EmptyLogDigest,
                _count, _count != valid);
        }
    }
}
