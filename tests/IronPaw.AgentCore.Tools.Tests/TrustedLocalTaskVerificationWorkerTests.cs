using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using IronPaw.AgentCore.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IronPaw.AgentCore.Tools.Tests;

[TestClass]
public sealed class TrustedLocalTaskVerificationWorkerTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IronPawVerificationWorkerTests", Guid.NewGuid().ToString("N"));
    private readonly GitTaskWorkspaceManager _manager = new();
    private static readonly Lazy<byte[]> FixtureBytes = new(CompileFixture);

    [TestMethod]
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
        Assert.AreSequenceEqual(committed, await File.ReadAllBytesAsync(Path.Combine(workspace.WorkspacePath, "source.txt")));
        Assert.AreSequenceEqual(dirty, await File.ReadAllBytesAsync(sourceFile));
    }

    [TestMethod]
    public async Task AuthorityAndExactApprovedProfileAreRequired()
    {
        var fixture = await FixtureAsync("pass");
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => new TrustedLocalTaskVerificationWorker([fixture.Job.Profile], TaskVerificationAuthority.None));
        var worker = Worker(fixture.Job.Profile);
        worker.ValidateProfile(fixture.Job.Profile, new("owner", "Owner", true));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => worker.ValidateProfile(fixture.Job.Profile, new("other", "Other", false)));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => worker.ValidateProfile(fixture.Job.Profile with { Arguments = ["different"] }, new("owner", "Owner", true)));
        Assert.ThrowsExactly<ArgumentException>(() => Worker(fixture.Job.Profile with { Timeout = TimeSpan.FromDays(2) }));
        Assert.ThrowsExactly<ArgumentException>(() => Worker(fixture.Job.Profile with { MaxOutputBytes = 1_048_577 }));
    }

    [TestMethod]
    public async Task SuccessfulCommandUsesFreshCopyAndPreservesOriginalSealedRevision()
    {
        var fixture = await FixtureAsync("pass");
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.AreEqual(TaskCommandOutcome.Passed, completion.Outcome);
        Assert.AreEqual(0, completion.ExitCode);
        Assert.Contains("build passed", completion.StandardOutput.Text);
        Assert.IsTrue(await _manager.VerifyAsync(fixture.Workspace, fixture.Job.Result, CancellationToken.None));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Job.Copy.EvidencePath, "completion.json")));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Job.Copy.EvidencePath, "stdout.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Job.Copy.WorkspacePath, "started.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Workspace.WorkspacePath, "started.txt")));
        var repeated = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.AreEqual(TaskCommandOutcome.NeedsReconciliation, repeated.Outcome);
    }

    [TestMethod]
    public async Task SameInvocationGetsNewDisposableCopyWithoutOverwritingPriorEvidence()
    {
        var fixture = await FixtureAsync("pass");
        await File.WriteAllTextAsync(Path.Combine(fixture.Job.Copy.EvidencePath, "preserve.txt"), "preserve");
        var second = await _manager.CreateVerificationCopyAsync(fixture.Workspace, fixture.Job.Result,
            fixture.Job.Copy.InvocationId, CancellationToken.None);
        Assert.AreEqual(fixture.Job.Copy.InvocationId, second.InvocationId);
        Assert.AreEqual(fixture.Job.Copy.InputDigest, second.InputDigest);
        Assert.AreNotEqual(fixture.Job.Copy.EvidencePath, second.EvidencePath);
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(Path.Combine(fixture.Job.Copy.EvidencePath, "preserve.txt")));
        Assert.AreEqual("committed", await File.ReadAllTextAsync(Path.Combine(second.WorkspacePath, "source.txt")));
    }

    [TestMethod]
    [DataRow("changed")]
    [DataRow("extra")]
    [DataRow("deleted")]
    [DataRow("manifest")]
    public async Task CopyTamperingIsRejectedBeforeProcessLaunch(string kind)
    {
        var fixture = await FixtureAsync("pass");
        if (kind == "changed") await File.WriteAllTextAsync(Path.Combine(fixture.Job.Copy.WorkspacePath, "source.txt"), "changed");
        if (kind == "extra") await File.WriteAllTextAsync(Path.Combine(fixture.Job.Copy.WorkspacePath, "extra.txt"), "extra");
        if (kind == "deleted") File.Delete(Path.Combine(fixture.Job.Copy.WorkspacePath, "source.txt"));
        if (kind == "manifest") await File.AppendAllTextAsync(Path.Combine(fixture.Job.Copy.EvidencePath, "input-manifest.json"), " ");
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.AreEqual(TaskCommandOutcome.Failed, completion.Outcome);
        Assert.IsNull(completion.ExitCode);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Job.Copy.WorkspacePath, "started.txt")));
    }

    [TestMethod]
    public async Task SealedSourceTamperingPreventsCopyCreationAndCommandAdmission()
    {
        var fixture = await FixtureAsync("pass");
        await File.WriteAllTextAsync(fixture.Job.Result.Artifacts.Single().Location, "tampered");
        await Assert.ThrowsExactlyAsync<TaskInputConflictException>(() => _manager.CreateVerificationCopyAsync(fixture.Workspace,
            fixture.Job.Result, fixture.Job.Copy.InvocationId, CancellationToken.None));
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.AreEqual(TaskCommandOutcome.Failed, completion.Outcome);
        Assert.IsNull(completion.ExitCode);
    }

    [TestMethod]
    public async Task InvalidCopyPathCannotWriteEvidenceIntoUnrelatedDirectory()
    {
        var fixture = await FixtureAsync("pass");
        var unrelated = Directory.CreateDirectory(Path.Combine(_root, "unrelated")).FullName;
        var malformed = fixture.Job with
        {
            Copy = fixture.Job.Copy with
            { EvidencePath = unrelated, WorkspacePath = Path.Combine(unrelated, "workspace") }
        };
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(malformed, CancellationToken.None);
        Assert.AreEqual(TaskCommandOutcome.Failed, completion.Outcome);
        Assert.IsNull(completion.ExitCode);
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(unrelated));
    }

    [TestMethod]
    public async Task NonzeroExitIsKnownFailureWithCompleteLogs()
    {
        var fixture = await FixtureAsync("fail");
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.AreEqual(TaskCommandOutcome.Failed, completion.Outcome);
        Assert.AreEqual(7, completion.ExitCode);
        Assert.Contains("intentional", completion.StandardError.Text);
        Assert.AreEqual(Digest(Encoding.UTF8.GetBytes(completion.StandardError.Text)), completion.StandardError.Digest);
        Assert.IsFalse(completion.StandardError.Truncated);
    }

    [TestMethod]
    public async Task BothLargeOutputStreamsAreDrainedAndHashedWithBoundedPrefixes()
    {
        var fixture = await FixtureAsync("output", maxOutput: 1_024);
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.AreEqual(TaskCommandOutcome.Passed, completion.Outcome);
        Assert.AreEqual(200_000, completion.StandardOutput.ByteCount);
        Assert.AreEqual(200_000, completion.StandardError.ByteCount);
        Assert.AreEqual(1_024, Encoding.UTF8.GetByteCount(completion.StandardOutput.Text));
        Assert.AreEqual(1_024, Encoding.UTF8.GetByteCount(completion.StandardError.Text));
        Assert.IsTrue(completion.StandardOutput.Truncated);
        Assert.IsTrue(completion.StandardError.Truncated);
        Assert.AreEqual(Digest(Enumerable.Repeat((byte)'x', 200_000).ToArray()), completion.StandardOutput.Digest);
        Assert.AreEqual(Digest(Enumerable.Repeat((byte)'y', 200_000).ToArray()), completion.StandardError.Digest);
    }

    [TestMethod]
    [DataRow("unicode", 5, "😀", 6)]
    [DataRow("invalid", 10, "", 2)]
    public async Task OutputPreservesValidUtf8PrefixAndFullRawDigest(string mode, int maxBytes, string expected, int length)
    {
        var fixture = await FixtureAsync(mode, maxOutput: maxBytes);
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.AreEqual(TaskCommandOutcome.Passed, completion.Outcome);
        Assert.AreEqual(expected, completion.StandardOutput.Text);
        Assert.AreEqual(length, completion.StandardOutput.ByteCount);
        Assert.IsTrue(completion.StandardOutput.Truncated);
        Assert.AreEqual(Digest(mode == "unicode" ? Encoding.UTF8.GetBytes("😀é") : [0xff, 0x01]), completion.StandardOutput.Digest);
    }

    [TestMethod]
    [DataRow("trx-pass", TaskCommandOutcome.Passed)]
    [DataRow("trx-skipped", TaskCommandOutcome.Passed)]
    [DataRow("trx-zero", TaskCommandOutcome.Failed)]
    [DataRow("trx-fail", TaskCommandOutcome.Failed)]
    [DataRow("trx-inconsistent", TaskCommandOutcome.Failed)]
    [DataRow("trx-missing", TaskCommandOutcome.Failed)]
    [DataRow("trx-dtd", TaskCommandOutcome.Failed)]
    [DataRow("trx-malformed", TaskCommandOutcome.Failed)]
    [DataRow("trx-missing-outcome", TaskCommandOutcome.Failed)]
    [DataRow("trx-too-large", TaskCommandOutcome.Failed)]
    public async Task SuccessfulTestExitStillRequiresFreshNonemptyConsistentTrx(string mode, TaskCommandOutcome expected)
    {
        var fixture = await FixtureAsync(mode, tests: true);
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.AreEqual(expected, completion.Outcome);
        Assert.AreEqual(0, completion.ExitCode);
        if (expected == TaskCommandOutcome.Passed) Assert.AreEqual(1, completion.Tests!.Passed);
        else Assert.IsNull(completion.Tests);
    }

    [TestMethod]
    public async Task CandidateSuppliedTestReportCannotPretendToBeFreshExecutionEvidence()
    {
        var fixture = await FixtureAsync("trx-pass", tests: true, preexistingReport: true);
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.AreEqual(TaskCommandOutcome.Failed, completion.Outcome);
        Assert.IsNull(completion.ExitCode);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Job.Copy.WorkspacePath, "started.txt")));
    }

    [TestMethod]
    public async Task ParentCredentialsAndProfileDirectoriesAreNotInherited()
    {
        var old = Environment.GetEnvironmentVariable("IRONPAW_VERIFICATION_SECRET");
        Environment.SetEnvironmentVariable("IRONPAW_VERIFICATION_SECRET", "must-not-inherit");
        try
        {
            var fixture = await FixtureAsync("environment");
            var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
            Assert.AreEqual(TaskCommandOutcome.Passed, completion.Outcome);
            Assert.StartsWith("absent\n", completion.StandardOutput.Text.Replace("\r\n", "\n"));
            Assert.Contains(Path.Combine(fixture.Job.Copy.EvidencePath, "environment", "home"), completion.StandardOutput.Text);
            Assert.Contains(Path.Combine(fixture.Job.Copy.EvidencePath, "environment", "temp"), completion.StandardOutput.Text);
            Assert.DoesNotContain("must-not-inherit", completion.StandardOutput.Text);
        }
        finally { Environment.SetEnvironmentVariable("IRONPAW_VERIFICATION_SECRET", old); }
    }

    [TestMethod]
    public async Task ArgumentsAreLiteralAndNeverPassedThroughShellInterpretation()
    {
        const string literal = "hello & echo injected > shell-created.txt ; $(echo unsafe)";
        var fixture = await FixtureAsync("argument", argument: literal);
        var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
        Assert.AreEqual(TaskCommandOutcome.Passed, completion.Outcome);
        Assert.AreEqual(literal, completion.StandardOutput.Text);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Job.Copy.WorkspacePath, "shell-created.txt")));
    }

    [TestMethod]
    public async Task ExecutableDigestDriftIsRejectedBeforeLaunch()
    {
        var fixture = await FixtureAsync("pass");
        var profile = fixture.Job.Profile with { ExecutableDigest = "sha256:" + new string('0', 64) };
        var copy = await _manager.CreateVerificationCopyAsync(fixture.Workspace, fixture.Job.Result,
            TaskVerificationIdentity.InvocationId(fixture.Job.Claim, fixture.Job.Result, profile), CancellationToken.None);
        var job = fixture.Job with { Profile = profile, ProfileFingerprint = TaskVerificationIdentity.ProfileFingerprint(profile), Copy = copy };
        var completion = await Worker(profile).ExecuteAsync(job, CancellationToken.None);
        Assert.AreEqual(TaskCommandOutcome.Failed, completion.Outcome);
        Assert.IsNull(completion.ExitCode);
        Assert.IsFalse(File.Exists(Path.Combine(copy.WorkspacePath, "started.txt")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
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
        Assert.AreEqual(TaskCommandOutcome.NeedsReconciliation, completion.Outcome);
        Assert.Contains("detached", completion.Explanation);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Job.Copy.EvidencePath, "completion.json")));
    }

    [TestMethod]
    public async Task CopyRootJunctionIsRejectedBeforeLaunch()
    {
        var fixture = await FixtureAsync("pass");
        var original = fixture.Job.Copy.WorkspacePath + "-original";
        Directory.Move(fixture.Job.Copy.WorkspacePath, original);
        await GitTaskWorkspaceManagerTests.CreateLinkAsync(fixture.Job.Copy.WorkspacePath, original);
        try
        {
            var completion = await Worker(fixture.Job.Profile).ExecuteAsync(fixture.Job, CancellationToken.None);
            Assert.AreEqual(TaskCommandOutcome.Failed, completion.Outcome);
            Assert.IsNull(completion.ExitCode);
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
            WorkflowTaskStatus.AwaitingVerification, capture.Revision, "candidate")
        { Artifacts = capture.Artifacts };
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
        Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return output.ToArray();
    }

    private static async Task GitAsync(string root, string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-C"); info.ArgumentList.Add(root);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        Assert.IsTrue(process.ExitCode == 0, await error);
    }

    [TestCleanup]
    public void Cleanup()
    {
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IronPawVerificationWorkerTests"));
        if (!Path.GetFullPath(_root).StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its private directory.");
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
