using System.Collections.Immutable;
using System.Text.Json;
using CSharpDB.Engine;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.AgentCore.Persistence.CSharpDb.Tests;

[TestClass]
public sealed class AgentTaskGraphStoreTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "IronPaw-AgentGraph-" + Guid.NewGuid().ToString("n"));
    private string DatabasePath => Path.Combine(_directory, "workflows.cdb");
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly CallerIdentity Caller = new("caller", "Original caller", true);

    [TestMethod]
    public async Task AcceptanceAtomicallyBindsImmutableAgentConfigurationAndNonExecutableRoot()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph(Task("a"));
        var binding = Binding(graph);
        var accepted = await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, binding, Ct);
        Assert.AreEqual(2, accepted.SchemaVersion);
        var root = (await store.GetAsync(new(binding.RootRunId), Ct))!;
        Assert.AreEqual(root.RunId, root.RootRunId);
        Assert.IsNull(root.ParentRunId);
        Assert.AreEqual(binding.SharedBudget, root.Budget);
        Assert.AreEqual(0, root.Usage!.ModelCalls);
        Assert.AreEqual(0, (await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, binding, Ct)).Revision);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.AcceptAgentTaskGraphAsync(graph, Caller, 1, binding with { Model = "changed" }, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.AcceptTaskGraphAsync(graph, Caller, 1, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(root with { LeaseOwner = "forbidden", Status = WorkflowStatus.Running }, root.Revision, "must-not-execute", Ct));
        Assert.ContainsSingle(await store.ReadJournalAsync(new(binding.RootRunId), Ct));
    }

    [TestMethod]
    public async Task GenericGraphCannotBeReboundAndRemainsSchemaOne()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph(Task("a"));
        await store.AcceptTaskGraphAsync(graph, Caller, 1, Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.AcceptAgentTaskGraphAsync(graph, Caller, 1, Binding(graph), Ct));
        Assert.AreEqual(1, (await store.GetTaskGraphAsync(graph.GraphId, Ct))!.SchemaVersion);
        Assert.IsNull(await store.GetAsync(AgentTaskIdentity.RootRunId(graph.GraphId), Ct));
    }

    [TestMethod]
    public async Task GraphJournalFailureRollsBackSyntheticRootAndBinding()
    {
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath)) { }
        await using (var database = await Database.OpenAsync(DatabasePath))
        {
            await using var result = await database.ExecuteAsync("INSERT INTO task_graph_journal VALUES (1, 'graph', 0, '2026-10-14T00:00:00+00:00', 'injected', NULL, NULL)");
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph(Task("a"));
        await Assert.ThrowsAsync<Exception>(() => reopened.AcceptAgentTaskGraphAsync(graph, Caller, 1, Binding(graph), Ct));
        Assert.IsNull(await reopened.GetTaskGraphAsync(graph.GraphId, Ct));
        Assert.IsNull(await reopened.GetAsync(AgentTaskIdentity.RootRunId(graph.GraphId), Ct));
        Assert.IsEmpty(await reopened.ReadJournalAsync(AgentTaskIdentity.RootRunId(graph.GraphId), Ct));
    }

    [TestMethod]
    public async Task ChildCreationAndEveryAdmissionRequireMatchingLiveGraphClaim()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph(Task("a"));
        var accepted = await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, Binding(graph), Ct);
        var claim = (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!;
        var child = Child(accepted, claim);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateAsync(child with { Request = child.Request with { Provider = "different" } }, Ct));
        await store.CreateAsync(child, Ct);
        await store.AbandonTaskClaimAsync(claim, "Executor stopped before dispatch", Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(child with { LeaseOwner = "bypass", Status = WorkflowStatus.Running }, 0, "bypass-claim", Ct));
        var reclaimed = await store.ReclaimAgentTaskAsync(graph.GraphId, "a", Caller, Ct);
        Assert.AreEqual(claim.AttemptId, reclaimed.AttemptId);
        Assert.AreNotEqual(claim.LeaseToken, reclaimed.LeaseToken);
        child = await store.SaveAsync(child with { LeaseOwner = "valid", Status = WorkflowStatus.Running }, 0, "claimed", Ct);
        var nested = child with
        {
            RunId = "nested",
            ParentRunId = child.RunId,
            Request = child.Request with { SessionKey = new("nested"), ParentRunId = new(child.RunId), RequestedRunId = new("nested") },
            Revision = 0,
            LeaseOwner = null
        };
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateAsync(nested, Ct));
    }

    [TestMethod]
    public async Task SharedModelCallBudgetCannotResetAcrossIndependentAgentTasks()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph(Task("a"), Task("b"));
        var accepted = await store.AcceptAgentTaskGraphAsync(graph, Caller, 2, Binding(graph) with { SharedBudget = new(1, 10) }, Ct);
        var a = Child(accepted, (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!);
        var b = Child(accepted, (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!);
        await store.CreateAsync(a, Ct);
        await store.CreateAsync(b, Ct);
        await store.SaveAsync(a with { Usage = new(ModelCalls: 1), ModelCalls = [new("model-a")] }, 0, "model_started", Ct);
        await Assert.ThrowsExactlyAsync<WorkflowBudgetExceededException>(() => store.SaveAsync(b with { Usage = new(ModelCalls: 1), ModelCalls = [new("model-b")] }, 0, "model_started", Ct));
        Assert.AreEqual(1, (await ((IWorkflowStore)store).GetUsageAsync(new(accepted.AgentBinding!.RootRunId), Ct)).ModelCalls);
        Assert.AreEqual(0, (await store.GetAsync(new(b.RunId), Ct))!.Revision);
    }

    [TestMethod]
    public async Task SharedTokenReservationsCoverAllBoundChildren()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph(Task("a"), Task("b"));
        var accepted = await store.AcceptAgentTaskGraphAsync(graph, Caller, 2, Binding(graph) with { SharedBudget = new(5, 10, MaxTotalTokens: 10) }, Ct);
        var a = Child(accepted, (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!);
        var b = Child(accepted, (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!);
        await store.CreateAsync(a, Ct);
        await store.CreateAsync(b, Ct);
        await store.SaveAsync(a with { Usage = new(ModelCalls: 1), ModelCalls = [new("a", Attempts: [new("attempt-a", 5, 2)])] }, 0, "model_started", Ct);
        await Assert.ThrowsExactlyAsync<WorkflowBudgetExceededException>(() => store.SaveAsync(b with { Usage = new(ModelCalls: 1), ModelCalls = [new("b", Attempts: [new("attempt-b", 6, 2)])] }, 0, "model_started", Ct));
        Assert.AreEqual(5, (await store.GetBudgetAsync(new(accepted.AgentBinding!.RootRunId), Ct)).ReservedTokens);
    }

    [TestMethod]
    public async Task GraphAndChildLeasesShareOneExecutionIntervalAndExcludeSettledTime()
    {
        var time = new ManualTimeProvider();
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, timeProvider: time);
        var graph = Graph(Task("a"));
        var accepted = await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, Binding(graph), Ct);
        var claim = (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!;
        time.Advance(TimeSpan.FromSeconds(5));
        var child = Child(accepted, claim) with { LeaseOwner = "child", Status = WorkflowStatus.Running };
        await store.CreateAsync(child, Ct);
        time.Advance(TimeSpan.FromSeconds(3));
        await store.SaveAsync(child with { LeaseOwner = null, Status = WorkflowStatus.Interrupted }, 0, "settled", Ct);
        await store.RecordTaskResultAsync(claim, Failure(claim), Ct);
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.AreEqual(TimeSpan.FromSeconds(8), (await store.GetBudgetAsync(new(accepted.AgentBinding!.RootRunId), Ct)).ExecutionTime);
    }

    [TestMethod]
    public async Task ReopenChargesUncleanGraphDowntimeThenStopsClockAndRequiresExplicitSafeReclaim()
    {
        var time = new ManualTimeProvider();
        var graph = Graph(Task("a"));
        var binding = Binding(graph);
        string attempt;
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, timeProvider: time))
        {
            await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, binding, Ct);
            attempt = (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!.AttemptId;
        }
        time.Advance(TimeSpan.FromSeconds(7));
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, timeProvider: time);
        Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, (await reopened.GetTaskGraphAsync(graph.GraphId, Ct))!.Tasks[0].Status);
        Assert.AreEqual(TimeSpan.FromSeconds(7), (await reopened.GetBudgetAsync(new(binding.RootRunId), Ct)).ExecutionTime);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TimeSpan.FromSeconds(7), (await reopened.GetBudgetAsync(new(binding.RootRunId), Ct)).ExecutionTime);
        Assert.IsNull(await reopened.ClaimTaskAsync(graph.GraphId, Caller, Ct));
        Assert.AreEqual(attempt, (await reopened.ReclaimAgentTaskAsync(graph.GraphId, "a", Caller, Ct)).AttemptId);
    }

    [TestMethod]
    public async Task MirroredPauseCannotBeClearedThroughWorkflowAndWaitsForGraphAndChildLeases()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph(Task("a"));
        var accepted = await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, Binding(graph), Ct);
        var claim = (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!;
        var child = Child(accepted, claim) with { LeaseOwner = "child", Status = WorkflowStatus.Running };
        await store.CreateAsync(child, Ct);
        await store.ControlTaskGraphAsync(graph.GraphId, WorkflowControl.Pause, Caller, Ct);
        var root = (await store.GetAsync(new(accepted.AgentBinding!.RootRunId), Ct))!;
        Assert.AreEqual(WorkflowControl.Pause, root.Control);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(root with { Control = WorkflowControl.None }, root.Revision, "resume_requested", Ct));
        await store.AbandonTaskClaimAsync(claim, "Pause settled at graph boundary", Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.ControlTaskGraphAsync(graph.GraphId, WorkflowControl.None, Caller, Ct));
        await store.SaveAsync(child with { LeaseOwner = null, Status = WorkflowStatus.Interrupted }, 0, "settled", Ct);
        await store.ControlTaskGraphAsync(graph.GraphId, WorkflowControl.None, Caller, Ct);
        await store.ControlTaskGraphAsync(graph.GraphId, WorkflowControl.Cancel, Caller, Ct);
        Assert.AreEqual(WorkflowControl.Cancel, (await store.GetAsync(new(accepted.AgentBinding.RootRunId), Ct))!.Control);
        await Assert.ThrowsExactlyAsync<WorkflowCancelledException>(() => store.ReclaimAgentTaskAsync(graph.GraphId, "a", Caller, Ct));
    }

    [TestMethod]
    public async Task UnknownToolEffectsBlockExplicitReclaimUntilKnownReceiptIsRecorded()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph(Task("a"));
        var accepted = await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, Binding(graph), Ct);
        var claim = (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!;
        var tool = new WorkflowToolExecution("effect", new("effect", "write_file", JsonDocument.Parse("{}").RootElement.Clone()));
        var child = Child(accepted, claim) with { ToolExecutions = [tool], Usage = new(ToolCalls: 1) };
        await store.CreateAsync(child, Ct);
        await store.AbandonTaskClaimAsync(claim, "Lost outcome", Ct);
        await Assert.ThrowsExactlyAsync<WorkflowReconciliationRequiredException>(() => store.ReclaimAgentTaskAsync(graph.GraphId, "a", Caller, Ct));
        await store.SaveAsync(child with { ToolExecutions = [tool with { State = WorkflowToolState.Completed, Result = ToolResult.Success("Observed saved file"), ReconciliationNote = "Inspected external state" }] }, 0, "reconciled", Ct);
        Assert.AreEqual(claim.AttemptId, (await store.ReclaimAgentTaskAsync(graph.GraphId, "a", Caller, Ct)).AttemptId);
    }

    [TestMethod]
    public async Task PendingVerificationNeedsTrustedAcceptanceAndCanEstablishBudgetExhaustion()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph(Task("a"), Task("b", "a"));
        await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, Binding(graph), Ct);
        var claim = (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!;
        var proposal = new WorkflowTaskResult(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId,
            WorkflowTaskStatus.AwaitingVerification, "revision", "Awaiting trusted artifact assessment");
        await store.RecordTaskResultAsync(claim, proposal, Ct);
        await store.RecordTaskAcceptanceAsync(claim, new(TaskAcceptanceOutcome.BudgetExhausted, "host", "No execution time remains for verification"), Ct);
        var saved = (await store.GetTaskGraphAsync(graph.GraphId, Ct))!;
        Assert.AreEqual(TaskGraphTaskStatus.BudgetExhausted, saved.Tasks[0].Status);
        Assert.AreEqual(TaskGraphTaskStatus.Pending, saved.Tasks[1].Status);
        Assert.IsNull(await store.ClaimTaskAsync(graph.GraphId, Caller, Ct));
    }

    [TestMethod]
    [DataRow("no-criteria")]
    [DataRow("missing-artifact")]
    [DataRow("unresolved-issue")]
    [DataRow("nonpassing-evidence")]
    public async Task TrustedAcceptanceCannotBypassCandidateStructuralRequirements(string defect)
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var task = Task("a");
        if (defect == "no-criteria") task = task with { AcceptanceCriteria = [] };
        if (defect == "missing-artifact") task = task with { ExpectedArtifacts = [new("output", "Required output", TaskArtifactKind.File, "result.txt")] };
        var graph = Graph(task);
        await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, Binding(graph), Ct);
        var claim = (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!;
        var proposal = new WorkflowTaskResult(graph.GraphId, claim.GraphFingerprint, task.TaskId,
            WorkflowTaskStatus.AwaitingVerification, "revision", "Awaiting trusted verification")
        {
            UnresolvedIssues = defect == "unresolved-issue" ? ["Known unresolved problem"] : [],
            VerificationEvidence = defect == "nonpassing-evidence" ? [new("evidence", "criterion", "revision", TaskVerificationOutcome.Failed, "Required check failed")] : []
        };
        var before = await store.RecordTaskResultAsync(claim, proposal, Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.RecordTaskAcceptanceAsync(claim,
            new(TaskAcceptanceOutcome.Accepted, "trusted-host", "Must not bypass protocol requirements"), Ct));
        var saved = (await store.GetTaskGraphAsync(graph.GraphId, Ct))!;
        Assert.AreEqual(before.Revision, saved.Revision);
        Assert.AreEqual(TaskGraphTaskStatus.AwaitingAcceptance, saved.Tasks[0].Status);
        Assert.IsNull(saved.Tasks[0].Acceptance);
    }

    private TaskGraphAgentBinding Binding(WorkflowTaskGraph graph) => new(AgentTaskIdentity.RootRunId(graph.GraphId).Value,
        Path.Combine(_directory, "source"), new string('a', 40), Path.Combine(_directory, "managed"), "test", "test-model",
        new(ToolProfile.Minimal), new(10, 10));
    private static WorkflowTaskGraph Graph(params WorkflowTaskDefinition[] tasks) => new("graph", "Deliver objective", tasks.ToImmutableArray());
    private static WorkflowTaskDefinition Task(string id, params string[] dependencies) => new(id, "Execute " + id)
    {
        Dependencies = dependencies.ToImmutableArray(),
        AcceptanceCriteria = [new("criterion", "Verify exact result")]
    };
    private static WorkflowCheckpoint Child(TaskGraphCheckpoint accepted, TaskGraphClaim claim)
    {
        var request = AgentTaskBindingValidator.CreateChildRequest(accepted.Graph, accepted.Caller, accepted.AgentBinding!, claim.Task, claim.AttemptId);
        return new(request.RequestedRunId!.Value.Value, accepted.AgentBinding!.RootRunId, accepted.AgentBinding.RootRunId,
            claim.Task.TaskId, request, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Budget: request.Budget, Usage: new());
    }
    private static WorkflowTaskResult Failure(TaskGraphClaim claim) => new(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId,
        WorkflowTaskStatus.Failed, "revision", "Established failure");
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
