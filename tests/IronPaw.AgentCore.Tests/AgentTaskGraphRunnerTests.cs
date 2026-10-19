using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class AgentTaskGraphRunnerTests
{
    private static readonly CallerIdentity Owner = new("graph-owner", "Graph owner", true);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [TestMethod]
    public async Task ScopedExecution_UsesCommittedSourceAndSeparateWorkspaces_LeavesDirtyCheckoutUntouched()
    {
        await using var scope = await Scope.OpenAsync();
        await File.WriteAllTextAsync(Path.Combine(scope.Source, "source.txt"), "dirty local work");
        await File.WriteAllTextAsync(Path.Combine(scope.Source, "untracked.txt"), "untracked local work");
        var graph = Graph(Task("first"), Task("second"));
        var client = new ScriptedClient((request, turn) => turn == 1
            ? [Call("read", "read_file", new { path = "source.txt" }),
                Call("write", "write_file", new { path = "result.txt", content = UserInput(request) }), Usage(), new CompletedDelta()]
            : Final());

        var completed = await scope.Runner(client).StartAsync(graph, Owner, 2, scope.Options(graph), Ct);

        Assert.AreEqual(TaskGraphRunStatus.Succeeded, TaskGraphProgress.Inspect(completed).Status);
        foreach (var state in completed.Tasks)
        {
            Assert.AreEqual(TaskAcceptanceOutcome.Accepted, state.Acceptance!.Outcome);
        }
        var artifacts = completed.Tasks.Select(state => Assert.ContainsSingle(state.Result!.Artifacts)).ToArray();
        Assert.AreNotEqual(artifacts[0].Location, artifacts[1].Location);
        Assert.AreEqual("Objective first", await File.ReadAllTextAsync(artifacts[0].Location));
        Assert.AreEqual("Objective second", await File.ReadAllTextAsync(artifacts[1].Location));
        foreach (var request in client.Requests.Where(request => request.Messages.Any(message => message.Role == ChatRole.Tool)))
        {
            Assert.Contains(message => message.Role == ChatRole.Tool && message.Content.Contains("committed source", StringComparison.Ordinal), request.Messages);
        }
        Assert.AreEqual("dirty local work", await File.ReadAllTextAsync(Path.Combine(scope.Source, "source.txt")));
        Assert.AreEqual("untracked local work", await File.ReadAllTextAsync(Path.Combine(scope.Source, "untracked.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(scope.Source, "result.txt")));
        var budget = await scope.Store.GetBudgetAsync(new RunId(completed.AgentBinding!.RootRunId), Ct);
        Assert.AreEqual(40, budget.ChargedTokens);
        Assert.AreEqual(0, budget.ReservedTokens);
        var usage = await ((IWorkflowStore)scope.Store).GetUsageAsync(new RunId(completed.AgentBinding.RootRunId), Ct);
        Assert.AreEqual(4, usage.ModelCalls);
        Assert.AreEqual(4, usage.ToolCalls);
    }

    [TestMethod]
    public async Task MissingExplicitIntegrityRule_StaysAwaitingAcceptance_ResumeDoesNotReplayProvider()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph(Task("first"));
        var client = Writer();
        var runner = scope.Runner(client);

        var pending = await runner.StartAsync(graph, Owner, 1,
            scope.Options(graph) with { ArtifactVerificationRules = [] }, Ct);
        Assert.AreEqual(TaskGraphRunStatus.AwaitingAcceptance, TaskGraphProgress.Inspect(pending).Status);
        Assert.AreEqual(TaskAcceptanceOutcome.Inconclusive, Assert.ContainsSingle(pending.Tasks).Acceptance!.Outcome);
        var count = client.Requests.Count;
        var resumed = await runner.ResumeAsync(graph.GraphId, Owner, Ct);
        Assert.AreEqual(TaskGraphRunStatus.AwaitingAcceptance, TaskGraphProgress.Inspect(resumed).Status);
        Assert.AreEqual(count, client.Requests.Count);
    }

    [TestMethod]
    public async Task CompletedGraphAcrossReopen_ReturnsAcceptedResultWithoutAnyProviderCalls()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph(Task("first"));
        var completed = await scope.Runner(Writer()).StartAsync(graph, Owner, 1, scope.Options(graph), Ct);
        Assert.AreEqual(TaskGraphRunStatus.Succeeded, TaskGraphProgress.Inspect(completed).Status);
        var before = await scope.Store.GetBudgetAsync(new RunId(completed.AgentBinding!.RootRunId), Ct);
        await scope.ReopenAsync();
        var unused = new ScriptedClient((_, _) => throw new InvalidOperationException("Completed work was replayed."));

        var replay = await scope.Runner(unused).ResumeAsync(graph.GraphId, Owner, Ct);

        Assert.AreEqual(TaskGraphRunStatus.Succeeded, TaskGraphProgress.Inspect(replay).Status);
        Assert.IsEmpty(unused.Requests);
        Assert.AreEqual(before.ChargedTokens, (await scope.Store.GetBudgetAsync(new RunId(completed.AgentBinding.RootRunId), Ct)).ChargedTokens);
        Assert.AreEqual(completed.Tasks[0].Result!.Revision, replay.Tasks[0].Result!.Revision);
    }

    [TestMethod]
    public async Task SharedModelCap_StopsSiblingTasksWithoutGrantingFreshBudgets()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph(Task("first"), Task("second"));
        var client = Writer();

        var stopped = await scope.Runner(client).StartAsync(graph, Owner, 1,
            scope.Options(graph) with { SharedBudget = new(1, 10, 100_000) }, Ct);

        Assert.AreEqual(TaskGraphRunStatus.BudgetExhausted, TaskGraphProgress.Inspect(stopped).Status);
        Assert.ContainsSingle(client.Requests);
        foreach (var state in stopped.Tasks)
        {
            Assert.AreEqual(TaskGraphTaskStatus.BudgetExhausted, state.Status);
        }
        var usage = await ((IWorkflowStore)scope.Store).GetUsageAsync(new RunId(stopped.AgentBinding!.RootRunId), Ct);
        Assert.AreEqual(1, usage.ModelCalls);
        Assert.AreEqual(1, usage.ToolCalls);
        Assert.ContainsSingle(Directory.EnumerateFiles(scope.Managed, "workspace.json", SearchOption.AllDirectories));
        Assert.IsFalse(File.Exists(Path.Combine(scope.Source, "result.txt")));
    }

    [TestMethod]
    public async Task SharedTokenOvershoot_PreservesReceiptAndBlocksToolsAndSiblings()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph(Task("first"), Task("second"));
        var client = new ScriptedClient((_, _) => [new UsageDelta(200_000, 5, 0, 0),
            Call("write", "write_file", new { path = "result.txt", content = "must not execute" }), new CompletedDelta()]);

        var stopped = await scope.Runner(client).StartAsync(graph, Owner, 1,
            scope.Options(graph) with { SharedBudget = new(20, 20, 10_000) }, Ct);

        Assert.AreEqual(TaskGraphRunStatus.BudgetExhausted, TaskGraphProgress.Inspect(stopped).Status);
        Assert.ContainsSingle(client.Requests);
        var budget = await scope.Store.GetBudgetAsync(new RunId(stopped.AgentBinding!.RootRunId), Ct);
        Assert.AreEqual(200_005, budget.ChargedTokens);
        var usage = await ((IWorkflowStore)scope.Store).GetUsageAsync(new RunId(stopped.AgentBinding.RootRunId), Ct);
        Assert.AreEqual(0, usage.ToolCalls);
        Assert.IsEmpty(Directory.EnumerateFiles(scope.Managed, "result.txt", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task TaskTools_ExcludeUnscopedCapabilities_AndEnforceWritablePaths()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph(Task("first"));
        var client = new ScriptedClient((_, turn) => turn == 1
            ? [Call("forbidden-write", "write_file", new { path = "source.txt", content = "overwrite" }), Usage(), new CompletedDelta()]
            : Final());

        var stopped = await scope.Runner(client).StartAsync(graph, Owner, 1, scope.Options(graph), Ct);

        Assert.AreEqual(TaskGraphRunStatus.Failed, TaskGraphProgress.Inspect(stopped).Status);
        foreach (var request in client.Requests)
        {
            Assert.DoesNotContain(tool => tool.Name is "invoke_agent" or "request_input" or "web_navigate" or "save_memory" or "run_command" or "send_agent_message", request.Tools);
        }
        var child = Assert.ContainsSingle(run => run.ParentRunId is not null, await scope.Store.ListAsync(Ct));
        Assert.IsTrue(Assert.ContainsSingle(child.ToolExecutions!).Result!.IsError);
        Assert.AreEqual("committed source", await File.ReadAllTextAsync(Path.Combine(scope.Source, "source.txt")));
        foreach (var path in Directory.EnumerateFiles(scope.Managed, "source.txt", SearchOption.AllDirectories))
        {
            Assert.AreEqual("committed source", File.ReadAllText(path));
        }
    }

    [TestMethod]
    public async Task ExplicitRootToolPolicy_RestrictsTaskToolsWithoutLosingTaskRestrictions()
    {
        await using var scope = await Scope.OpenAsync();
        var task = Task("first") with
        {
            ToolPolicy = new(ToolProfile.Coding,
            InheritedPolicy: new(ToolProfile.Coding, DenyList: ["list_files"]))
        };
        var graph = Graph(task);
        var client = new ScriptedClient((_, _) => Final());

        await scope.Runner(client).StartAsync(graph, Owner, 1, scope.Options(graph) with
        { RootToolPolicy = new(ToolProfile.Coding, DenyList: ["write_file"]) }, Ct);

        var request = Assert.ContainsSingle(client.Requests);
        Assert.DoesNotContain(tool => tool.Name is "write_file" or "list_files", request.Tools);
        Assert.Contains(tool => tool.Name == "read_file", request.Tools);
        Assert.IsFalse(File.Exists(Path.Combine(scope.Source, "result.txt")));
    }

    [TestMethod]
    public async Task InterruptedProvider_ExplicitRecoveryReusesAttemptChildAndWorkspaceAcrossReopen()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph(Task("first"));
        var failing = new ScriptedClient((_, _) => [new AssistantTextDelta("partial"), new LlmErrorDelta("connection interrupted")]);
        var interrupted = await scope.Runner(failing).StartAsync(graph, Owner, 1, scope.Options(graph), Ct);
        var state = Assert.ContainsSingle(interrupted.Tasks);
        Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, state.Status);
        var child = Assert.ContainsSingle(run => run.ParentRunId is not null, await scope.Store.ListAsync(Ct));
        Assert.IsFalse(WorkflowIdentity.HasUnknownEffects(child));
        var marker = Assert.ContainsSingle(Directory.EnumerateFiles(scope.Managed, "workspace.json", SearchOption.AllDirectories));
        var workspaceBefore = JsonSerializer.Deserialize<TaskWorkspace>(await File.ReadAllTextAsync(marker))!;
        await scope.ReopenAsync();
        var recovery = Writer();

        var completed = await scope.Runner(recovery).RecoverTaskAsync(graph.GraphId, "first", Owner, Ct);

        Assert.AreEqual(TaskGraphRunStatus.Succeeded, TaskGraphProgress.Inspect(completed).Status);
        Assert.AreEqual(state.AttemptId, completed.Tasks[0].AttemptId);
        var reused = Assert.ContainsSingle(run => run.ParentRunId is not null, await scope.Store.ListAsync(Ct));
        Assert.AreEqual(child.RunId, reused.RunId);
        Assert.AreEqual(child.Request.SessionKey, reused.Request.SessionKey);
        Assert.AreEqual(workspaceBefore, JsonSerializer.Deserialize<TaskWorkspace>(await File.ReadAllTextAsync(marker)));
        Assert.ContainsSingle(Directory.EnumerateFiles(scope.Managed, "workspace.json", SearchOption.AllDirectories));
        Assert.AreEqual(2, recovery.Requests.Count);
        var usage = await ((IWorkflowStore)scope.Store).GetUsageAsync(new RunId(completed.AgentBinding!.RootRunId), Ct);
        Assert.AreEqual(3, usage.ModelCalls);
        Assert.AreEqual(1, usage.ToolCalls);
    }

    [TestMethod]
    public async Task SharedExecutionTime_IncludesWorkspacePreparationBeforeAnyProviderCall()
    {
        var clock = new ManualTimeProvider();
        await using var scope = await Scope.OpenAsync(clock);
        var graph = Graph(Task("first"));
        var client = new ScriptedClient((_, _) => throw new InvalidOperationException("Expired preflight admitted the provider."));
        // The test controls ledger time. Keep the independent real cancellation timer well
        // outside Git preparation so machine load cannot cancel before the simulated advance.
        var delayed = new AdvancingWorkspaces(scope.Manager, () => clock.Advance(TimeSpan.FromHours(2)));

        var stopped = await scope.Runner(client, delayed).StartAsync(graph, Owner, 1,
            scope.Options(graph) with { SharedBudget = new(10, 10, MaxExecutionTime: TimeSpan.FromHours(1)) }, Ct);

        Assert.AreEqual(TaskGraphRunStatus.BudgetExhausted, TaskGraphProgress.Inspect(stopped).Status);
        Assert.IsEmpty(client.Requests);
        var snapshot = await scope.Store.GetBudgetAsync(new RunId(stopped.AgentBinding!.RootRunId), Ct);
        Assert.IsTrue(snapshot.ExecutionTime >= TimeSpan.FromHours(2));
        Assert.AreEqual(TimeSpan.Zero, snapshot.RemainingExecutionTime);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActiveGraphControl_SettlesChildAndNeverRepeatsConfirmedWrite(bool permanent)
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph(Task("first"));
        var client = new InterruptibleClient();
        var runner = scope.Runner(client);
        var active = runner.StartAsync(graph, Owner, 1, scope.Options(graph), Ct);
        await client.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));

        if (permanent) await runner.CancelAsync(graph.GraphId, Owner, Ct);
        else await runner.PauseAsync(graph.GraphId, Owner, Ct);
        var stopped = await active.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(permanent ? WorkflowControl.Cancel : WorkflowControl.Pause, stopped.Control);
        var root = (await scope.Store.GetAsync(new RunId(stopped.AgentBinding!.RootRunId), Ct))!;
        Assert.AreEqual(stopped.Control, root.Control);
        var child = Assert.ContainsSingle(run => run.ParentRunId is not null, await scope.Store.ListAsync(Ct));
        Assert.IsNull(child.LeaseOwner);
        Assert.IsFalse(WorkflowIdentity.HasUnknownEffects(child));
        var write = Assert.ContainsSingle(child.ToolExecutions!);
        Assert.AreEqual(WorkflowToolState.Completed, write.State);
        Assert.IsFalse(write.Result!.IsError);
        Assert.IsTrue(client.Interrupted);
        if (permanent)
        {
            Assert.AreEqual(WorkflowTaskStatus.Cancelled, stopped.Tasks[0].Result!.Status);
            await Assert.ThrowsExactlyAsync<WorkflowCancelledException>(() => runner.ResumeAsync(graph.GraphId, Owner, Ct));
            await Assert.ThrowsExactlyAsync<WorkflowCancelledException>(() => runner.RecoverTaskAsync(graph.GraphId, "first", Owner, Ct));
        }
        else
        {
            Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, stopped.Tasks[0].Status);
            await runner.ResumeAsync(graph.GraphId, Owner, Ct);
            var completed = await runner.RecoverTaskAsync(graph.GraphId, "first", Owner, Ct);
            Assert.AreEqual(TaskGraphRunStatus.Succeeded, TaskGraphProgress.Inspect(completed).Status);
            var resumedChild = (await scope.Store.GetAsync(new RunId(child.RunId), Ct))!;
            Assert.AreEqual(write.ExecutionId, Assert.ContainsSingle(resumedChild.ToolExecutions!).ExecutionId);
            Assert.AreEqual(1, resumedChild.Usage!.ToolCalls);
            Assert.AreEqual(3, client.Requests);
        }
        Assert.AreEqual("committed source", await File.ReadAllTextAsync(Path.Combine(scope.Source, "source.txt")));
    }

    private static WorkflowTaskDefinition Task(string id) => new(id, "Objective " + id)
    {
        ToolPolicy = new(ToolProfile.Coding),
        Resources = new() { ReadPaths = ["source.txt"], WritePaths = ["result.txt"] },
        ExpectedArtifacts = [new("output", "Task output", TaskArtifactKind.File, "result.txt")],
        AcceptanceCriteria = [new("integrity", "Declared output is present and bound to this captured revision") { ArtifactIds = ["output"] }]
    };
    private static WorkflowTaskGraph Graph(params WorkflowTaskDefinition[] tasks) => new("graph-" + Guid.NewGuid().ToString("n"), "Execute isolated tasks", tasks.ToImmutableArray());
    private static string UserInput(LlmRequest request) => request.Messages.First(message => message.Role == ChatRole.User).Content.Split('\n')[0];
    private static ToolCallDelta Call(string id, string name, object arguments) => new(id, name, JsonSerializer.SerializeToElement(arguments));
    private static UsageDelta Usage() => new(7, 3, 0, 0);
    private static LlmDelta[] Final() => [Usage(), new AssistantTextDelta("Completed the task."), new CompletedDelta()];
    private static ScriptedClient Writer() => new((request, turn) => turn == 1
        ? [Call("write", "write_file", new { path = "result.txt", content = UserInput(request) }), Usage(), new CompletedDelta()]
        : Final());

    private sealed class ScriptedClient(Func<LlmRequest, int, LlmDelta[]> script) : ILlmClient
    {
        private readonly ConcurrentDictionary<SessionKey, int> turns = new();
        public ConcurrentQueue<LlmRequest> Requests { get; } = new();
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            foreach (var delta in script(request, turns.AddOrUpdate(request.SessionKey, 1, (_, current) => current + 1)))
            { ct.ThrowIfCancellationRequested(); yield return delta; }
        }
    }

    private sealed class InterruptibleClient : ILlmClient
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests;
        public bool Interrupted;
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            var turn = Interlocked.Increment(ref Requests);
            if (turn == 1)
            {
                yield return Call("write", "write_file", new { path = "result.txt", content = "confirmed write" });
                yield return Usage();
                yield return new CompletedDelta();
            }
            else if (turn == 2)
            {
                Waiting.TrySetResult();
                try { await System.Threading.Tasks.Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { Interrupted = true; throw; }
            }
            else foreach (var delta in Final()) yield return delta;
        }
    }

    private sealed class AdvancingWorkspaces(ITaskWorkspaceManager inner, Action advance) : ITaskWorkspaceManager
    {
        public Task<string> ResolveCommitAsync(string sourceWorkspaceRoot, string revision, CancellationToken ct) => inner.ResolveCommitAsync(sourceWorkspaceRoot, revision, ct);
        public Task<TaskWorkspace> OpenExistingAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct) => inner.OpenExistingAsync(binding, claim, ct);
        public Task ValidateWorkingStateAsync(TaskWorkspace workspace, ImmutableArray<WorkflowToolExecution> tools, CancellationToken ct) => inner.ValidateWorkingStateAsync(workspace, tools, ct);
        public async Task<TaskWorkspace> PrepareAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim,
            ImmutableArray<TaskDependencyInput> dependencies, CancellationToken ct)
        {
            var workspace = await inner.PrepareAsync(binding, claim, dependencies, ct);
            advance();
            return workspace;
        }
        public async Task<TaskWorkspace> OpenOrCreateAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct)
        {
            var workspace = await inner.OpenOrCreateAsync(binding, claim, ct);
            advance();
            return workspace;
        }
        public Task<TaskWorkspaceCapture> CaptureAsync(TaskWorkspace workspace, ImmutableArray<TaskArtifactRequirement> requirements, CancellationToken ct) => inner.CaptureAsync(workspace, requirements, ct);
        public Task<bool> VerifyAsync(TaskWorkspace workspace, WorkflowTaskResult result, CancellationToken ct) => inner.VerifyAsync(workspace, result, ct);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan delta) => Interlocked.Add(ref ticks, delta.Ticks);
    }

    private sealed class Scope : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "IronPawAgentGraphTests", Guid.NewGuid().ToString("n"));
        public string Source => Path.Combine(Root, "source");
        public string Managed => Path.Combine(Root, "managed");
        private string DatabasePath => Path.Combine(Root, "workflows.cdb");
        private readonly TimeProvider? clock;
        public CSharpDbWorkflowStore Store { get; private set; } = null!;
        public GitTaskWorkspaceManager Manager { get; } = new();
        private Scope(TimeProvider? clock) => this.clock = clock;
        public static async Task<Scope> OpenAsync(TimeProvider? clock = null)
        {
            var scope = new Scope(clock);
            Directory.CreateDirectory(scope.Source);
            await GitAsync(scope.Source, "init");
            await GitAsync(scope.Source, "config", "user.name", "IronPaw Tests");
            await GitAsync(scope.Source, "config", "user.email", "tests@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(scope.Source, "source.txt"), "committed source");
            await GitAsync(scope.Source, "add", "source.txt");
            await GitAsync(scope.Source, "commit", "-m", "Initial task input");
            scope.Store = await CSharpDbWorkflowStore.OpenAsync(scope.DatabasePath, Ct, clock);
            return scope;
        }
        public TaskGraphAgentOptions Options(WorkflowTaskGraph graph) => new(Source, Managed, "HEAD", "mock", "mock-model",
            new(ToolProfile.Coding), new(20, 20, 100_000), MaxRounds: 4, MaxOutputTokens: 256)
        { ArtifactVerificationRules = graph.Tasks.Select(task => new TaskArtifactVerificationRule(task.TaskId, "integrity")).ToImmutableArray() };
        public AgentTaskGraphRunner Runner(ILlmClient client, ITaskWorkspaceManager? manager = null) => new(Store, Store, manager ?? Manager, client);
        public async Task ReopenAsync()
        {
            await Store.DisposeAsync();
            Store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, Ct, clock);
        }
        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            if (!Directory.Exists(Root)) return;
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        private static async Task GitAsync(string path, params string[] arguments)
        {
            var info = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
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
