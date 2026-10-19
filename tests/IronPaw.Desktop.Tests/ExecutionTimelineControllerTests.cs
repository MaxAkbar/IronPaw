using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.Desktop;

namespace IronPaw.Desktop.Tests;

public sealed class ExecutionTimelineControllerTests
{
    [Fact]
    public async Task SelectionChangeCancelsOldReadAndRejectsItsLateSuccess()
    {
        using var reads = new TimelineReadCoordinator();
        var first = new TaskCompletionSource<ExecutionTimelineSnapshot>();
        var second = new TaskCompletionSource<ExecutionTimelineSnapshot>();
        CancellationToken oldToken = default;
        var displayed = new List<string>();
        var oldRead = reads.ReadAsync(ct => { oldToken = ct; return first.Task; }, value => displayed.Add(value.Id), Fail);
        Assert.True(reads.IsReading);
        var newRead = reads.ReadAsync(_ => second.Task, value => displayed.Add(value.Id), Fail);
        Assert.True(oldToken.IsCancellationRequested);
        second.SetResult(Snapshot("second"));
        await newRead;
        Assert.False(reads.IsReading);
        first.SetResult(Snapshot("first"));
        await oldRead;
        Assert.Equal(new[] { "second" }, displayed);
    }

    [Fact]
    public async Task StaleFailureCannotReplaceCurrentSelectionAndOldReadCannotClearNewReadState()
    {
        using var reads = new TimelineReadCoordinator();
        var first = new TaskCompletionSource<ExecutionTimelineSnapshot>();
        var second = new TaskCompletionSource<ExecutionTimelineSnapshot>();
        var errors = new List<string>();
        var oldRead = reads.ReadAsync(_ => first.Task, _ => { }, error => errors.Add(error.Message));
        var newRead = reads.ReadAsync(_ => second.Task, _ => { }, error => errors.Add(error.Message));
        first.SetException(new InvalidOperationException("stale error"));
        await oldRead;
        Assert.True(reads.IsReading);
        Assert.Empty(errors);
        second.SetResult(Snapshot("current"));
        await newRead;
        Assert.False(reads.IsReading);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearingOrClosingTheViewRejectsLateResults(bool closing)
    {
        using var reads = new TimelineReadCoordinator();
        var source = new TaskCompletionSource<ExecutionTimelineSnapshot>();
        CancellationToken token = default;
        var applied = false;
        var read = reads.ReadAsync(ct => { token = ct; return source.Task; }, _ => applied = true, Fail);
        if (closing) reads.Dispose(); else reads.Invalidate();
        Assert.True(token.IsCancellationRequested);
        source.SetResult(Snapshot("late"));
        await read;
        Assert.False(applied);
        Assert.False(reads.IsReading);
        if (closing)
        {
            var invoked = false;
            await reads.ReadAsync(_ => { invoked = true; return source.Task; }, _ => applied = true, Fail);
            Assert.False(invoked);
        }
    }

    [Fact]
    public async Task CurrentFailureIsShownAndNextRefreshCanRecover()
    {
        using var reads = new TimelineReadCoordinator();
        Exception? shown = null;
        await reads.ReadAsync(_ => Task.FromException<ExecutionTimelineSnapshot>(new InvalidOperationException("store unavailable")), _ => { }, error => shown = error);
        Assert.Equal("store unavailable", shown?.Message);
        Assert.False(reads.IsReading);
        string? id = null;
        await reads.ReadAsync(_ => Task.FromResult(Snapshot("recovered")), value => id = value.Id, Fail);
        Assert.Equal("recovered", id);
    }

    [Fact]
    public async Task BrowsingOrdinaryWorkflowUsesSavedEvidenceWithoutInvokingCommands()
    {
        var store = new WorkflowRecoveryControllerTests.MemoryStore();
        var root = WorkflowRecoveryControllerTests.MakeRun("root", "root", null) with { Revision = 1, Status = WorkflowStatus.Succeeded };
        store.Runs[root.RunId] = root;
        var agent = new WorkflowRecoveryControllerTests.FakeOrchestrator(store);
        var controller = new ExecutionTimelineController(new(agent, store), null);
        var choice = Assert.Single(await controller.ListAsync(default));
        var snapshot = await controller.InspectAsync(choice, default);
        Assert.Equal(ExecutionTimelineKind.Workflow, choice.Kind);
        Assert.Equal("Run finished", snapshot.Status);
        Assert.Equal("root", snapshot.Id);
        Assert.Equal("run/root/1", Assert.Single(snapshot.Events).Id);
        Assert.Empty(agent.Calls);
        Assert.Equal(root, store.Runs[root.RunId]);
    }

    [Theory]
    [InlineData("other", true)]
    [InlineData("local-owner", false)]
    public async Task ForeignCallerCannotBrowseOrControlSavedWork(string callerId, bool isOwner)
    {
        var store = new WorkflowRecoveryControllerTests.MemoryStore();
        var foreign = WorkflowRecoveryControllerTests.MakeRun("foreign", "foreign", null);
        foreign = foreign with { Request = foreign.Request with { Caller = foreign.Request.Caller with { Id = callerId, IsOwner = isOwner } } };
        store.Runs[foreign.RunId] = foreign;
        var root = WorkflowRecoveryControllerTests.MakeRun("root", "root", null);
        store.Runs[root.RunId] = root;
        var child = foreign with { RunId = "foreign-child", RootRunId = "root", ParentRunId = "root" };
        store.Runs[child.RunId] = child;
        var agent = new WorkflowRecoveryControllerTests.FakeOrchestrator(store);
        var recovery = new WorkflowRecoveryController(agent, store);
        var controller = new ExecutionTimelineController(recovery, null);
        Assert.Equal("root", Assert.Single(await controller.ListAsync(default)).Id);
        var snapshot = await controller.InspectAsync(new(ExecutionTimelineKind.Workflow, "root", "title", root.UpdatedAt), default);
        Assert.Equal("root", Assert.Single(snapshot.Tasks).Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => controller.InspectAsync(new(ExecutionTimelineKind.Workflow, "foreign", "title", foreign.UpdatedAt), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => recovery.CancelAsync(new("foreign"), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => recovery.PauseAsync(new("foreign"), default));
        Assert.Empty(agent.Calls);
    }

    private static ExecutionTimelineSnapshot Snapshot(string id) => new(id, id, "Ready", "summary", DateTimeOffset.UtcNow, [], [], 0);
    private static void Fail(Exception error) => Assert.Fail(error.ToString());
}
