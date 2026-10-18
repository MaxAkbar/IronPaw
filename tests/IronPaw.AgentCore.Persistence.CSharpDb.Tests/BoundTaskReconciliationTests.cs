using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.AgentCore.Persistence.CSharpDb.Tests;

public sealed class BoundTaskReconciliationTests : IDisposable
{
    private static readonly string TestBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IronPawBoundTaskReconciliation"));
    private readonly string _directory = Path.Combine(TestBase, Guid.NewGuid().ToString("n"));
    private string DatabasePath => Path.Combine(_directory, "workflows.cdb");
    private static readonly CallerIdentity Caller = new("owner", "Original owner", true);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData(WorkflowTaskStatus.AwaitingVerification)]
    [InlineData(WorkflowTaskStatus.Failed)]
    public async Task UnknownChildToolBlocksTaskReconciliationUntilItsReceiptIsEstablished(WorkflowTaskStatus outcome)
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var accepted = await AcceptAsync(store);
        var claim = (await store.ClaimTaskAsync(accepted.Graph.GraphId, Caller, Ct))!;
        var tool = new WorkflowToolExecution("write-output", new("write-output", "write_file",
            JsonSerializer.SerializeToElement(new { path = "result.txt", content = "candidate" })));
        var child = Child(accepted, claim) with { ToolExecutions = [tool], Usage = new(ToolCalls: 1) };
        await store.CreateAsync(child, Ct);
        var before = await store.AbandonTaskClaimAsync(claim, "Lost task and file outcome", Ct);
        var journalBefore = await store.ReadTaskGraphJournalAsync(accepted.Graph.GraphId, Ct);
        var result = Result(claim, outcome);

        await Assert.ThrowsAsync<WorkflowReconciliationRequiredException>(() => store.ReconcileTaskAsync(
            accepted.Graph.GraphId, "task", result, "Task-level evidence cannot settle unknown file effects", Caller, Ct));

        var unchanged = (await store.GetTaskGraphAsync(accepted.Graph.GraphId, Ct))!;
        Assert.Equal(before.Revision, unchanged.Revision);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(unchanged));
        Assert.Equal(journalBefore.Count, (await store.ReadTaskGraphJournalAsync(accepted.Graph.GraphId, Ct)).Count);
        Assert.Null(unchanged.Tasks[0].Result);
        Assert.Null(unchanged.Tasks[0].ReconciliationNote);

        await store.SaveAsync(child with { Status = WorkflowStatus.Interrupted, ToolExecutions = [tool with {
            State = WorkflowToolState.Completed, Result = ToolResult.Success("Observed the saved file"),
            ReconciliationNote = "Inspected preserved file bytes" }] }, child.Revision, "tool_reconciled", Ct);
        var reconciled = await store.ReconcileTaskAsync(accepted.Graph.GraphId, "task", result,
            "Established the task outcome after settling file effects", Caller, Ct);
        Assert.Equal(result, reconciled.Tasks[0].Result);
        Assert.Equal(outcome == WorkflowTaskStatus.Failed ? TaskGraphTaskStatus.Failed : TaskGraphTaskStatus.AwaitingAcceptance,
            reconciled.Tasks[0].Status);
        Assert.Null(reconciled.Tasks[0].LeaseToken);
        Assert.Null(reconciled.Tasks[0].Acceptance);
    }

    [Fact]
    public async Task ActiveChildLeaseBlocksTaskReconciliationEvenWithoutUnknownTools()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var accepted = await AcceptAsync(store);
        var claim = (await store.ClaimTaskAsync(accepted.Graph.GraphId, Caller, Ct))!;
        var child = Child(accepted, claim) with { Status = WorkflowStatus.Running, LeaseOwner = "active-worker" };
        await store.CreateAsync(child, Ct);
        var before = await store.AbandonTaskClaimAsync(claim, "Graph owner stopped while child remained active", Ct);

        await Assert.ThrowsAsync<WorkflowReconciliationRequiredException>(() => store.ReconcileTaskAsync(
            accepted.Graph.GraphId, "task", Result(claim, WorkflowTaskStatus.Failed), "Child is still active", Caller, Ct));

        var unchanged = (await store.GetTaskGraphAsync(accepted.Graph.GraphId, Ct))!;
        Assert.Equal(before.Revision, unchanged.Revision);
        Assert.Null(unchanged.Tasks[0].Result);
        Assert.Equal("active-worker", (await store.GetAsync(new(child.RunId), Ct))!.LeaseOwner);
        await store.SaveAsync(child with { Status = WorkflowStatus.Interrupted, LeaseOwner = null }, child.Revision, "child_settled", Ct);
        var reconciled = await store.ReconcileTaskAsync(accepted.Graph.GraphId, "task", Result(claim, WorkflowTaskStatus.Failed),
            "Observed the child settle without outstanding effects", Caller, Ct);
        Assert.Equal(TaskGraphTaskStatus.Failed, reconciled.Tasks[0].Status);
    }

    [Fact]
    public async Task MissingBoundChildStillAllowsAnEstablishedTaskOutcome()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var accepted = await AcceptAsync(store);
        var claim = (await store.ClaimTaskAsync(accepted.Graph.GraphId, Caller, Ct))!;
        await store.AbandonTaskClaimAsync(claim, "Interrupted before creating the child", Ct);
        var reconciled = await store.ReconcileTaskAsync(accepted.Graph.GraphId, "task", Result(claim, WorkflowTaskStatus.Failed),
            "Established that child execution never began", Caller, Ct);
        Assert.Equal(TaskGraphTaskStatus.Failed, reconciled.Tasks[0].Status);
    }

    [Fact]
    public async Task GenericTaskReconciliationRemainsAvailableWithoutAnAgentBinding()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph();
        await store.AcceptTaskGraphAsync(graph, Caller, 1, Ct);
        var claim = (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!;
        await store.AbandonTaskClaimAsync(claim, "Generic executor interrupted", Ct);
        var reconciled = await store.ReconcileTaskAsync(graph.GraphId, "task", Result(claim, WorkflowTaskStatus.Failed),
            "Host established the generic executor outcome", Caller, Ct);
        Assert.Null(reconciled.AgentBinding);
        Assert.Equal(TaskGraphTaskStatus.Failed, reconciled.Tasks[0].Status);
    }

    private Task<TaskGraphCheckpoint> AcceptAsync(CSharpDbWorkflowStore store)
    {
        var graph = Graph();
        var binding = new TaskGraphAgentBinding(AgentTaskIdentity.RootRunId(graph.GraphId).Value,
            Path.Combine(_directory, "source"), new string('a', 40), Path.Combine(_directory, "managed"),
            "local", "rule-based-v1", new(ToolProfile.Coding), new(10, 10));
        return store.AcceptAgentTaskGraphAsync(graph, Caller, 1, binding, Ct);
    }
    private static WorkflowTaskGraph Graph() => new("bound-reconciliation", "Establish outcomes without bypassing child effects",
        [new("task", "Produce controlled output") { ToolPolicy = new(ToolProfile.Coding),
            Resources = new() { ReadPaths = ["."], WritePaths = ["result.txt"] },
            AcceptanceCriteria = [new("criterion", "Trusted host verifies the result")] }]);
    private static WorkflowCheckpoint Child(TaskGraphCheckpoint graph, TaskGraphClaim claim)
    {
        var request = AgentTaskBindingValidator.CreateChildRequest(graph.Graph, graph.Caller, graph.AgentBinding!, claim.Task, claim.AttemptId);
        return new(request.RequestedRunId!.Value.Value, graph.AgentBinding!.RootRunId, graph.AgentBinding.RootRunId,
            claim.Task.TaskId, request, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Budget: request.Budget, Usage: new());
    }
    private static WorkflowTaskResult Result(TaskGraphClaim claim, WorkflowTaskStatus outcome) =>
        new(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId, outcome, "established-revision", "Externally established task outcome");
    public void Dispose()
    {
        var resolved = Path.GetFullPath(_directory);
        if (!resolved.StartsWith(TestBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Reconciliation test cleanup escaped its isolated root.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
