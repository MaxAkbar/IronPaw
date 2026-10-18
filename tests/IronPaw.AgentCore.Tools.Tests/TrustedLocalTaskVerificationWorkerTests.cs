using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using IronPaw.AgentCore.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class TrustedLocalTaskVerificationWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IronPawVerificationWorkerTests", Guid.NewGuid().ToString("N"));
    private readonly GitTaskWorkspaceManager _manager = new();
    private static readonly Lazy<byte[]> FixtureBytes = new(CompileFixture);

    [Fact]
    public async Task CommitSnapshotPreservesMultilineBlobWhenCheckoutConversionIsEnabled()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        await GitAsync(source, ["init"]);
        await GitAsync(source, ["config", "user.name", "IronPaw Verification Tests"]);
        await GitAsync(source, ["config", "user.email", "tests@example.invalid"]);
        await GitAsync(source, ["config", "core.autocrlf", "true"]);
        var committed = Encoding.UTF8.GetBytes("first\nsecond\n");
        var sourceFile = Path.Combine(source, "source.txt");
        await File.WriteAllBytesAsync(sourceFile, committed);
        await GitAsync(source, ["add", "source.txt"]);
        await GitAsync(source, ["commit", "-m", "Multiline raw blob"]);
        var commit = await _manager.ResolveCommitAsync(source, "HEAD", CancellationToken.None);
        var dirty = Encoding.UTF8.GetBytes("dirty\r\ncheckout\r\n");
        await File.WriteAllBytesAsync(sourceFile, dirty);
        var binding = new TaskGraphAgentBinding("root", source, commit, Path.Combine(_root, "store"), "test", "test", new(ToolProfile.Coding), new());
        var claim = new TaskGraphClaim("graph", "sha256:graph", new("task", "objective"), "attempt", "lease");
        var workspace = await _manager.OpenOrCreateAsync(binding, claim, CancellationToken.None);
        Assert.Equal(committed, await File.ReadAllBytesAsync(Path.Combine(workspace.WorkspacePath, "source.txt")));
        Assert.Equal(dirty, await File.ReadAllBytesAsync(sourceFile));
    }

    [Fact]
    public async Task AuthorityAndExactApprovedProfileAreRequired()
    {
        var fixture = await FixtureAsync("pass");
        Assert.Throws<UnauthorizedAccessException>(() => new TrustedLocalTaskVerificationWorker([fixture.Job.Profile], TaskVerificationAuthority.None));
        var worker = Worker(fixture.Job.Profile);
        worker.ValidateProfile(fixture.Job.Profile, new("owner", "Owner", true));
        Assert.Throws<UnauthorizedAccessException>(() => worker.ValidateProfile(fixture.Job.Profile, new("other", "Other", false)));
        Assert.Throws<UnauthorizedAccessException>(() => worker.ValidateProfile(fixture.Job.Profile with { Arguments = ["different"] }, new("owner", "Owner", true)));
        Assert.Throws<ArgumentException>(() => Worker(fixture.Job.Profile with { Timeout = TimeSpan.FromDays(2) }));
        Assert.Throws<ArgumentException>(() => Worker(fixture.Job.Profile with { MaxOutputBytes = 1_048_577 }));
    }

    [Fact]
    public async Task SuccessfulCommandUsesFreshCopyAndPreservesOriginalSealedRevision()
    {
        var fixture = await FixtureAsync("pass");
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.Equal(TaskCommandOutcome.Passed, completion.Outcome);
        Assert.Equal(0, completion.ExitCode);
        Assert.Contains("build passed", completion.StandardOutput.Text);
        Assert.True(await _manager.VerifyAsync(fixture.Workspace, fixture.Job.Result, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(fixture.Job.Copy.EvidencePath, "completion.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Job.Copy.EvidencePath, "stdout.txt")));
        Assert.True(File.Exists(Path.Combine(fixture.Job.Copy.WorkspacePath, "started.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Workspace.WorkspacePath, "started.txt")));
        var repeated = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.Equal(TaskCommandOutcome.NeedsReconciliation, repeated.Outcome);
    }

    [Fact]
    public async Task SameInvocationGetsNewDisposableCopyWithoutOverwritingPriorEvidence()
    {
        var fixture = await FixtureAsync("pass");
        await File.WriteAllTextAsync(Path.Combine(fixture.Job.Copy.EvidencePath, "preserve.txt"), "preserve");
        var second = await _manager.CreateVerificationCopyAsync(fixture.Workspace, fixture.Job.Result,
            fixture.Job.Copy.InvocationId, CancellationToken.None);
        Assert.Equal(fixture.Job.Copy.InvocationId, second.InvocationId);
        Assert.Equal(fixture.Job.Copy.InputDigest, second.InputDigest);
        Assert.NotEqual(fixture.Job.Copy.EvidencePath, second.EvidencePath);
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(fixture.Job.Copy.EvidencePath, "preserve.txt")));
        Assert.Equal("committed", await File.ReadAllTextAsync(Path.Combine(second.WorkspacePath, "source.txt")));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("extra")]
    [InlineData("deleted")]
    [InlineData("manifest")]
    public async Task CopyTamperingIsRejectedBeforeProcessLaunch(string kind)
    {
        var fixture = await FixtureAsync("pass");
        if (kind == "changed") await File.WriteAllTextAsync(Path.Combine(fixture.Job.Copy.WorkspacePath, "source.txt"), "changed");
        if (kind == "extra") await File.WriteAllTextAsync(Path.Combine(fixture.Job.Copy.WorkspacePath, "extra.txt"), "extra");
        if (kind == "deleted") File.Delete(Path.Combine(fixture.Job.Copy.WorkspacePath, "source.txt"));
        if (kind == "manifest") await File.AppendAllTextAsync(Path.Combine(fixture.Job.Copy.EvidencePath, "input-manifest.json"), " ");
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.Equal(TaskCommandOutcome.Failed, completion.Outcome);
        Assert.Null(completion.ExitCode);
        Assert.False(File.Exists(Path.Combine(fixture.Job.Copy.WorkspacePath, "started.txt")));
    }

    [Fact]
    public async Task SealedSourceTamperingPreventsCopyCreationAndCommandAdmission()
    {
        var fixture = await FixtureAsync("pass");
        await File.WriteAllTextAsync(fixture.Job.Result.Artifacts.Single().Location, "tampered");
        await Assert.ThrowsAsync<TaskInputConflictException>(() => _manager.CreateVerificationCopyAsync(fixture.Workspace,
            fixture.Job.Result, fixture.Job.Copy.InvocationId, CancellationToken.None));
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.Equal(TaskCommandOutcome.Failed, completion.Outcome);
        Assert.Null(completion.ExitCode);
    }

    [Fact]
    public async Task InvalidCopyPathCannotWriteEvidenceIntoUnrelatedDirectory()
    {
        var fixture = await FixtureAsync("pass");
        var unrelated = Directory.CreateDirectory(Path.Combine(_root, "unrelated")).FullName;
        var malformed = fixture.Job with { Copy = fixture.Job.Copy with
        { EvidencePath = unrelated, WorkspacePath = Path.Combine(unrelated, "workspace") } };
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(malformed, CancellationToken.None);
        Assert.Equal(TaskCommandOutcome.Failed, completion.Outcome);
        Assert.Null(completion.ExitCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(unrelated));
    }

    [Fact]
    public async Task NonzeroExitIsKnownFailureWithCompleteLogs()
    {
        var fixture = await FixtureAsync("fail");
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.Equal(TaskCommandOutcome.Failed, completion.Outcome);
        Assert.Equal(7, completion.ExitCode);
        Assert.Contains("intentional", completion.StandardError.Text);
        Assert.Equal(Digest(Encoding.UTF8.GetBytes(completion.StandardError.Text)), completion.StandardError.Digest);
        Assert.False(completion.StandardError.Truncated);
    }

    [Fact]
    public async Task BothLargeOutputStreamsAreDrainedAndHashedWithBoundedPrefixes()
    {
        var fixture = await FixtureAsync("output", maxOutput: 1_024);
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.Equal(TaskCommandOutcome.Passed, completion.Outcome);
        Assert.Equal(200_000, completion.StandardOutput.ByteCount);
        Assert.Equal(200_000, completion.StandardError.ByteCount);
        Assert.Equal(1_024, Encoding.UTF8.GetByteCount(completion.StandardOutput.Text));
        Assert.Equal(1_024, Encoding.UTF8.GetByteCount(completion.StandardError.Text));
        Assert.True(completion.StandardOutput.Truncated);
        Assert.True(completion.StandardError.Truncated);
        Assert.Equal(Digest(Enumerable.Repeat((byte)'x', 200_000).ToArray()), completion.StandardOutput.Digest);
        Assert.Equal(Digest(Enumerable.Repeat((byte)'y', 200_000).ToArray()), completion.StandardError.Digest);
    }

    [Theory]
    [InlineData("unicode", 5, "😀", 6)]
    [InlineData("invalid", 10, "", 2)]
    public async Task OutputPreservesValidUtf8PrefixAndFullRawDigest(string mode, int maxBytes, string expected, int length)
    {
        var fixture = await FixtureAsync(mode, maxOutput: maxBytes);
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.Equal(TaskCommandOutcome.Passed, completion.Outcome);
        Assert.Equal(expected, completion.StandardOutput.Text);
        Assert.Equal(length, completion.StandardOutput.ByteCount);
        Assert.True(completion.StandardOutput.Truncated);
        Assert.Equal(Digest(mode == "unicode" ? Encoding.UTF8.GetBytes("😀é") : [0xff, 0x01]), completion.StandardOutput.Digest);
    }

    [Theory]
    [InlineData("trx-pass", TaskCommandOutcome.Passed)]
    [InlineData("trx-skipped", TaskCommandOutcome.Passed)]
    [InlineData("trx-zero", TaskCommandOutcome.Failed)]
    [InlineData("trx-fail", TaskCommandOutcome.Failed)]
    [InlineData("trx-inconsistent", TaskCommandOutcome.Failed)]
    [InlineData("trx-missing", TaskCommandOutcome.Failed)]
    [InlineData("trx-dtd", TaskCommandOutcome.Failed)]
    [InlineData("trx-malformed", TaskCommandOutcome.Failed)]
    [InlineData("trx-missing-outcome", TaskCommandOutcome.Failed)]
    [InlineData("trx-too-large", TaskCommandOutcome.Failed)]
    public async Task SuccessfulTestExitStillRequiresFreshNonemptyConsistentTrx(string mode, TaskCommandOutcome expected)
    {
        var fixture = await FixtureAsync(mode, tests: true);
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.Equal(expected, completion.Outcome);
        Assert.Equal(0, completion.ExitCode);
        if (expected == TaskCommandOutcome.Passed) Assert.Equal(1, completion.Tests!.Passed);
        else Assert.Null(completion.Tests);
    }

    [Fact]
    public async Task CandidateSuppliedTestReportCannotPretendToBeFreshExecutionEvidence()
    {
        var fixture = await FixtureAsync("trx-pass", tests: true, preexistingReport: true);
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.Equal(TaskCommandOutcome.Failed, completion.Outcome);
        Assert.Null(completion.ExitCode);
        Assert.False(File.Exists(Path.Combine(fixture.Job.Copy.WorkspacePath, "started.txt")));
    }

    [Fact]
    public async Task ParentCredentialsAndProfileDirectoriesAreNotInherited()
    {
        var old = Environment.GetEnvironmentVariable("IRONPAW_VERIFICATION_SECRET");
        Environment.SetEnvironmentVariable("IRONPAW_VERIFICATION_SECRET", "must-not-inherit");
        try
        {
            var fixture = await FixtureAsync("environment");
            var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
            Assert.Equal(TaskCommandOutcome.Passed, completion.Outcome);
            Assert.StartsWith("absent\n", completion.StandardOutput.Text.Replace("\r\n", "\n"));
            Assert.Contains(Path.Combine(fixture.Job.Copy.EvidencePath, "environment", "home"), completion.StandardOutput.Text);
            Assert.Contains(Path.Combine(fixture.Job.Copy.EvidencePath, "environment", "temp"), completion.StandardOutput.Text);
            Assert.DoesNotContain("must-not-inherit", completion.StandardOutput.Text);
        }
        finally { Environment.SetEnvironmentVariable("IRONPAW_VERIFICATION_SECRET", old); }
    }

    [Fact]
    public async Task ArgumentsAreLiteralAndNeverPassedThroughShellInterpretation()
    {
        const string literal = "hello & echo injected > shell-created.txt ; $(echo unsafe)";
        var fixture = await FixtureAsync("argument", argument: literal);
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.Equal(TaskCommandOutcome.Passed, completion.Outcome);
        Assert.Equal(literal, completion.StandardOutput.Text);
        Assert.False(File.Exists(Path.Combine(fixture.Job.Copy.WorkspacePath, "shell-created.txt")));
    }

    [Fact]
    public async Task ExecutableDigestDriftIsRejectedBeforeLaunch()
    {
        var fixture = await FixtureAsync("pass");
        var profile = fixture.Job.Profile with { ExecutableDigest = "sha256:" + new string('0', 64) };
        var copy = await _manager.CreateVerificationCopyAsync(fixture.Workspace, fixture.Job.Result,
            TaskVerificationIdentity.InvocationId(fixture.Job.Claim, fixture.Job.Result, profile), CancellationToken.None);
        var job = fixture.Job with { Profile = profile, ProfileFingerprint = TaskVerificationIdentity.ProfileFingerprint(profile), Copy = copy };
        var completion = await Worker(profile).ExecuteAsync(job, CancellationToken.None);
        Assert.Equal(TaskCommandOutcome.Failed, completion.Outcome);
        Assert.Null(completion.ExitCode);
        Assert.False(File.Exists(Path.Combine(copy.WorkspacePath, "started.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedStartedProcessRequiresReconciliation(bool callerCancellation)
    {
        var fixture = await FixtureAsync("sleep", timeout: TimeSpan.FromMilliseconds(callerCancellation ? 10_000 : 300));
        using var cancellation = new CancellationTokenSource();
        var execution = Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, cancellation.Token);
        if (callerCancellation)
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(fixture.Job.Copy.WorkspacePath, "started.txt")))
                await Task.Delay(10, wait.Token);
            cancellation.Cancel();
        }
        var completion = await execution.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(TaskCommandOutcome.NeedsReconciliation, completion.Outcome);
        Assert.Contains("detached", completion.Explanation);
        Assert.True(File.Exists(Path.Combine(fixture.Job.Copy.EvidencePath, "completion.json")));
    }

    [Fact]
    public async Task CopyRootJunctionIsRejectedBeforeLaunch()
    {
        var fixture = await FixtureAsync("pass");
        var original = fixture.Job.Copy.WorkspacePath + "-original";
        Directory.Move(fixture.Job.Copy.WorkspacePath, original);
        await GitTaskWorkspaceManagerTests.CreateLinkAsync(fixture.Job.Copy.WorkspacePath, original);
        try
        {
            var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
            Assert.Equal(TaskCommandOutcome.Failed, completion.Outcome);
            Assert.Null(completion.ExitCode);
        }
        finally { Directory.Delete(fixture.Job.Copy.WorkspacePath); }
    }

    private async Task<(TaskVerificationJob Job, TaskWorkspace Workspace)> FixtureAsync(string mode,
        bool tests = false, bool preexistingReport = false, int maxOutput = 65_536, TimeSpan? timeout = null, string? argument = null)
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, "Fixture.dll"), FixtureBytes.Value);
        await File.WriteAllTextAsync(Path.Combine(source, "Fixture.runtimeconfig.json"),
            "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}}}");
        await File.WriteAllTextAsync(Path.Combine(source, "source.txt"), "committed");
        if (preexistingReport) await File.WriteAllTextAsync(Path.Combine(source, "results.trx"), "candidate supplied report");
        await GitAsync(source, ["init"]);
        await GitAsync(source, ["config", "user.name", "IronPaw Verification Tests"]);
        await GitAsync(source, ["config", "user.email", "tests@example.invalid"]);
        await GitAsync(source, ["add", "."]);
        await GitAsync(source, ["commit", "-m", "Controlled verification fixture"]);
        var commit = await _manager.ResolveCommitAsync(source, "HEAD", CancellationToken.None);
        var binding = new TaskGraphAgentBinding("root", source, commit, Path.Combine(_root, "store"), "test", "test", new(ToolProfile.Coding), new());
        var claim = new TaskGraphClaim("graph", "sha256:graph", new("task", "objective"), "attempt", "lease");
        var workspace = await _manager.OpenOrCreateAsync(binding, claim, CancellationToken.None);
        var capture = await _manager.CaptureAsync(workspace, [new("source", "source output", TaskArtifactKind.File, "source.txt")], CancellationToken.None);
        var result = new WorkflowTaskResult(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId,
            WorkflowTaskStatus.AwaitingVerification, capture.Revision, "candidate") { Artifacts = capture.Artifacts };
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnet) || !Path.IsPathFullyQualified(dotnet))
            dotnet = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe") : "/usr/share/dotnet/dotnet";
        var profile = new TaskVerificationProfile("fixture", Path.GetFullPath(dotnet), Digest(await File.ReadAllBytesAsync(dotnet)),
            argument is null ? ["Fixture.dll", mode] : ["Fixture.dll", mode, argument], timeout ?? TimeSpan.FromSeconds(10))
        { Kind = tests ? TaskVerificationKind.Tests : TaskVerificationKind.Build, TestResultsPath = tests ? "results.trx" : null, MaxOutputBytes = maxOutput };
        var copy = await _manager.CreateVerificationCopyAsync(workspace, result, TaskVerificationIdentity.InvocationId(claim, result, profile), CancellationToken.None);
        return (new(claim, result, profile, TaskVerificationIdentity.ProfileFingerprint(profile), copy), workspace);
    }

    private static TrustedLocalTaskVerificationWorker Worker(TaskVerificationProfile profile) => new([profile], TaskVerificationAuthority.TrustedLocalProcess);
    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] CompileFixture()
    {
        const string source = """
            using System;
            using System.IO;
            using System.Text;
            using System.Threading;
            File.WriteAllText("started.txt", "started");
            var mode = args[0];
            if (mode == "fail") { Console.Error.Write("intentional failure"); return 7; }
            if (mode == "sleep") { Thread.Sleep(30_000); return 0; }
            if (mode == "output") {
                Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes(new string('x', 200_000)));
                Console.OpenStandardError().Write(Encoding.UTF8.GetBytes(new string('y', 200_000))); return 0;
            }
            if (mode == "unicode") { Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes("😀é")); return 0; }
            if (mode == "invalid") { Console.OpenStandardOutput().Write(new byte[] { 0xff, 0x01 }); return 0; }
            if (mode == "argument") { Console.Write(args[1]); return 0; }
            if (mode == "environment") {
                Console.WriteLine(Environment.GetEnvironmentVariable("IRONPAW_VERIFICATION_SECRET") ?? "absent");
                Console.WriteLine(Environment.GetEnvironmentVariable("USERPROFILE"));
                Console.WriteLine(Environment.GetEnvironmentVariable("TEMP")); return 0;
            }
            if (mode.StartsWith("trx-")) {
                if (mode == "trx-missing") return 0;
                if (mode == "trx-malformed") { File.WriteAllText("results.trx", "<broken>"); return 0; }
                if (mode == "trx-too-large") { File.WriteAllText("results.trx", new string('x', 1_048_577)); return 0; }
                if (mode == "trx-dtd") { File.WriteAllText("results.trx", "<!DOCTYPE x [<!ENTITY secret SYSTEM 'file:///secret'>]><x>&secret;</x>"); return 0; }
                var total = mode == "trx-zero" ? 0 : mode == "trx-skipped" ? 2 : 1;
                var passed = mode == "trx-zero" || mode == "trx-fail" ? 0 : 1;
                var failed = mode == "trx-fail" ? 1 : 0;
                var skipped = mode == "trx-skipped" ? 1 : 0;
                var outcome = mode == "trx-fail" || mode == "trx-inconsistent" ? "Failed" : "Passed";
                var results = total == 0 ? "" : "<UnitTestResult outcome=\"" + outcome + "\" />";
                File.WriteAllText("results.trx", "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>" + results +
                    "</Results><ResultSummary" + (mode == "trx-missing-outcome" ? "" : " outcome=\"Completed\"") + "><Counters total=\"" + total + "\" executed=\"" + (passed + failed) + "\" passed=\"" + passed +
                    "\" failed=\"" + failed + "\" notExecuted=\"" + skipped + "\" /></ResultSummary></TestRun>"); return 0;
            }
            Console.Write("build passed"); return 0;
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("IronPawControlledVerificationFixture", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return output.ToArray();
    }

    private static async Task GitAsync(string root, string[] arguments)
    {
        var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-C"); info.ArgumentList.Add(root);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        Assert.True(process.ExitCode == 0, await error);
    }

    public void Dispose()
    {
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IronPawVerificationWorkerTests"));
        if (!Path.GetFullPath(_root).StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its private directory.");
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
