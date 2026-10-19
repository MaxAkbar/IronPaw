using System.Collections.Immutable;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

public sealed class ExecutionTimelineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(2);
    private static readonly CallerIdentity Owner = CallerIdentity.LocalOwner();

    [Fact]
    public void CompletionClaimsCannotAcceptTasksOrUnblockDependencies()
    {
        var graph = Graph();
        var claimedSuccess = graph.Tasks[0] with { Status = TaskGraphTaskStatus.Succeeded };
        var snapshot = Project(graph with { Tasks = [claimedSuccess, graph.Tasks[1]] });
        Assert.Equal(0, snapshot.AcceptedCount);
        Assert.Equal("Awaiting checks", snapshot.Tasks[0].Status);
        Assert.Equal("Waiting", snapshot.Tasks[1].Status);
        Assert.Equal("producer", snapshot.Tasks[1].WaitingFor);

        snapshot = Project(graph with { Tasks = [Accepted(), graph.Tasks[1]] });
        Assert.Equal(1, snapshot.AcceptedCount);
        Assert.Equal("Accepted", snapshot.Tasks[0].Status);
        Assert.Equal("Ready", snapshot.Tasks[1].Status);
        Assert.Equal("—", snapshot.Tasks[1].WaitingFor);
        Assert.Contains("no build/test check configured", snapshot.Tasks[0].Checks);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, "Tests: no complete passing report")]
    [InlineData(4, 3, 0, 1, "Tests: no complete passing report")]
    [InlineData(4, 3, 1, 0, "Tests: no complete passing report")]
    [InlineData(4, 3, 0, 0, "Tests: no complete passing report")]
    [InlineData(4, 4, 0, 0, "Tests: 4/4 passed")]
    public void TestChecksRequirePositiveCompletePassingReceipts(int total, int passed, int failed, int skipped, string expected)
    {
        var graph = WithCheck(Graph(), TaskVerificationKind.Tests);
        var command = Command() with { Completion = Completion() with { Tests = new(total, passed, failed, skipped) } };
        var state = graph.Tasks[0] with { Status = TaskGraphTaskStatus.AwaitingAcceptance, Verifications = [command] };
        var snapshot = Project(graph with { Tasks = [state, graph.Tasks[1]] });
        Assert.Equal(expected, snapshot.Tasks[0].Checks);
        Assert.False(snapshot.Tasks[0].Accepted);
        Assert.Contains($"Tests: {passed}/{total} passed", snapshot.Tasks[0].Details);
        Assert.Equal(expected != "Tests: 4/4 passed", snapshot.Tasks[0].NeedsAttention);
    }

    [Theory]
    [InlineData(TaskVerificationInvocationState.Started, TaskCommandOutcome.Passed, 0, "Build: outcome unknown")]
    [InlineData(TaskVerificationInvocationState.NeedsReconciliation, TaskCommandOutcome.Passed, 0, "Build: outcome unknown")]
    [InlineData(TaskVerificationInvocationState.Completed, TaskCommandOutcome.Failed, 0, "Build: not passed")]
    [InlineData(TaskVerificationInvocationState.Completed, TaskCommandOutcome.Passed, 7, "Build: not passed")]
    [InlineData(TaskVerificationInvocationState.Completed, TaskCommandOutcome.Passed, 0, "Build: passed")]
    public void BuildChecksUseEstablishedOutcomeAndExitCode(TaskVerificationInvocationState state, TaskCommandOutcome outcome, int exitCode, string expected)
    {
        var graph = WithCheck(Graph(), TaskVerificationKind.Build);
        var command = Command() with { State = state, Completion = Completion() with { Outcome = outcome, ExitCode = exitCode } };
        var snapshot = Project(graph with { Tasks = [graph.Tasks[0] with { Verifications = [command] }, graph.Tasks[1]] });
        Assert.Equal(expected, snapshot.Tasks[0].Checks);
        Assert.False(snapshot.Tasks[0].Accepted);
        if (state == TaskVerificationInvocationState.Completed)
            Assert.Equal(outcome != TaskCommandOutcome.Passed || exitCode != 0, snapshot.Tasks[0].NeedsAttention);
        if (state == TaskVerificationInvocationState.NeedsReconciliation)
        {
            Assert.True(snapshot.Tasks[0].NeedsAttention);
            Assert.Equal("Needs review", snapshot.Tasks[0].Status);
        }
    }

    [Fact]
    public void CanonicalChildShowsActiveToolsAndUnknownEffectsWithoutReadingUnrelatedChildren()
    {
        var graph = Graph();
        graph = graph with { Tasks = [graph.Tasks[0] with { Status = TaskGraphTaskStatus.Running, AttemptId = "attempt" }, graph.Tasks[1]] };
        var childId = AgentTaskIdentity.ChildRunId(graph.DefinitionFingerprint, "producer", "attempt").Value;
        var child = Run(childId, "root") with { LeaseOwner = "worker", ToolExecutions = [Tool()] };
        var unrelated = child with { RunId = "unrelated", Request = child.Request with { AgentId = "private-agent" } };
        var active = Project(graph, [child, unrelated]);
        Assert.Equal("Tool in progress: write_file", active.Tasks[0].Activity);
        Assert.False(active.Tasks[0].NeedsAttention);
        Assert.Equal("Working", active.Tasks[0].Status);

        var interrupted = Project(graph, [child with { LeaseOwner = null }]);
        Assert.Equal("Outcome unknown: write_file", interrupted.Tasks[0].Activity);
        Assert.True(interrupted.Tasks[0].NeedsAttention);
        Assert.Equal("Needs review", interrupted.Tasks[0].Status);
        Assert.Equal("Needs review", interrupted.Status);

        var foreign = child with { Request = child.Request with { Caller = Owner with { Id = "foreign" } } };
        Assert.Equal("Task attempt recorded", Project(graph, [foreign]).Tasks[0].Activity);
    }

    [Theory]
    [InlineData(WorkflowControl.Pause, "Pausing; work is settling", "Paused")]
    [InlineData(WorkflowControl.Cancel, "Stopping; cancellation recorded", "Cancelled")]
    public void RequestedControlDoesNotClaimActiveWorkHasAlreadySettled(WorkflowControl control, string overall, string pending)
    {
        var graph = Graph();
        graph = graph with { Control = control, Tasks = [graph.Tasks[0] with { Status = TaskGraphTaskStatus.Running, LeaseToken = "lease", AttemptId = "attempt" }, graph.Tasks[1]] };
        var snapshot = Project(graph);
        Assert.Equal(overall, snapshot.Status);
        Assert.Equal(pending, snapshot.Tasks[1].Status);
    }

    [Fact]
    public void FinishedAgentRunIsNeverReportedAsIndependentlyVerified()
    {
        var root = Run("root", "root") with { Status = WorkflowStatus.Succeeded };
        var snapshot = Workflow(root, [root]);
        Assert.Equal("Run finished", snapshot.Status);
        var row = Assert.Single(snapshot.Tasks);
        Assert.False(row.Accepted);
        Assert.Contains("No independent verification", row.Checks);
        Assert.Equal(0, snapshot.AcceptedCount);
    }

    [Fact]
    public void UnknownChildEffectsRemainVisibleEvenWhenRootHasFinished()
    {
        var root = Run("root", "root") with { Status = WorkflowStatus.Succeeded };
        var child = Run("child", "root") with { ToolExecutions = [Tool()] };
        var snapshot = Workflow(root, [root, child]);
        Assert.Equal("Needs review", snapshot.Status);
        Assert.True(snapshot.Tasks.Single(row => row.Id == "child").NeedsAttention);
        var waiting = child with { ToolExecutions = [Tool() with { State = WorkflowToolState.WaitingForChild }] };
        Assert.Equal("Waiting for a specialist", Workflow(root, [root, waiting]).Tasks.Single(row => row.Id == "child").Activity);
        var approval = child with { ToolExecutions = [], HumanRequests = [new("ask", WorkflowHumanRequestKind.ToolApproval, "Approve?", "source", Now)] };
        Assert.Equal("Waiting for your response", Workflow(root, [root, approval]).Tasks.Single(row => row.Id == "child").Activity);
    }

    [Fact]
    public void JournalMergeFiltersForeignAndFutureEntriesAndPreservesEachRevisionStreamAfterClockCorrections()
    {
        var graph = Graph() with { Revision = 2 };
        var child = Run("child", "root") with { Revision = 2 };
        var foreign = Run("foreign", "root") with { Request = child.Request with { Caller = Owner with { IsOwner = false } } };
        var detail = Detail(graph, [child, foreign]) with {
            Journal = [new("graph", 2, Now.AddSeconds(-10), "task_result"), new("graph", 1, Now, "accepted"),
                new("graph", 1, Now, "accepted"), new("graph", 3, Now, "future"), new("other", 1, Now, "private")],
            InvocationJournal = [new("child", 2, Now.AddSeconds(-20), "tool_result"), new("child", 1, Now, "tool_started"),
                new("child", 3, Now, "future"), new("foreign", 1, Now, "private")]
        };
        var snapshot = ExecutionTimelineBuilder.ForGraph(detail, Now);
        Assert.Equal(4, snapshot.TotalEvents);
        Assert.Equal(new[] { "graph/graph/1", "graph/graph/2", "run/child/1", "run/child/2" }, snapshot.Events.Select(entry => entry.Id));
        Assert.Contains("acceptance may still be pending", snapshot.Events[1].Title);
        Assert.Equal(snapshot.Events.ToArray(), ExecutionTimelineBuilder.ForGraph(detail, Now).Events.ToArray());
        Assert.DoesNotContain(snapshot.Events, entry => entry.EventType is "private" or "future");
    }

    [Fact]
    public void OrdinaryWorkflowJournalAlsoFiltersCallerRootAndFutureRevisions()
    {
        var root = Run("root", "root") with { Revision = 1 };
        var foreign = Run("foreign", "root") with { Request = root.Request with { Caller = Owner with { Id = "foreign" } }, Revision = 1 };
        var other = Run("other", "other") with { Revision = 1 };
        var snapshot = Workflow(root, [root, foreign, other], [new("root", 1, Now, "succeeded"), new("root", 2, Now, "future"),
            new("foreign", 1, Now, "private"), new("other", 1, Now, "private")]);
        Assert.Single(snapshot.Tasks);
        Assert.Equal("run/root/1", Assert.Single(snapshot.Events).Id);
        Assert.Contains("verification is separate", snapshot.Events[0].Title);
    }

    [Fact]
    public void LargeJournalHasAnExplicitBoundAndBudgetDoesNotInventMoney()
    {
        var graph = Graph() with { Revision = 500 };
        var detail = Detail(graph) with { Journal = Enumerable.Range(1, 500).Select(revision => new TaskGraphJournalEntry("graph", revision, Now, "task_acceptance")).ToArray() };
        var snapshot = ExecutionTimelineBuilder.ForGraph(detail, Now);
        Assert.Equal(250, snapshot.Events.Length);
        Assert.Equal(500, snapshot.TotalEvents);
        Assert.Equal(250, snapshot.HiddenEvents);
        Assert.Equal(251, snapshot.Events[0].Revision);
        Assert.Contains("Host acceptance decision recorded", snapshot.Events[0].Title);
        Assert.Contains("Tokens charged: 10; reserved: 20; remaining: 70", snapshot.Summary);
        Assert.DoesNotContain("$", snapshot.Summary);
    }

    [Fact]
    public void TextAndSavedCommandOutputAreBoundedAndControlCharactersAreRemoved()
    {
        var graph = WithCheck(Graph(), TaskVerificationKind.Build);
        var command = Command() with { Completion = Completion() with { StandardOutput = Log(new string('x', 3000) + "\0SECRET") } };
        var snapshot = Project(graph with { Graph = graph.Graph with { Objective = "\0\u001b" + new string('a', 500) },
            Tasks = [graph.Tasks[0] with { Verifications = [command] }, graph.Tasks[1]] });
        Assert.True(snapshot.Title.Length <= 241);
        Assert.DoesNotContain('\0', snapshot.Title);
        Assert.DoesNotContain('\u001b', snapshot.Title);
        Assert.Contains(new string('x', 2000) + "…", snapshot.Tasks[0].Details);
        Assert.DoesNotContain("SECRET", snapshot.Tasks[0].Details);
    }

    private static TaskGraphCheckpoint Graph() => new(new("graph", "Create and consume an artifact", [new("producer", "Create result"),
        new("consumer", "Use result") { Dependencies = ["producer"] }]), "fingerprint", Owner, 1, [new("producer"), new("consumer")], Now, Now,
        AgentBinding: new("root", "source", new string('0', 40), "managed", "local", "rule-based-v1", new(ToolProfile.Coding), new()));
    private static TaskGraphCheckpoint WithCheck(TaskGraphCheckpoint graph, TaskVerificationKind kind) => graph with { AgentBinding = graph.AgentBinding! with {
        VerificationProfiles = [new("check", "executable", "digest", [], TimeSpan.FromSeconds(30)) { Kind = kind }],
        CommandVerificationRules = [new("producer", "criterion", "check")]
    } };
    private static TaskGraphTaskState Accepted() => new("producer", TaskGraphTaskStatus.Succeeded, "attempt", Acceptance:
        new(TaskAcceptanceOutcome.Accepted, "host", "Artifacts checked", "fingerprint", "producer", "attempt", "digest", "revision", Now));
    private static TaskVerificationInvocation Command() => new("invocation", "check", "profile-digest", "result-digest", "revision",
        TaskVerificationInvocationState.Completed, Now, Completion());
    private static TaskVerificationLog Log(string text = "") => new(text, "log-digest", text.Length, false);
    private static TaskVerificationCompletion Completion() => new(TaskCommandOutcome.Passed, "worker", "Observed exit", 0, Log(), Log());
    private static WorkflowCheckpoint Run(string id, string root) => new(id, root, id == root ? null : root, "task-" + id,
        new(new("timeline"), "Implement and verify", "local", "rule-based-v1", new(ToolProfile.Coding), Owner), Now, Now);
    private static WorkflowToolExecution Tool() => new("tool-execution", new("tool-call", "write_file", JsonSerializer.SerializeToElement(new { path = "result.txt" })));
    private static WorkflowBudgetSnapshot Budget() => new("root", new(MaxTotalTokens: 100), 10, 20, TimeSpan.FromSeconds(3), null);
    private static TaskGraphHostDetail Detail(TaskGraphCheckpoint graph, IReadOnlyList<WorkflowCheckpoint>? runs = null) =>
        new(graph, TaskGraphProgress.Inspect(graph), runs ?? [], [], new(2, 1), Budget());
    private static ExecutionTimelineSnapshot Project(TaskGraphCheckpoint graph, IReadOnlyList<WorkflowCheckpoint>? runs = null) => ExecutionTimelineBuilder.ForGraph(Detail(graph, runs), Now);
    private static ExecutionTimelineSnapshot Workflow(WorkflowCheckpoint root, IReadOnlyList<WorkflowCheckpoint> runs, IReadOnlyList<WorkflowJournalEntry>? journal = null) =>
        ExecutionTimelineBuilder.ForWorkflow(root, runs, journal ?? [], new(2, 1), Budget(), Now);
}
