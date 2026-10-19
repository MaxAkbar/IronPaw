using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class TaskCommandVerificationIntegrationTests
{
    private static readonly CallerIdentity Owner = new("verification-owner", "Verification owner", true);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [TestMethod]
    public async Task MixedIntegrityAndCommandCriteria_AcceptOnlyWithBoundReceipts_AndNeverReplayCompletedWork()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph();
        var verificationSpans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ExecutionDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = span =>
            {
                if (span.OperationName == "ironpaw.verification" && Equals(span.GetTagItem("ironpaw.graph.id"), graph.GraphId)) verificationSpans.Enqueue(span);
            }
        };
        ActivitySource.AddActivityListener(listener);
        var client = new WriterClient();
        var worker = new Worker((job, _) =>
        {
            Assert.AreNotEqual(Path.GetDirectoryName(job.Result.Artifacts[0].Location), job.Copy.WorkspacePath);
            Assert.AreEqual("verified candidate", File.ReadAllText(Path.Combine(job.Copy.WorkspacePath, "result.txt")));
            return Task.FromResult(Completion(TaskCommandOutcome.Passed));
        });
        var completed = await scope.Runner(client, worker).StartAsync(graph, Owner, 1, scope.Options(), Ct);

        Assert.AreEqual(TaskGraphRunStatus.Succeeded, TaskGraphProgress.Inspect(completed).Status);
        var state = Assert.ContainsSingle(completed.Tasks);
        Assert.AreEqual(TaskAcceptanceOutcome.Accepted, state.Acceptance!.Outcome);
        var receipt = Assert.ContainsSingle(state.Verifications);
        Assert.AreEqual(TaskVerificationInvocationState.Completed, receipt.State);
        Assert.AreEqual(TaskCommandOutcome.Passed, receipt.Completion!.Outcome);
        Assert.AreEqual(state.Result!.Revision, receipt.Revision);
        Assert.AreEqual(TaskInputIdentity.ResultDigest(state.Result), receipt.ResultDigest);
        Assert.AreEqual(TaskVerificationIdentity.ProfileFingerprint(completed.AgentBinding!.VerificationProfiles[0]), receipt.ProfileFingerprint);
        Assert.AreEqual(2, client.Calls);
        Assert.ContainsSingle(worker.Jobs);
        var verificationSpan = Assert.ContainsSingle(verificationSpans);
        Assert.AreEqual(receipt.InvocationId, verificationSpan.GetTagItem("ironpaw.verification.invocation_id"));
        Assert.AreEqual(receipt.Revision, verificationSpan.GetTagItem("ironpaw.revision"));
        Assert.AreEqual("completed", verificationSpan.GetTagItem("ironpaw.outcome"));
        await scope.ReopenAsync();

        var replayClient = new WriterClient();
        var replayWorker = new Worker((_, _) => throw new InvalidOperationException("Durable verification was repeated."));
        var replay = await scope.Runner(replayClient, replayWorker).ResumeAsync(graph.GraphId, Owner, Ct);
        Assert.AreEqual(TaskGraphRunStatus.Succeeded, TaskGraphProgress.Inspect(replay).Status);
        Assert.AreEqual(0, replayClient.Calls);
        Assert.IsEmpty(replayWorker.Jobs);
        Assert.ContainsSingle(verificationSpans);
        Assert.AreEqual(receipt, Assert.ContainsSingle(replay.Tasks[0].Verifications));
    }

    [TestMethod]
    public async Task FailedCommand_RejectsCandidate_AndPreservesCommandEvidence()
    {
        await using var scope = await Scope.OpenAsync();
        var worker = new Worker((_, _) => Task.FromResult(Completion(TaskCommandOutcome.Failed, exitCode: 7)));

        var rejected = await scope.Runner(new WriterClient(), worker).StartAsync(Graph(), Owner, 1, scope.Options(), Ct);

        var state = Assert.ContainsSingle(rejected.Tasks);
        Assert.AreEqual(TaskGraphRunStatus.Failed, TaskGraphProgress.Inspect(rejected).Status);
        Assert.AreEqual(TaskAcceptanceOutcome.Rejected, state.Acceptance!.Outcome);
        Assert.AreEqual(7, Assert.ContainsSingle(state.Verifications).Completion!.ExitCode);
        Assert.AreEqual("verified candidate", await File.ReadAllTextAsync(Assert.ContainsSingle(state.Result!.Artifacts).Location));
    }

    [TestMethod]
    public async Task UnknownCommand_RequiresExplicitReconciliation_ResumeDoesNotRerunProcessOrProvider()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph();
        var client = new WriterClient();
        var worker = new Worker((_, _) => Task.FromResult(Completion(TaskCommandOutcome.NeedsReconciliation, exitCode: null)));
        var runner = scope.Runner(client, worker);
        var unknown = await runner.StartAsync(graph, Owner, 1, scope.Options(), Ct);
        var receipt = Assert.ContainsSingle(Assert.ContainsSingle(unknown.Tasks).Verifications);
        Assert.AreEqual(TaskVerificationInvocationState.NeedsReconciliation, receipt.State);
        Assert.AreEqual(TaskGraphRunStatus.NeedsReconciliation, TaskGraphProgress.Inspect(unknown).Status);

        var resumed = await runner.ResumeAsync(graph.GraphId, Owner, Ct);
        Assert.AreEqual(TaskGraphRunStatus.NeedsReconciliation, TaskGraphProgress.Inspect(resumed).Status);
        Assert.ContainsSingle(worker.Jobs);
        Assert.AreEqual(2, client.Calls);

        await runner.ReconcileVerificationAsync(graph.GraphId, "task", receipt.InvocationId,
            Completion(TaskCommandOutcome.Passed), "Inspected completed process output and exit status.", Owner, Ct);
        var completed = await runner.ResumeAsync(graph.GraphId, Owner, Ct);
        Assert.AreEqual(TaskGraphRunStatus.Succeeded, TaskGraphProgress.Inspect(completed).Status);
        Assert.ContainsSingle(worker.Jobs);
        Assert.AreEqual(2, client.Calls);
        Assert.AreEqual("Inspected completed process output and exit status.", Assert.ContainsSingle(completed.Tasks[0].Verifications).ReconciliationNote);
    }

    [TestMethod]
    public async Task ReopenedStartedInvocation_IsUnknown_AndNeverExecutesAutomatically()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph();
        var worker = new Worker((_, _) => throw new InvalidOperationException("Process outcome was lost."));
        var interrupted = await scope.Runner(new WriterClient(), worker).StartAsync(graph, Owner, 1, scope.Options(), Ct);
        var invocationId = Assert.ContainsSingle(Assert.ContainsSingle(interrupted.Tasks).Verifications).InvocationId;
        await scope.ReopenAsync();
        var unusedClient = new WriterClient();
        var unusedWorker = new Worker((_, _) => throw new InvalidOperationException("Unknown process was repeated."));

        var resumed = await scope.Runner(unusedClient, unusedWorker).ResumeAsync(graph.GraphId, Owner, Ct);

        Assert.AreEqual(TaskGraphRunStatus.NeedsReconciliation, TaskGraphProgress.Inspect(resumed).Status);
        var invocation = Assert.ContainsSingle(Assert.ContainsSingle(resumed.Tasks).Verifications);
        Assert.AreEqual(invocationId, invocation.InvocationId);
        Assert.AreEqual(TaskVerificationInvocationState.NeedsReconciliation, invocation.State);
        Assert.AreEqual(0, unusedClient.Calls);
        Assert.IsEmpty(unusedWorker.Jobs);
    }

    [TestMethod]
    public async Task ChangedAcceptedProfile_IsRejectedBeforeEitherProviderOrWorkerRunsAgain()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph();
        var options = scope.Options();
        var first = new Worker((_, _) => Task.FromResult(Completion(TaskCommandOutcome.Passed)));
        await scope.Runner(new WriterClient(), first).StartAsync(graph, Owner, 1, options, Ct);
        var client = new WriterClient();
        var worker = new Worker((_, _) => throw new InvalidOperationException("Changed binding executed."));

        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => scope.Runner(client, worker).StartAsync(graph, Owner, 1,
            options with { VerificationProfiles = [options.VerificationProfiles[0] with { Arguments = ["--info"] }] }, Ct));

        Assert.AreEqual(0, client.Calls);
        Assert.IsEmpty(worker.Jobs);
        Assert.ContainsSingle((await scope.Store.GetTaskGraphAsync(graph.GraphId, Ct))!.Tasks[0].Verifications);
    }

    [TestMethod]
    public async Task MissingWorker_RejectsNewGraphBeforeAcceptance()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph();
        var client = new WriterClient();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scope.Runner(client).StartAsync(graph, Owner, 1, scope.Options(), Ct));

        Assert.IsNull(await scope.Store.GetTaskGraphAsync(graph.GraphId, Ct));
        Assert.IsEmpty(await scope.Store.ListAsync(Ct));
        Assert.AreEqual(0, client.Calls);
    }

    [TestMethod]
    public async Task MissingWorker_CanReadExistingUnknownCommandWithoutAdmittingFurtherWork()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph();
        var worker = new Worker((_, _) => Task.FromResult(Completion(TaskCommandOutcome.NeedsReconciliation, exitCode: null)));
        await scope.Runner(new WriterClient(), worker).StartAsync(graph, Owner, 1, scope.Options(), Ct);
        await scope.ReopenAsync();
        var client = new WriterClient();

        var unchanged = await scope.Runner(client).ResumeAsync(graph.GraphId, Owner, Ct);

        Assert.AreEqual(0, client.Calls);
        Assert.AreEqual(TaskGraphRunStatus.NeedsReconciliation, TaskGraphProgress.Inspect(unchanged).Status);
        Assert.ContainsSingle(worker.Jobs);
        Assert.AreEqual(TaskVerificationInvocationState.NeedsReconciliation, Assert.ContainsSingle(unchanged.Tasks[0].Verifications).State);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GraphControl_DoesNotDiscardAWorkerCompletionThatArrivesAfterCancellation(bool permanent)
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Worker(async (_, _) =>
        {
            waiting.TrySetResult();
            await release.Task;
            return Completion(TaskCommandOutcome.Passed);
        });
        var runner = scope.Runner(new WriterClient(), worker);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var active = runner.StartAsync(graph, Owner, 1, scope.Options(), lifetime.Token);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (permanent) await runner.CancelAsync(graph.GraphId, Owner, Ct);
            else await runner.PauseAsync(graph.GraphId, Owner, Ct);
            release.TrySetResult();
            var stopped = await active.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.AreEqual(permanent ? WorkflowControl.Cancel : WorkflowControl.Pause, stopped.Control);
            var receipt = Assert.ContainsSingle(Assert.ContainsSingle(stopped.Tasks).Verifications);
            Assert.AreEqual(TaskVerificationInvocationState.Completed, receipt.State);
            Assert.AreEqual(TaskCommandOutcome.Passed, receipt.Completion!.Outcome);
            Assert.ContainsSingle(worker.Jobs);
        }
        finally
        {
            release.TrySetResult();
            await lifetime.CancelAsync();
            try { await active; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task KnownWorkerReceipt_SurvivesSharedExecutionBudgetOvershoot()
    {
        var clock = new ManualTimeProvider();
        await using var scope = await Scope.OpenAsync(clock: clock);
        var graph = Graph();
        var worker = new Worker((_, _) =>
        {
            clock.Advance(TimeSpan.FromHours(2));
            return Task.FromResult(Completion(TaskCommandOutcome.Passed));
        });
        var completed = await scope.Runner(new WriterClient(), worker).StartAsync(graph, Owner, 1,
            scope.Options() with { SharedBudget = new(20, 20, 100_000, TimeSpan.FromHours(1)) }, Ct);

        Assert.AreEqual(TaskGraphRunStatus.BudgetExhausted, TaskGraphProgress.Inspect(completed).Status);
        var receipt = Assert.ContainsSingle(Assert.ContainsSingle(completed.Tasks).Verifications);
        Assert.AreEqual(TaskVerificationInvocationState.Completed, receipt.State);
        Assert.AreEqual(TaskCommandOutcome.Passed, receipt.Completion!.Outcome);
        Assert.IsTrue((await scope.Store.GetBudgetAsync(new RunId(completed.AgentBinding!.RootRunId), Ct)).ExecutionTime >= TimeSpan.FromHours(2));
    }

    [TestMethod]
    public async Task PassingWorkerCannotAcceptASealedSourceChangedDuringVerification()
    {
        await using var scope = await Scope.OpenAsync();
        var worker = new Worker(async (job, _) =>
        {
            var sealedArtifact = Assert.ContainsSingle(job.Result.Artifacts).Location;
            File.SetAttributes(sealedArtifact, FileAttributes.Normal);
            await File.WriteAllTextAsync(sealedArtifact, "tampered after command admission");
            return Completion(TaskCommandOutcome.Passed);
        });

        var rejected = await scope.Runner(new WriterClient(), worker).StartAsync(Graph(), Owner, 1, scope.Options(), Ct);

        var state = Assert.ContainsSingle(rejected.Tasks);
        Assert.AreEqual(TaskGraphRunStatus.Failed, TaskGraphProgress.Inspect(rejected).Status);
        Assert.AreEqual(TaskAcceptanceOutcome.Rejected, state.Acceptance!.Outcome);
        Assert.AreEqual(TaskCommandOutcome.Passed, Assert.ContainsSingle(state.Verifications).Completion!.Outcome);
        Assert.AreEqual("committed source", await File.ReadAllTextAsync(Path.Combine(scope.Source, "source.txt")));
    }

    [TestMethod]
    public async Task TrustedLocalWorker_CompilesAndRunsControlledCombinedCandidate_WithoutTouchingSource()
    {
        await using var scope = await Scope.OpenAsync(withProject: true);
        var graph = Graph(includeTests: true);
        var options = scope.Options(includeTests: true);
        var worker = new TrustedLocalTaskVerificationWorker(options.VerificationProfiles, TaskVerificationAuthority.TrustedLocalProcess);

        var completed = await scope.Runner(new WriterClient(combinedCandidate: true), worker).StartAsync(graph, Owner, 1, options, Ct);

        Assert.IsTrue(TaskGraphProgress.Inspect(completed).Status == TaskGraphRunStatus.Succeeded, JsonSerializer.Serialize(completed.Tasks.Select(task => new { task.TaskId, task.Status, task.Error, task.Result, task.Acceptance, task.Verifications }), new JsonSerializerOptions { WriteIndented = true }));
        var state = Assert.ContainsSingle(task => task.TaskId == "task", completed.Tasks);
        var precursor = Assert.ContainsSingle(task => task.TaskId == "precursor", completed.Tasks);
        Assert.AreEqual(TaskGraphTaskStatus.Succeeded, precursor.Status);
        Assert.AreEqual(precursor.Result!.Revision, Assert.ContainsSingle(state.Inputs!.Dependencies).Revision);
        Assert.AreEqual(2, state.Verifications.Length);
        foreach (var invocation in state.Verifications)
        {
            Assert.AreEqual(TaskCommandOutcome.Passed, invocation.Completion!.Outcome);
        }
        var testReceipt = Assert.ContainsSingle(invocation => invocation.ProfileId == "tests", state.Verifications);
        Assert.AreEqual(new TaskVerificationTestSummary(1, 1, 0, 0), testReceipt.Completion!.Tests);
        Assert.Contains("CONTROLLED_TEST_PASS", testReceipt.Completion.StandardOutput.Text, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(Path.Combine(scope.Source, "bin")));
        Assert.IsFalse(Directory.Exists(Path.Combine(scope.Source, "obj")));
        Assert.IsFalse(File.Exists(Path.Combine(scope.Source, "result.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(scope.Source, "checked.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(scope.Source, "results.trx")));
    }

    private static WorkflowTaskGraph Graph(bool includeTests = false)
    {
        var task = new WorkflowTaskDefinition("task", "Produce the controlled candidate")
        {
            ToolPolicy = new(ToolProfile.Coding),
            Dependencies = includeTests ? ["precursor"] : [],
            Resources = new() { ReadPaths = ["."], WritePaths = includeTests ? ["checked.txt"] : ["result.txt"] },
            ExpectedArtifacts = [new("output", "Candidate output", TaskArtifactKind.File, includeTests ? "checked.txt" : "result.txt")],
            AcceptanceCriteria = includeTests
                ? [new("integrity", "Output is bound to the sealed candidate") { ArtifactIds = ["output"] },
                    new("build", "Candidate compiles"), new("tests", "Controlled candidate assertions pass")]
                : [new("integrity", "Output is bound to the sealed candidate") { ArtifactIds = ["output"] }, new("build", "Candidate compiles")]
        };
        var precursor = new WorkflowTaskDefinition("precursor", "Produce precursor input")
        {
            ToolPolicy = new(ToolProfile.Coding),
            Resources = new() { ReadPaths = ["."], WritePaths = ["result.txt"] },
            ExpectedArtifacts = [new("output", "Precursor output", TaskArtifactKind.File, "result.txt")],
            AcceptanceCriteria = [new("integrity", "Output is bound to the sealed precursor") { ArtifactIds = ["output"] }]
        };
        return new("verification-" + Guid.NewGuid().ToString("n"), "Verify candidate through trusted commands", includeTests ? [precursor, task] : [task]);
    }

    private static TaskVerificationCompletion Completion(TaskCommandOutcome outcome, int? exitCode = 0) =>
        new(outcome, "controlled-worker/v1", "Controlled command outcome.", exitCode,
            new("", TaskVerificationIdentity.EmptyLogDigest, 0, false), new("", TaskVerificationIdentity.EmptyLogDigest, 0, false));

    private sealed class Worker(Func<TaskVerificationJob, CancellationToken, Task<TaskVerificationCompletion>> execute) : ITaskVerificationWorker
    {
        public ConcurrentQueue<TaskVerificationJob> Jobs { get; } = new();
        public void ValidateProfile(TaskVerificationProfile profile, CallerIdentity caller) => Assert.IsTrue(caller.IsOwner);
        public Task<TaskVerificationCompletion> ExecuteAsync(TaskVerificationJob job, CancellationToken ct)
        {
            Jobs.Enqueue(job);
            return execute(job, ct);
        }
    }

    private sealed class WriterClient(bool combinedCandidate = false) : ILlmClient
    {
        private readonly ConcurrentDictionary<SessionKey, int> turns = new();
        public int Calls;
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Calls);
            var turn = turns.AddOrUpdate(request.SessionKey, 1, (_, count) => count + 1);
            var isPrecursor = request.Messages.First(message => message.Role == ChatRole.User).Content.StartsWith("Produce precursor input", StringComparison.Ordinal);
            if (turn == 1)
                yield return new ToolCallDelta("write-result", "write_file", JsonSerializer.SerializeToElement(new { path = combinedCandidate && !isPrecursor ? "checked.txt" : "result.txt", content = "verified candidate" }));
            else yield return new AssistantTextDelta("Produced the candidate; host verification is required.");
            yield return new UsageDelta(7, 3, 0, 0);
            yield return new CompletedDelta();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }

    private sealed class Scope : IAsyncDisposable
    {
        private static readonly string TestBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IronPawCommandIntegration"));
        public string Root { get; } = Path.Combine(TestBase, Guid.NewGuid().ToString("n"));
        public string Source => Path.Combine(Root, "source");
        public string Managed => Path.Combine(Root, "managed");
        private string DatabasePath => Path.Combine(Root, "workflows.cdb");
        private readonly TimeProvider? clock;
        public CSharpDbWorkflowStore Store { get; private set; } = null!;
        public GitTaskWorkspaceManager Manager { get; } = new();
        private string DotNetPath { get; } = FindDotNet();
        private Scope(TimeProvider? clock) => this.clock = clock;
        public static async Task<Scope> OpenAsync(bool withProject = false, TimeProvider? clock = null)
        {
            var scope = new Scope(clock);
            Directory.CreateDirectory(scope.Source);
            await GitAsync(scope.Source, "init");
            await GitAsync(scope.Source, "config", "user.name", "IronPaw Controlled Tests");
            await GitAsync(scope.Source, "config", "user.email", "tests@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(scope.Source, "source.txt"), "committed source");
            if (withProject)
            {
                await File.WriteAllTextAsync(Path.Combine(scope.Source, "Fixture.csproj"), """
                    <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>
                    """);
                await File.WriteAllTextAsync(Path.Combine(scope.Source, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
                await File.WriteAllTextAsync(Path.Combine(scope.Source, "Program.cs"), """"
                    using System.Xml.Linq;
                    if (File.ReadAllText("result.txt") != "verified candidate" || File.ReadAllText("checked.txt") != "verified candidate" || 17 + 25 != 42)
                        return 7;
                    XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
                    new XDocument(new XElement(ns + "TestRun",
                        new XElement(ns + "Results", new XElement(ns + "UnitTestResult", new XAttribute("testName", "candidate assertion"), new XAttribute("outcome", "Passed"))),
                        new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"),
                            new XElement(ns + "Counters", new XAttribute("total", 1), new XAttribute("executed", 1), new XAttribute("passed", 1), new XAttribute("failed", 0), new XAttribute("notExecuted", 0)))))
                        .Save(args[0]);
                    Console.WriteLine("CONTROLLED_TEST_PASS");
                    return 0;
                    """");
            }
            await GitAsync(scope.Source, "add", ".");
            await GitAsync(scope.Source, "commit", "-m", "Controlled candidate fixture");
            scope.Store = await CSharpDbWorkflowStore.OpenAsync(scope.DatabasePath, Ct, clock);
            return scope;
        }
        public TaskGraphAgentOptions Options(bool includeTests = false)
        {
            var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(DotNetPath)));
            var build = new TaskVerificationProfile("build", DotNetPath, digest,
                includeTests ? ["build", "Fixture.csproj", "--configuration", "Release", "--verbosity", "quiet"] : ["--version"], TimeSpan.FromMinutes(2));
            var tests = new TaskVerificationProfile("tests", DotNetPath, digest,
                ["run", "--project", "Fixture.csproj", "--configuration", "Release", "--", "results.trx"], TimeSpan.FromMinutes(2))
            { Kind = TaskVerificationKind.Tests, TestResultsPath = "results.trx" };
            return new(Source, Managed, "HEAD", "mock", "mock-model", new(ToolProfile.Coding), new(20, 20, 100_000), MaxRounds: 4, MaxOutputTokens: 256)
            {
                DependencyMode = TaskDependencyMode.AcceptedChanges,
                ArtifactVerificationRules = includeTests ? [new("precursor", "integrity"), new("task", "integrity")] : [new("task", "integrity")],
                VerificationAuthority = TaskVerificationAuthority.TrustedLocalProcess,
                VerificationProfiles = includeTests ? [build, tests] : [build],
                CommandVerificationRules = includeTests ? [new("task", "build", "build"), new("task", "tests", "tests")] : [new("task", "build", "build")]
            };
        }
        public AgentTaskGraphRunner Runner(ILlmClient client, ITaskVerificationWorker? worker = null) => new(Store, Store, Manager, client, worker);
        public async Task ReopenAsync()
        {
            await Store.DisposeAsync();
            Store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, Ct, clock);
        }
        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            if (!Directory.Exists(Root)) return;
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(TestBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Command integration cleanup escaped its isolated test directory.");
            foreach (var path in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(resolved, recursive: true);
        }
        private static string FindDotNet()
        {
            foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(path, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            throw new InvalidOperationException("The test SDK requires a dotnet host on PATH.");
        }
        private static async Task GitAsync(string path, params string[] arguments)
        {
            var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-C"); info.ArgumentList.Add(path);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await output;
            Assert.IsTrue(process.ExitCode == 0, await error);
        }
    }
}
