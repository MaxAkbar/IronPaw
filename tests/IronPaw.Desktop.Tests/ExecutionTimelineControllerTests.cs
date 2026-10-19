using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.Desktop;

namespace IronPaw.Desktop.Tests;

[TestClass]
public sealed class ExecutionTimelineControllerTests
{
    [TestMethod]
    public async Task SelectionChangeCancelsOldReadAndRejectsItsLateSuccess()
    {
        using var reads = new TimelineReadCoordinator();
        var first = new TaskCompletionSource<ExecutionTimelineSnapshot>();
        var second = new TaskCompletionSource<ExecutionTimelineSnapshot>();
        CancellationToken oldToken = default;
        var displayed = new List<string>();
        var oldRead = reads.ReadAsync(ct => { oldToken = ct; return first.Task; }, value => displayed.Add(value.Id), Fail);
        Assert.IsTrue(reads.IsReading);
        var newRead = reads.ReadAsync(_ => second.Task, value => displayed.Add(value.Id), Fail);
        Assert.IsTrue(oldToken.IsCancellationRequested);
        second.SetResult(Snapshot("second"));
        await newRead;
        Assert.IsFalse(reads.IsReading);
        first.SetResult(Snapshot("first"));
        await oldRead;
        Assert.AreSequenceEqual(new[] { "second" }, displayed);
    }

    [TestMethod]
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
        Assert.IsTrue(reads.IsReading);
        Assert.IsEmpty(errors);
        second.SetResult(Snapshot("current"));
        await newRead;
        Assert.IsFalse(reads.IsReading);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClearingOrClosingTheViewRejectsLateResults(bool closing)
    {
        using var reads = new TimelineReadCoordinator();
        var source = new TaskCompletionSource<ExecutionTimelineSnapshot>();
        CancellationToken token = default;
        var applied = false;
        var read = reads.ReadAsync(ct => { token = ct; return source.Task; }, _ => applied = true, Fail);
        if (closing) reads.Dispose(); else reads.Invalidate();
        Assert.IsTrue(token.IsCancellationRequested);
        source.SetResult(Snapshot("late"));
        await read;
        Assert.IsFalse(applied);
        Assert.IsFalse(reads.IsReading);
        if (closing)
        {
            var invoked = false;
            await reads.ReadAsync(_ => { invoked = true; return source.Task; }, _ => applied = true, Fail);
            Assert.IsFalse(invoked);
        }
    }

    [TestMethod]
    public async Task CurrentFailureIsShownAndNextRefreshCanRecover()
    {
        using var reads = new TimelineReadCoordinator();
        Exception? shown = null;
        await reads.ReadAsync(_ => Task.FromException<ExecutionTimelineSnapshot>(new InvalidOperationException("store unavailable")), _ => { }, error => shown = error);
        Assert.AreEqual("store unavailable", shown?.Message);
        Assert.IsFalse(reads.IsReading);
        string? id = null;
        await reads.ReadAsync(_ => Task.FromResult(Snapshot("recovered")), value => id = value.Id, Fail);
        Assert.AreEqual("recovered", id);
    }

    [TestMethod]
    public async Task BrowsingOrdinaryWorkflowUsesSavedEvidenceWithoutInvokingCommands()
    {
        var store = new WorkflowRecoveryControllerTests.MemoryStore();
        var root = WorkflowRecoveryControllerTests.MakeRun("root", "root", null) with { Revision = 1, Status = WorkflowStatus.Succeeded };
        store.Runs[root.RunId] = root;
        var agent = new WorkflowRecoveryControllerTests.FakeOrchestrator(store);
        var controller = new ExecutionTimelineController(new(agent, store), null);
        var choice = Assert.ContainsSingle(await controller.ListAsync(default));
        var snapshot = await controller.InspectAsync(choice, default);
        Assert.AreEqual(ExecutionTimelineKind.Workflow, choice.Kind);
        Assert.AreEqual("Run finished", snapshot.Status);
        Assert.AreEqual("root", snapshot.Id);
        Assert.AreEqual("run/root/1", Assert.ContainsSingle(snapshot.Events).Id);
        Assert.IsEmpty(agent.Calls);
        Assert.AreEqual(root, store.Runs[root.RunId]);
    }

    [TestMethod]
    [DataRow("other", true)]
    [DataRow("local-owner", false)]
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
        Assert.AreEqual("root", Assert.ContainsSingle(await controller.ListAsync(default)).Id);
        var snapshot = await controller.InspectAsync(new(ExecutionTimelineKind.Workflow, "root", "title", root.UpdatedAt), default);
        Assert.AreEqual("root", Assert.ContainsSingle(snapshot.Tasks).Id);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => controller.InspectAsync(new(ExecutionTimelineKind.Workflow, "foreign", "title", foreign.UpdatedAt), default));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => recovery.CancelAsync(new("foreign"), default));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => recovery.PauseAsync(new("foreign"), default));
        Assert.IsEmpty(agent.Calls);
    }

    private static ExecutionTimelineSnapshot Snapshot(string id) => new(id, id, "Ready", "summary", DateTimeOffset.UtcNow, [], [], 0);
    private static void Fail(Exception error) => Assert.Fail(error.ToString());
}
