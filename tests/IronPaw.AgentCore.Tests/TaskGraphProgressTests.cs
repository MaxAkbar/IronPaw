using System.Collections.Immutable;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

public sealed class TaskGraphProgressTests
{
    [Fact]
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

        Assert.Equal(TaskGraphRunStatus.Queued, summary.Status);
        Assert.Equal(new[] { "independent-b", "implement", "independent-a" }, summary.ReadyTaskIds.ToArray());
        Assert.Equal(new[]
        {
            new TaskGraphDependencyBlock("deliver", "review", TaskGraphTaskStatus.Pending),
            new TaskGraphDependencyBlock("deliver", "test", TaskGraphTaskStatus.Pending),
            new TaskGraphDependencyBlock("review", "implement", TaskGraphTaskStatus.Pending),
            new TaskGraphDependencyBlock("test", "implement", TaskGraphTaskStatus.Pending)
        }, summary.BlockedTasks.ToArray());
        Assert.Equal(0, summary.ActiveClaims);
    }

    [Fact]
    public void AcceptedDependency_UnblocksItsDependent()
    {
        var checkpoint = Checkpoint([Definition("deliver", "build"), Definition("build")],
            [new("deliver"), Accepted("build")]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.Equal(TaskGraphRunStatus.Queued, summary.Status);
        Assert.Equal(new[] { "deliver" }, summary.ReadyTaskIds.ToArray());
        Assert.Empty(summary.BlockedTasks);
    }

    [Fact]
    public void SuccessfulRawResult_StillRequiresAcceptanceBeforeUnblockingDescendants()
    {
        var result = new WorkflowTaskResult("graph", "graph-fingerprint", "build",
            WorkflowTaskStatus.Succeeded, "revision-1", "The executor reports success");
        var checkpoint = Checkpoint([Definition("build"), Definition("deliver", "build")],
            [new("build", TaskGraphTaskStatus.AwaitingAcceptance, Result: result), new("deliver")]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.Equal(TaskGraphRunStatus.AwaitingAcceptance, summary.Status);
        Assert.Empty(summary.ReadyTaskIds);
        Assert.Equal(new TaskGraphDependencyBlock("deliver", "build", TaskGraphTaskStatus.AwaitingAcceptance),
            Assert.Single(summary.BlockedTasks));
    }

    [Theory]
    [InlineData(TaskAcceptanceOutcome.Inconclusive)]
    [InlineData(TaskAcceptanceOutcome.Rejected)]
    [InlineData(null)]
    public void SucceededStateWithoutAcceptedReceipt_DoesNotCompleteGraphOrUnblockDependents(TaskAcceptanceOutcome? outcome)
    {
        var state = new TaskGraphTaskState("build", TaskGraphTaskStatus.Succeeded,
            Acceptance: outcome is { } value ? Receipt("build", value) : null);
        var checkpoint = Checkpoint([Definition("build"), Definition("deliver", "build")], [state, new("deliver")]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.Equal(TaskGraphRunStatus.Queued, summary.Status);
        Assert.Empty(summary.ReadyTaskIds);
        Assert.Equal(new TaskGraphDependencyBlock("deliver", "build", TaskGraphTaskStatus.Succeeded),
            Assert.Single(summary.BlockedTasks));
    }

    [Theory]
    [InlineData(TaskGraphTaskStatus.Failed, TaskGraphRunStatus.Failed)]
    [InlineData(TaskGraphTaskStatus.BudgetExhausted, TaskGraphRunStatus.BudgetExhausted)]
    [InlineData(TaskGraphTaskStatus.Cancelled, TaskGraphRunStatus.Cancelled)]
    public void EstablishedTerminalOutcome_BlocksDependentAndExplainsGraphStatus(
        TaskGraphTaskStatus taskStatus, TaskGraphRunStatus graphStatus)
    {
        var checkpoint = Checkpoint([Definition("build"), Definition("deliver", "build")],
            [new("build", taskStatus), new("deliver")]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.Equal(graphStatus, summary.Status);
        Assert.Empty(summary.ReadyTaskIds);
        Assert.Equal(new TaskGraphDependencyBlock("deliver", "build", taskStatus), Assert.Single(summary.BlockedTasks));
    }

    [Fact]
    public void IndependentReadyTask_RemainsVisibleAfterAnotherBranchFails()
    {
        var checkpoint = Checkpoint([Definition("failed"), Definition("blocked", "failed"), Definition("independent")],
            [new("failed", TaskGraphTaskStatus.Failed), new("blocked"), new("independent")]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.Equal(TaskGraphRunStatus.Queued, summary.Status);
        Assert.Equal(new[] { "independent" }, summary.ReadyTaskIds.ToArray());
        Assert.Equal(new TaskGraphDependencyBlock("blocked", "failed", TaskGraphTaskStatus.Failed),
            Assert.Single(summary.BlockedTasks));
    }

    [Theory]
    [InlineData(WorkflowControl.Cancel, TaskGraphRunStatus.Cancelled)]
    [InlineData(WorkflowControl.Pause, TaskGraphRunStatus.Paused)]
    public void RootControl_SuppressesReadyWorkAndTakesPrecedenceOverActiveClaims(
        WorkflowControl control, TaskGraphRunStatus status)
    {
        var checkpoint = Checkpoint([Definition("active"), Definition("ready")],
            [new("active", TaskGraphTaskStatus.Running, LeaseToken: "lease-1"), new("ready")]) with { Control = control };

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.Equal(status, summary.Status);
        Assert.Empty(summary.ReadyTaskIds);
        Assert.Equal(1, summary.ActiveClaims);
    }

    [Theory]
    [InlineData(WorkflowControl.None)]
    [InlineData(WorkflowControl.Cancel)]
    [InlineData(WorkflowControl.Pause)]
    public void UncertainTaskRemainsVisible_EvenWhenGraphWasPausedOrCancelled(WorkflowControl control)
    {
        var checkpoint = Checkpoint([Definition("uncertain"), Definition("ready")],
            [new("uncertain", TaskGraphTaskStatus.NeedsReconciliation), new("ready")]) with { Control = control };

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.Equal(TaskGraphRunStatus.NeedsReconciliation, summary.Status);
        Assert.Equal(control == WorkflowControl.None ? new[] { "ready" } : Array.Empty<string>(), summary.ReadyTaskIds.ToArray());
    }

    [Fact]
    public void AnyLiveLease_CountsAsActiveIncludingVerificationClaim()
    {
        var checkpoint = Checkpoint([Definition("executing"), Definition("verifying"), Definition("ready")],
        [
            new("executing", TaskGraphTaskStatus.Running, LeaseToken: "execution-lease"),
            new("verifying", TaskGraphTaskStatus.AwaitingAcceptance, LeaseToken: "verification-lease"),
            new("ready")
        ]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.Equal(TaskGraphRunStatus.Running, summary.Status);
        Assert.Equal(2, summary.ActiveClaims);
        Assert.Equal(new[] { "ready" }, summary.ReadyTaskIds.ToArray());
    }

    [Fact]
    public void EveryTaskWithAcceptedReceipt_CompletesGraph()
    {
        var checkpoint = Checkpoint([Definition("first"), Definition("second", "first")],
            [Accepted("first"), Accepted("second")]);

        var summary = TaskGraphProgress.Inspect(checkpoint);

        Assert.Equal(TaskGraphRunStatus.Succeeded, summary.Status);
        Assert.Empty(summary.ReadyTaskIds);
        Assert.Empty(summary.BlockedTasks);
        Assert.Equal(0, summary.ActiveClaims);
    }

    [Fact]
    public void AcceptanceWait_TakesPrecedenceOverFailureAndBudgetExhaustion()
    {
        var checkpoint = Checkpoint([Definition("failed"), Definition("budget"), Definition("waiting")],
        [
            new("failed", TaskGraphTaskStatus.Failed), new("budget", TaskGraphTaskStatus.BudgetExhausted),
            new("waiting", TaskGraphTaskStatus.AwaitingAcceptance)
        ]);

        Assert.Equal(TaskGraphRunStatus.AwaitingAcceptance, TaskGraphProgress.Inspect(checkpoint).Status);
    }

    [Fact]
    public void Failure_TakesPrecedenceOverBudgetExhaustionAndCancellation()
    {
        var checkpoint = Checkpoint([Definition("failed"), Definition("budget"), Definition("cancelled")],
        [
            new("failed", TaskGraphTaskStatus.Failed), new("budget", TaskGraphTaskStatus.BudgetExhausted),
            new("cancelled", TaskGraphTaskStatus.Cancelled)
        ]);

        Assert.Equal(TaskGraphRunStatus.Failed, TaskGraphProgress.Inspect(checkpoint).Status);
    }

    [Fact]
    public void Inspection_IsDeterministicAndLeavesCheckpointUnchanged()
    {
        var checkpoint = Checkpoint([Definition("deliver", "review", "test"), Definition("review"), Definition("test")],
            [new("deliver"), Accepted("review"), new("test", TaskGraphTaskStatus.AwaitingAcceptance)]);
        var before = JsonSerializer.Serialize(checkpoint);

        var first = TaskGraphProgress.Inspect(checkpoint);
        var second = TaskGraphProgress.Inspect(checkpoint);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.ReadyTaskIds.ToArray(), second.ReadyTaskIds.ToArray());
        Assert.Equal(first.BlockedTasks.ToArray(), second.BlockedTasks.ToArray());
        Assert.Equal(first.ActiveClaims, second.ActiveClaims);
        Assert.Equal(before, JsonSerializer.Serialize(checkpoint));
        Assert.Equal(new TaskGraphDependencyBlock("deliver", "test", TaskGraphTaskStatus.AwaitingAcceptance),
            Assert.Single(first.BlockedTasks));
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
