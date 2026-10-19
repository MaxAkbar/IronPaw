using System.Collections.Immutable;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class TaskGraphProgressTests
{
    [TestMethod]
    public void BlockedDescendantsAndIndependentReadyWork_AreReportedInDeclarationOrder()
    {
        var checkpoint = Checkpoint(
        [
            Definition("deliver", "review", "test"),
            Definition("review", "implement"),
            Definition("test", "implement"),
            Definition("independent-b"), Definition("implement"), Definition("independent-a")
        ],
        [
            new("independent-a"), new("test"), new("implement"),
            new("deliver"), new("independent-b"), new("review")
        ]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.AreEqual(TaskGraphRunStatus.Queued, summary.Status);
        Assert.AreSequenceEqual(new[] { "independent-b", "implement", "independent-a" }, summary.ReadyTaskIds.ToArray());
        Assert.AreSequenceEqual(new[]
        {
            new TaskGraphDependencyBlock("deliver", "review", TaskGraphTaskStatus.Pending),
            new TaskGraphDependencyBlock("deliver", "test", TaskGraphTaskStatus.Pending),
            new TaskGraphDependencyBlock("review", "implement", TaskGraphTaskStatus.Pending),
            new TaskGraphDependencyBlock("test", "implement", TaskGraphTaskStatus.Pending)
        }, summary.BlockedTasks.ToArray());
        Assert.AreEqual(0, summary.ActiveClaims);
    }

    [TestMethod]
    public void AcceptedDependency_UnblocksItsDependent()
    {
        var checkpoint = Checkpoint([Definition("deliver", "build"), Definition("build")],
            [new("deliver"), Accepted("build")]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.AreEqual(TaskGraphRunStatus.Queued, summary.Status);
        Assert.AreSequenceEqual(new[] { "deliver" }, summary.ReadyTaskIds.ToArray());
        Assert.IsEmpty(summary.BlockedTasks);
    }

    [TestMethod]
    public void SuccessfulRawResult_StillRequiresAcceptanceBeforeUnblockingDescendants()
    {
        var result = new WorkflowTaskResult("graph", "graph-fingerprint", "build",
            WorkflowTaskStatus.Succeeded, "revision-1", "The executor reports success");
        var checkpoint = Checkpoint([Definition("build"), Definition("deliver", "build")],
            [new("build", TaskGraphTaskStatus.AwaitingAcceptance, Result: result), new("deliver")]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.AreEqual(TaskGraphRunStatus.AwaitingAcceptance, summary.Status);
        Assert.IsEmpty(summary.ReadyTaskIds);
        Assert.AreEqual(new TaskGraphDependencyBlock("deliver", "build", TaskGraphTaskStatus.AwaitingAcceptance), Assert.ContainsSingle(summary.BlockedTasks));
    }

    [TestMethod]
    [DataRow(TaskAcceptanceOutcome.Inconclusive)]
    [DataRow(TaskAcceptanceOutcome.Rejected)]
    [DataRow(null)]
    public void SucceededStateWithoutAcceptedReceipt_DoesNotCompleteGraphOrUnblockDependents(TaskAcceptanceOutcome? outcome)
    {
        var state = new TaskGraphTaskState("build", TaskGraphTaskStatus.Succeeded,
            Acceptance: outcome is { } value ? Receipt("build", value) : null);
        var checkpoint = Checkpoint([Definition("build"), Definition("deliver", "build")], [state, new("deliver")]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.AreEqual(TaskGraphRunStatus.Queued, summary.Status);
        Assert.IsEmpty(summary.ReadyTaskIds);
        Assert.AreEqual(new TaskGraphDependencyBlock("deliver", "build", TaskGraphTaskStatus.Succeeded), Assert.ContainsSingle(summary.BlockedTasks));
    }

    [TestMethod]
    [DataRow(TaskGraphTaskStatus.Failed, TaskGraphRunStatus.Failed)]
    [DataRow(TaskGraphTaskStatus.BudgetExhausted, TaskGraphRunStatus.BudgetExhausted)]
    [DataRow(TaskGraphTaskStatus.Cancelled, TaskGraphRunStatus.Cancelled)]
    public void EstablishedTerminalOutcome_BlocksDependentAndExplainsGraphStatus(
        TaskGraphTaskStatus taskStatus, TaskGraphRunStatus graphStatus)
    {
        var checkpoint = Checkpoint([Definition("build"), Definition("deliver", "build")],
            [new("build", taskStatus), new("deliver")]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.AreEqual(graphStatus, summary.Status);
        Assert.IsEmpty(summary.ReadyTaskIds);
        Assert.AreEqual(new TaskGraphDependencyBlock("deliver", "build", taskStatus), Assert.ContainsSingle(summary.BlockedTasks));
    }

    [TestMethod]
    public void IndependentReadyTask_RemainsVisibleAfterAnotherBranchFails()
    {
        var checkpoint = Checkpoint([Definition("failed"), Definition("blocked", "failed"), Definition("independent")],
            [new("failed", TaskGraphTaskStatus.Failed), new("blocked"), new("independent")]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.AreEqual(TaskGraphRunStatus.Queued, summary.Status);
        Assert.AreSequenceEqual(new[] { "independent" }, summary.ReadyTaskIds.ToArray());
        Assert.AreEqual(new TaskGraphDependencyBlock("blocked", "failed", TaskGraphTaskStatus.Failed), Assert.ContainsSingle(summary.BlockedTasks));
    }

    [TestMethod]
    [DataRow(WorkflowControl.Cancel, TaskGraphRunStatus.Cancelled)]
    [DataRow(WorkflowControl.Pause, TaskGraphRunStatus.Paused)]
    public void RootControl_SuppressesReadyWorkAndTakesPrecedenceOverActiveClaims(
        WorkflowControl control, TaskGraphRunStatus status)
    {
        var checkpoint = Checkpoint([Definition("active"), Definition("ready")],
            [new("active", TaskGraphTaskStatus.Running, LeaseToken: "lease-1"), new("ready")]) with
        { Control = control };

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.AreEqual(status, summary.Status);
        Assert.IsEmpty(summary.ReadyTaskIds);
        Assert.AreEqual(1, summary.ActiveClaims);
    }

    [TestMethod]
    [DataRow(WorkflowControl.None)]
    [DataRow(WorkflowControl.Cancel)]
    [DataRow(WorkflowControl.Pause)]
    public void UncertainTaskRemainsVisible_EvenWhenGraphWasPausedOrCancelled(WorkflowControl control)
    {
        var checkpoint = Checkpoint([Definition("uncertain"), Definition("ready")],
            [new("uncertain", TaskGraphTaskStatus.NeedsReconciliation), new("ready")]) with
        { Control = control };

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.AreEqual(TaskGraphRunStatus.NeedsReconciliation, summary.Status);
        Assert.AreSequenceEqual(control == WorkflowControl.None ? new[] { "ready" } : Array.Empty<string>(), summary.ReadyTaskIds.ToArray());
    }

    [TestMethod]
    public void AnyLiveLease_CountsAsActiveIncludingVerificationClaim()
    {
        var checkpoint = Checkpoint([Definition("executing"), Definition("verifying"), Definition("ready")],
        [
            new("executing", TaskGraphTaskStatus.Running, LeaseToken: "execution-lease"),
            new("verifying", TaskGraphTaskStatus.AwaitingAcceptance, LeaseToken: "verification-lease"),
            new("ready")
        ]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.AreEqual(TaskGraphRunStatus.Running, summary.Status);
        Assert.AreEqual(2, summary.ActiveClaims);
        Assert.AreSequenceEqual(new[] { "ready" }, summary.ReadyTaskIds.ToArray());
    }

    [TestMethod]
    public void EveryTaskWithAcceptedReceipt_CompletesGraph()
    {
        var checkpoint = Checkpoint([Definition("first"), Definition("second", "first")],
            [Accepted("first"), Accepted("second")]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.AreEqual(TaskGraphRunStatus.Succeeded, summary.Status);
        Assert.IsEmpty(summary.ReadyTaskIds);
        Assert.IsEmpty(summary.BlockedTasks);
        Assert.AreEqual(0, summary.ActiveClaims);
    }

    [TestMethod]
    public void AcceptanceWait_TakesPrecedenceOverFailureAndBudgetExhaustion()
    {
        var checkpoint = Checkpoint([Definition("failed"), Definition("budget"), Definition("waiting")],
        [
            new("failed", TaskGraphTaskStatus.Failed), new("budget", TaskGraphTaskStatus.BudgetExhausted),
            new("waiting", TaskGraphTaskStatus.AwaitingAcceptance)
        ]);

        Assert.AreEqual(TaskGraphRunStatus.AwaitingAcceptance, TaskGraphProgress.Inspect(checkpoint).Status);
    }

    [TestMethod]
    public void Failure_TakesPrecedenceOverBudgetExhaustionAndCancellation()
    {
        var checkpoint = Checkpoint([Definition("failed"), Definition("budget"), Definition("cancelled")],
        [
            new("failed", TaskGraphTaskStatus.Failed), new("budget", TaskGraphTaskStatus.BudgetExhausted),
            new("cancelled", TaskGraphTaskStatus.Cancelled)
        ]);

        Assert.AreEqual(TaskGraphRunStatus.Failed, TaskGraphProgress.Inspect(checkpoint).Status);
    }

    [TestMethod]
    public void Inspection_IsDeterministicAndLeavesCheckpointUnchanged()
    {
        var checkpoint = Checkpoint([Definition("deliver", "review", "test"), Definition("review"), Definition("test")],
            [new("deliver"), Accepted("review"), new("test", TaskGraphTaskStatus.AwaitingAcceptance)]);
        var before = JsonSerializer.Serialize(checkpoint);

        var first = TaskGraphProgress.Inspect(checkpoint);
        var second = TaskGraphProgress.Inspect(checkpoint);

        Assert.AreEqual(first.Status, second.Status);
        Assert.AreSequenceEqual(first.ReadyTaskIds.ToArray(), second.ReadyTaskIds.ToArray());
        Assert.AreSequenceEqual(first.BlockedTasks.ToArray(), second.BlockedTasks.ToArray());
        Assert.AreEqual(first.ActiveClaims, second.ActiveClaims);
        Assert.AreEqual(before, JsonSerializer.Serialize(checkpoint));
        Assert.AreEqual(new TaskGraphDependencyBlock("deliver", "test", TaskGraphTaskStatus.AwaitingAcceptance), Assert.ContainsSingle(first.BlockedTasks));
    }

    private static WorkflowTaskDefinition Definition(string taskId, params string[] dependencies) =>
        new(taskId, "Perform " + taskId) { Dependencies = dependencies.ToImmutableArray() };

    private static TaskGraphTaskState Accepted(string taskId) =>
        new(taskId, TaskGraphTaskStatus.Succeeded, Acceptance: Receipt(taskId, TaskAcceptanceOutcome.Accepted));

    private static TaskAcceptanceReceipt Receipt(string taskId, TaskAcceptanceOutcome outcome) =>
        new(outcome, "trusted-verifier", "Reported decision", "graph-fingerprint", taskId,
            "attempt-1", "result-digest", "revision-1", DateTimeOffset.UnixEpoch);

    private static TaskGraphCheckpoint Checkpoint(ImmutableArray<WorkflowTaskDefinition> definitions,
        ImmutableArray<TaskGraphTaskState> states) =>
        new(new WorkflowTaskGraph("graph", "Complete the graph", definitions), "graph-fingerprint",
            CallerIdentity.Authenticated("owner", "Owner"), 3, states, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
}
