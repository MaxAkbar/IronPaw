using System.Collections.Immutable;
using System.Text.Json;
using CSharpDB.Engine;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence.CSharpDb.Tests;

[TestClass]
public sealed class TaskVerificationStoreTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "IronPaw-VerificationStore-" + Guid.NewGuid().ToString("n"));
    private string DatabasePath => Path.Combine(_directory, "workflows.cdb");
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly CallerIdentity Caller = new("owner", "Owner", true);

    [TestMethod]
    public async Task AdmissionIsOnceOnlyAndCompletedReceiptIsReusableAndImmutable()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var claim = await CandidateAsync(store);
        var admitted = await store.BeginTaskVerificationAsync(claim, "build", Ct);
        Assert.IsTrue(admitted.ShouldExecute);
        Assert.AreEqual(TaskVerificationInvocationState.Started, admitted.Invocation.State);
        await Assert.ThrowsExactlyAsync<WorkflowReconciliationRequiredException>(() => store.BeginTaskVerificationAsync(claim, "build", Ct));
        var complete = Completion();
        await store.CompleteTaskVerificationAsync(claim, admitted.Invocation.InvocationId, complete, Ct);
        await store.CompleteTaskVerificationAsync(claim, admitted.Invocation.InvocationId, complete, Ct);
        var replay = await store.BeginTaskVerificationAsync(claim, "build", Ct);
        Assert.IsFalse(replay.ShouldExecute);
        Assert.AreEqual(complete, replay.Invocation.Completion);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CompleteTaskVerificationAsync(claim,
            admitted.Invocation.InvocationId, complete with { Explanation = "Replaced evidence" }, Ct));
        await store.RecordTaskAcceptanceAsync(claim, new(TaskAcceptanceOutcome.Accepted, "host", "Required build passed"), Ct);
        Assert.AreEqual(TaskGraphTaskStatus.Succeeded, (await store.GetTaskGraphAsync("graph", Ct))!.Tasks[0].Status);
    }

    [TestMethod]
    public async Task ConcurrentAdmissionStartsOneInvocation()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var claim = await CandidateAsync(store);
        async Task<bool> AdmitAsync()
        {
            try { return (await store.BeginTaskVerificationAsync(claim, "build", Ct)).ShouldExecute; }
            catch (WorkflowReconciliationRequiredException) { return false; }
        }
        Assert.AreEqual(1, (await Task.WhenAll(AdmitAsync(), AdmitAsync(), AdmitAsync())).Count(value => value));
        Assert.ContainsSingle((await store.GetTaskGraphAsync("graph", Ct))!.Tasks[0].Verifications);
    }

    [TestMethod]
    public async Task RestartFencesUnknownWorkerAndRequiresExplicitOutcomeBeforeReplay()
    {
        TaskGraphClaim claim;
        string invocationId;
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            claim = await CandidateAsync(store);
            invocationId = (await store.BeginTaskVerificationAsync(claim, "build", Ct)).Invocation.InvocationId;
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var unknown = (await reopened.GetTaskGraphAsync("graph", Ct))!;
        Assert.AreEqual(TaskGraphTaskStatus.AwaitingAcceptance, unknown.Tasks[0].Status);
        Assert.IsNotNull(unknown.Tasks[0].Result);
        Assert.IsNull(unknown.Tasks[0].LeaseToken);
        Assert.AreEqual(TaskVerificationInvocationState.NeedsReconciliation, unknown.Tasks[0].Verifications[0].State);
        Assert.AreEqual(TaskGraphRunStatus.NeedsReconciliation, TaskGraphProgress.Inspect(unknown).Status);
        Assert.IsNull(await reopened.ClaimTaskAsync("graph", Caller, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => reopened.CompleteTaskVerificationAsync(claim, invocationId, Completion(), Ct));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => reopened.ReconcileTaskVerificationAsync("graph", "task", invocationId,
            Completion(), "Inspected stopped worker and exact evidence", new("other", "Other", true), Ct));
        await reopened.ReconcileTaskVerificationAsync("graph", "task", invocationId, Completion(), "Inspected stopped worker and exact evidence", Caller, Ct);
        var next = (await reopened.ClaimTaskAsync("graph", Caller, Ct))!;
        Assert.AreEqual(claim.AttemptId, next.AttemptId);
        Assert.AreEqual(claim.Inputs!.Digest, next.Inputs!.Digest);
        Assert.IsFalse((await reopened.BeginTaskVerificationAsync(next, "build", Ct)).ShouldExecute);
        await reopened.RecordTaskAcceptanceAsync(next, new(TaskAcceptanceOutcome.Accepted, "host", "Established existing build evidence"), Ct);
    }

    [TestMethod]
    [DataRow(WorkflowControl.Pause)]
    [DataRow(WorkflowControl.Cancel)]
    public async Task ControlsBlockAdmissionButAllowKnownReceipts(WorkflowControl control)
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var claim = await CandidateAsync(store);
        var started = await store.BeginTaskVerificationAsync(claim, "build", Ct);
        await store.ControlTaskGraphAsync("graph", control, Caller, Ct);
        await store.CompleteTaskVerificationAsync(claim, started.Invocation.InvocationId, Completion(), Ct);
        var saved = await store.AbandonTaskClaimAsync(claim, "Stopped after known receipt", Ct);
        Assert.AreEqual(control, saved.Control);
        Assert.AreEqual(TaskVerificationInvocationState.Completed, saved.Tasks[0].Verifications[0].State);
        Assert.IsNull(saved.Tasks[0].LeaseToken);
    }

    [TestMethod]
    public async Task PausedGraphCannotStartACommand()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var claim = await CandidateAsync(store);
        await store.ControlTaskGraphAsync("graph", WorkflowControl.Pause, Caller, Ct);
        await Assert.ThrowsExactlyAsync<WorkflowSuspendedException>(() => store.BeginTaskVerificationAsync(claim, "build", Ct));
        Assert.IsEmpty((await store.GetTaskGraphAsync("graph", Ct))!.Tasks[0].Verifications);
    }

    [TestMethod]
    public async Task UncertainVerificationKeepsClockActiveAcrossPauseAndReopenUntilReconciled()
    {
        var time = new ManualTimeProvider();
        string id;
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, timeProvider: time))
        {
            var claim = await CandidateAsync(store);
            id = (await store.BeginTaskVerificationAsync(claim, "build", Ct)).Invocation.InvocationId;
            await store.ControlTaskGraphAsync("graph", WorkflowControl.Pause, Caller, Ct);
            await store.AbandonTaskClaimAsync(claim, "Worker stopped without receipt", Ct);
        }
        time.Advance(TimeSpan.FromSeconds(7));
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, timeProvider: time);
        Assert.AreEqual(TimeSpan.FromSeconds(7), (await reopened.GetBudgetAsync(AgentTaskIdentity.RootRunId("graph"), Ct)).ExecutionTime);
        await reopened.ReconcileTaskVerificationAsync("graph", "task", id, Completion(), "Established exact stopped worker result", Caller, Ct);
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.AreEqual(TimeSpan.FromSeconds(7), (await reopened.GetBudgetAsync(AgentTaskIdentity.RootRunId("graph"), Ct)).ExecutionTime);
    }

    [TestMethod]
    public async Task ExhaustedBudgetBlocksAdmissionAndLateAcceptanceAtomically()
    {
        var time = new ManualTimeProvider();
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, timeProvider: time);
        var claim = await CandidateAsync(store, new(10, 10, MaxExecutionTime: TimeSpan.FromSeconds(5)));
        var admitted = await store.BeginTaskVerificationAsync(claim, "build", Ct);
        time.Advance(TimeSpan.FromSeconds(6));
        await store.CompleteTaskVerificationAsync(claim, admitted.Invocation.InvocationId, Completion(), Ct);
        await Assert.ThrowsExactlyAsync<WorkflowBudgetExceededException>(() => store.RecordTaskAcceptanceAsync(claim,
            new(TaskAcceptanceOutcome.Accepted, "host", "Attempted late acceptance"), Ct));
        Assert.AreEqual(TaskGraphTaskStatus.AwaitingAcceptance, (await store.GetTaskGraphAsync("graph", Ct))!.Tasks[0].Status);
    }

    [TestMethod]
    public async Task ExhaustedBudgetCreatesNoCommandIntent()
    {
        var time = new ManualTimeProvider();
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, timeProvider: time);
        var claim = await CandidateAsync(store, new(10, 10, MaxExecutionTime: TimeSpan.FromSeconds(5)));
        time.Advance(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<WorkflowBudgetExceededException>(() => store.BeginTaskVerificationAsync(claim, "build", Ct));
        Assert.IsEmpty((await store.GetTaskGraphAsync("graph", Ct))!.Tasks[0].Verifications);
    }

    [TestMethod]
    public async Task AcceptanceCannotBypassMissingOrFailingCommandReceipts()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var claim = await CandidateAsync(store);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.RecordTaskAcceptanceAsync(claim,
            new(TaskAcceptanceOutcome.Accepted, "unrelated-host", "No build evidence"), Ct));
        var invocation = await store.BeginTaskVerificationAsync(claim, "build", Ct);
        await store.CompleteTaskVerificationAsync(claim, invocation.Invocation.InvocationId,
            Completion() with { Outcome = TaskCommandOutcome.Failed, ExitCode = 1 }, Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.RecordTaskAcceptanceAsync(claim,
            new(TaskAcceptanceOutcome.Accepted, "unrelated-host", "Failed build ignored"), Ct));
    }

    [TestMethod]
    public async Task BindingAndClaimsCannotDrift()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var claim = await CandidateAsync(store);
        var graph = Graph();
        var changed = Binding(graph) with { VerificationProfiles = [Profile() with { Arguments = ["changed"] }] };
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.AcceptAgentTaskGraphAsync(graph, Caller, 1, changed, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.BeginTaskVerificationAsync(claim with { LeaseToken = "foreign-token" }, "build", Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.BeginTaskVerificationAsync(claim, "undeclared", Ct));
        Assert.IsEmpty((await store.GetTaskGraphAsync("graph", Ct))!.Tasks[0].Verifications);
    }

    [TestMethod]
    [DataRow("NUL", false)]
    [DataRow("src/con.txt", false)]
    [DataRow("out/LPT9.trx", true)]
    [DataRow("out/COM0.log", true)]
    [DataRow("src/project.", false)]
    [DataRow("src/project /inner", false)]
    [DataRow("out/result?.trx", true)]
    [DataRow("out/<report>.trx", true)]
    [DataRow("out/result|file.trx", true)]
    [DataRow("out/result\"file.trx", true)]
    [DataRow("/outside", false)]
    [DataRow("\\\\host\\share\\report.trx", true)]
    [DataRow("../outside", false)]
    [DataRow(".git/report.trx", true)]
    public async Task NonportableCommandPathsAreRejectedBeforeLedgerAdmission(string path, bool testResults)
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph();
        var profile = testResults ? Profile() with { Kind = TaskVerificationKind.Tests, TestResultsPath = path }
            : Profile() with { WorkingDirectory = path };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.AcceptAgentTaskGraphAsync(graph, Caller, 1,
            Binding(graph) with { VerificationProfiles = [profile] }, Ct));
        Assert.IsNull(await store.GetTaskGraphAsync(graph.GraphId, Ct));
        Assert.IsEmpty(await store.ListAsync(Ct));
    }

    [TestMethod]
    [DataRow(10, false)]
    [DataRow(11, true)]
    public async Task SharedTokenEqualityPermitsVerificationButActualOvershootBlocksIt(long charged, bool exhausted)
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph();
        var accepted = await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, Binding(graph) with { SharedBudget = new(10, 10, MaxTotalTokens: 10) }, Ct);
        var claim = (await store.ClaimTaskAsync("graph", Caller, Ct))!;
        var request = AgentTaskBindingValidator.CreateChildRequest(graph, Caller, accepted.AgentBinding!, claim.Task, claim.AttemptId, claim.Inputs);
        await store.CreateAsync(new(request.RequestedRunId!.Value.Value, accepted.AgentBinding!.RootRunId, accepted.AgentBinding.RootRunId,
            "task", request, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Budget: request.Budget, Usage: new(InputTokens: charged)), Ct);
        await store.RecordTaskResultAsync(claim, new("graph", claim.GraphFingerprint, "task", WorkflowTaskStatus.AwaitingVerification,
            "exact-sealed-revision", "Awaiting verification"), Ct);
        if (exhausted)
            await Assert.ThrowsExactlyAsync<WorkflowBudgetExceededException>(() => store.BeginTaskVerificationAsync(claim, "build", Ct));
        else
        {
            var admission = await store.BeginTaskVerificationAsync(claim, "build", Ct);
            await store.CompleteTaskVerificationAsync(claim, admission.Invocation.InvocationId, Completion(), Ct);
            await store.RecordTaskAcceptanceAsync(claim, new(TaskAcceptanceOutcome.Accepted, "host", "Root token use equals its cap"), Ct);
        }
    }

    [TestMethod]
    [DataRow("profile")]
    [DataRow("revision")]
    [DataRow("identity")]
    [DataRow("log")]
    [DataRow("failed-accepted")]
    [DataRow("schema")]
    public async Task ReopenRejectsPersistedReceiptOrAcceptanceTampering(string defect)
    {
        TaskGraphCheckpoint saved;
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            var claim = await CandidateAsync(store);
            var invocation = await store.BeginTaskVerificationAsync(claim, "build", Ct);
            await store.CompleteTaskVerificationAsync(claim, invocation.Invocation.InvocationId, Completion(), Ct);
            saved = await store.RecordTaskAcceptanceAsync(claim, new(TaskAcceptanceOutcome.Accepted, "host", "Exact command evidence passed"), Ct);
        }
        var state = saved.Tasks[0];
        var receipt = state.Verifications[0];
        receipt = defect switch
        {
            "profile" => receipt with { ProfileFingerprint = "changed-profile" },
            "revision" => receipt with { Revision = "changed-revision" },
            "identity" => receipt with { InvocationId = "changed-identity" },
            "log" => receipt with { Completion = receipt.Completion! with { StandardOutput = new("forged", TaskVerificationIdentity.EmptyLogDigest, 6, false) } },
            "failed-accepted" => receipt with { Completion = receipt.Completion! with { Outcome = TaskCommandOutcome.Failed, ExitCode = 1 } },
            _ => receipt
        };
        saved = saved with { Tasks = saved.Tasks.SetItem(0, state with { Verifications = [receipt] }), SchemaVersion = defect == "schema" ? 3 : 4 };
        var payload = JsonSerializer.Serialize(saved, new JsonSerializerOptions { MaxDepth = 256 });
        await using (var database = await Database.OpenAsync(DatabasePath))
        {
            await using var command = await database.ExecuteAsync("UPDATE task_graphs SET payload = '" + payload.Replace("'", "''") + "' WHERE graph_id = 'graph'");
        }
        await Assert.ThrowsAsync<Exception>(async () => { await using var rejected = await CSharpDbWorkflowStore.OpenAsync(DatabasePath); });
    }

    private async Task<TaskGraphClaim> CandidateAsync(CSharpDbWorkflowStore store, WorkflowBudget? budget = null)
    {
        var graph = Graph();
        var accepted = await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, Binding(graph) with { SharedBudget = budget ?? new(10, 10) }, Ct);
        Assert.AreEqual(4, accepted.SchemaVersion);
        var claim = (await store.ClaimTaskAsync("graph", Caller, Ct))!;
        await store.RecordTaskResultAsync(claim, new("graph", claim.GraphFingerprint, "task", WorkflowTaskStatus.AwaitingVerification,
            "exact-sealed-revision", "Awaiting controlled verification"), Ct);
        return claim;
    }
    private TaskGraphAgentBinding Binding(WorkflowTaskGraph graph) => new(AgentTaskIdentity.RootRunId(graph.GraphId).Value,
        Path.Combine(_directory, "source"), new string('a', 40), Path.Combine(_directory, "managed"), "test", "model",
        new(ToolProfile.Minimal), new(10, 10))
    {
        DependencyMode = TaskDependencyMode.AcceptedChanges,
        VerificationAuthority = TaskVerificationAuthority.TrustedLocalProcess,
        VerificationProfiles = [Profile()],
        CommandVerificationRules = [new("task", "build-ok", "build")]
    };
    private static WorkflowTaskGraph Graph() => new("graph", "Verify deliverable", [new("task", "Build deliverable")
        { AcceptanceCriteria = [new("build-ok", "Controlled build passes")] }]);
    private static TaskVerificationProfile Profile() => new("build", Path.Combine(Path.GetTempPath(), "test-worker.exe"),
        "sha256:" + new string('a', 64), ["build", "--no-restore"], TimeSpan.FromMinutes(1));
    private static TaskVerificationCompletion Completion() => new(TaskCommandOutcome.Passed, "test-worker", "Exact build passed", 0,
        new("", TaskVerificationIdentity.EmptyLogDigest, 0, false), new("", TaskVerificationIdentity.EmptyLogDigest, 0, false));
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
