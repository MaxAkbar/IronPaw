using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class AgentTaskBoundaryTests
{
    private static readonly CallerIdentity Owner = new("owner", "Graph owner", true);

    [TestMethod]
    public async Task NoRequiredCriteria_CannotBeAcceptedVacuously()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = new WorkflowTaskGraph("graph", "Produce a result", [new("task", "Perform the task")]);
        var runner = scope.Runner(new CompletingClient());

        var result = await runner.StartAsync(graph, Owner, 1, scope.Options, CancellationToken.None);

        Assert.AreEqual(TaskGraphTaskStatus.AwaitingAcceptance, Assert.ContainsSingle(result.Tasks).Status);
        Assert.AreEqual(TaskAcceptanceOutcome.Inconclusive, result.Tasks[0].Acceptance!.Outcome);
        Assert.AreEqual(TaskGraphRunStatus.AwaitingAcceptance, TaskGraphProgress.Inspect(result).Status);
    }

    [TestMethod]
    [DataRow(WorkflowControl.Pause)]
    [DataRow(WorkflowControl.Cancel)]
    public async Task ControlDuringExplicitRecovery_RemainsAppliedAfterRecoverySettles(WorkflowControl control)
    {
        await using var scope = await Scope.OpenAsync();
        var graph = new WorkflowTaskGraph("graph", "Produce a result", [new("task", "Perform the task")]);
        var interrupted = await scope.Runner(new InterruptingClient()).StartAsync(graph, Owner, 1, scope.Options, CancellationToken.None);
        Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, interrupted.Tasks[0].Status);
        var client = new BlockingClient();
        var runner = scope.Runner(client);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var recovery = runner.RecoverTaskAsync(graph.GraphId, "task", Owner, cleanup.Token);
        try
        {
            await client.Entered.Task.WaitAsync(cleanup.Token);
            if (control == WorkflowControl.Pause) await runner.PauseAsync(graph.GraphId, Owner, cleanup.Token);
            else await runner.CancelAsync(graph.GraphId, Owner, cleanup.Token);
            var paused = await recovery.WaitAsync(cleanup.Token);
            Assert.AreEqual(control, paused.Control);
            Assert.AreEqual(control, (await scope.Store.GetAsync(new(paused.AgentBinding!.RootRunId), cleanup.Token))!.Control);
            Assert.IsNull(paused.Tasks[0].LeaseToken);
            if (control == WorkflowControl.Pause) Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, paused.Tasks[0].Status);
            else Assert.Contains(paused.Tasks[0].Status, new[] { TaskGraphTaskStatus.Cancelled, TaskGraphTaskStatus.NeedsReconciliation });
            Assert.AreEqual(1, client.Calls);
        }
        finally
        {
            await cleanup.CancelAsync();
            try { await recovery; } catch (OperationCanceledException) { }
        }
    }

    private sealed class CompletingClient : ILlmClient
    {
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            yield return new UsageDelta(1, 1, 0, 0);
            yield return new AssistantTextDelta("Completed.");
            yield return new CompletedDelta();
        }
    }

    private sealed class InterruptingClient : ILlmClient
    {
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            yield return new LlmErrorDelta("Interrupted connection");
        }
    }

    private sealed class BlockingClient : ILlmClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            await System.Threading.Tasks.Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }
    }

    private sealed class FakeWorkspaces(string root) : ITaskWorkspaceManager
    {
        public Task ValidateWorkingStateAsync(TaskWorkspace workspace, ImmutableArray<WorkflowToolExecution> tools, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return System.Threading.Tasks.Task.CompletedTask; }
        public Task<string> ResolveCommitAsync(string sourceWorkspaceRoot, string revision, CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new string('a', 40));
        public Task<TaskWorkspace> OpenExistingAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var attempt = Path.Combine(root, "attempt");
            return System.Threading.Tasks.Task.FromResult(new TaskWorkspace(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId,
                claim.AttemptId, binding.BaseCommit, Path.Combine(attempt, "workspace"), attempt)
            { InputDigest = claim.Inputs?.Digest });
        }
        public Task<TaskWorkspace> OpenOrCreateAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var attempt = Path.Combine(root, "attempt");
            var path = Path.Combine(attempt, "workspace");
            Directory.CreateDirectory(path);
            return System.Threading.Tasks.Task.FromResult(new TaskWorkspace(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId,
                claim.AttemptId, binding.BaseCommit, path, attempt)
            { InputDigest = claim.Inputs?.Digest });
        }
        public Task<TaskWorkspace> PrepareAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim,
            ImmutableArray<TaskDependencyInput> dependencies, CancellationToken ct) => OpenOrCreateAsync(binding, claim, ct);
        public Task<TaskWorkspaceCapture> CaptureAsync(TaskWorkspace workspace, ImmutableArray<TaskArtifactRequirement> requirements, CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new TaskWorkspaceCapture("revision", [], []));
        public Task<bool> VerifyAsync(TaskWorkspace workspace, WorkflowTaskResult result, CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(true);
    }

    private sealed class Scope : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "IronPaw-AgentBoundary-" + Guid.NewGuid().ToString("n"));
        public CSharpDbWorkflowStore Store { get; private set; } = null!;
        private FakeWorkspaces Workspaces => new(Path.Combine(root, "managed"));
        public TaskGraphAgentOptions Options => new(Path.Combine(root, "source"), Path.Combine(root, "managed"), "HEAD", "mock", "model",
            new(ToolProfile.Minimal), new(10, 10), MaxRounds: 2);
        public static async Task<Scope> OpenAsync()
        {
            var scope = new Scope();
            scope.Store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(scope.root, "workflows.cdb"));
            return scope;
        }
        public AgentTaskGraphRunner Runner(ILlmClient client) => new(Store, Store, Workspaces, client);
        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
