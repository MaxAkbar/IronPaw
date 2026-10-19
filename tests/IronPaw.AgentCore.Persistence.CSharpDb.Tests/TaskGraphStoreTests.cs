using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpDB.Engine;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.AgentCore.Persistence.CSharpDb.Tests;

[TestClass]
public sealed class TaskGraphStoreTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "IronPaw-TaskGraphs-" + Guid.NewGuid().ToString("n"));
    private string DatabasePath => Path.Combine(_directory, "workflows.cdb");
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly CallerIdentity Caller = new("caller", "Original caller", true);

    [TestMethod]
    public async Task AcceptedGraphReopensWithFrozenDefinitionAndIdempotentAcceptance()
    {
        var allow = new List<string> { "read_file" };
        var graph = Graph(Task("a") with { ToolPolicy = new(ToolProfile.Minimal, AllowList: allow) });
        string fingerprint;
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            var accepted = await store.AcceptTaskGraphAsync(graph, Caller, 2, Ct);
            fingerprint = accepted.DefinitionFingerprint;
            allow.Add("write_file");
            Assert.AreSequenceEqual(["read_file"], accepted.Graph.Tasks[0].ToolPolicy.AllowList);
            Assert.AreEqual(0, (await store.AcceptTaskGraphAsync(accepted.Graph, Caller with { DisplayName = "Renamed" }, 2, Ct)).Revision);
            await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.AcceptTaskGraphAsync(accepted.Graph, Caller, 3, Ct));
            await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.AcceptTaskGraphAsync(accepted.Graph with { Objective = "Changed" }, Caller, 2, Ct));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.AcceptTaskGraphAsync(accepted.Graph, Caller with { IsOwner = false }, 2, Ct));
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var saved = Assert.ContainsSingle(await reopened.ListTaskGraphsAsync(Ct));
        Assert.AreEqual(fingerprint, saved.DefinitionFingerprint);
        Assert.AreSequenceEqual(["read_file"], saved.Graph.Tasks[0].ToolPolicy.AllowList);
        Assert.AreEqual(0, saved.Revision);
        Assert.ContainsSingle(await reopened.ReadTaskGraphJournalAsync("graph'quoted", Ct));
    }

    [TestMethod]
    public async Task ConcurrentClaimsRespectConcurrencyAndDependenciesUntilTrustedAcceptance()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("dependent", "first"), Task("first"), Task("independent")), Caller, 1, Ct);
        var claims = await System.Threading.Tasks.Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.ClaimTaskAsync("graph'quoted", Caller, Ct)));
        var first = Assert.ContainsSingle(claims.OfType<TaskGraphClaim>());
        Assert.AreEqual("first", first.Task.TaskId);
        var proposal = await store.RecordTaskResultAsync(first, Success(first), Ct);
        Assert.AreEqual(TaskGraphTaskStatus.AwaitingAcceptance, proposal.Tasks[1].Status);
        Assert.IsNull(await store.ClaimTaskAsync("graph'quoted", Caller, Ct));
        var accepted = await store.RecordTaskAcceptanceAsync(first, Accepted(), Ct);
        Assert.AreEqual(TaskGraphTaskStatus.Succeeded, accepted.Tasks[1].Status);
        var receipt = accepted.Tasks[1].Acceptance!;
        Assert.AreEqual(first.AttemptId, receipt.AttemptId);
        Assert.AreEqual(first.GraphFingerprint, receipt.GraphFingerprint);
        Assert.StartsWith("sha256:", receipt.ResultDigest);
        Assert.AreEqual("revision-1", receipt.Revision);
        Assert.AreEqual("dependent", (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!.Task.TaskId);
    }

    [TestMethod]
    public async Task SuccessCandidateCannotUnlockDependencyBeforeAcceptanceAndIndependentBranchCanRun()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("dependent", "first"), Task("first"), Task("independent")), Caller, 2, Ct);
        var first = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await store.RecordTaskResultAsync(first, Success(first), Ct);
        var second = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        Assert.AreEqual("independent", second.Task.TaskId);
        await store.RecordTaskResultAsync(second, Failure(second), Ct);
        await store.RecordTaskAcceptanceAsync(first, new(TaskAcceptanceOutcome.Rejected, "host-check", "Verification failed"), Ct);
        Assert.IsNull(await store.ClaimTaskAsync("graph'quoted", Caller, Ct));
        var saved = (await store.GetTaskGraphAsync("graph'quoted", Ct))!;
        Assert.AreEqual(TaskGraphTaskStatus.Pending, saved.Tasks[0].Status);
        Assert.AreEqual(TaskGraphTaskStatus.Failed, saved.Tasks[1].Status);
    }

    [TestMethod]
    public async Task InconclusiveVerificationReclaimsSavedCandidateWithNewFenceAndSameAttempt()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("a"), Task("b")), Caller, 1, Ct);
        var original = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        var result = Success(original);
        await store.RecordTaskResultAsync(original, result, Ct);
        await store.RecordTaskAcceptanceAsync(original, new(TaskAcceptanceOutcome.Inconclusive, "host-check", "Evidence temporarily unavailable"), Ct);
        var next = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct, ["a"]))!;
        Assert.AreEqual("b", next.Task.TaskId);
        await store.RecordTaskResultAsync(next, Failure(next), Ct);
        var verification = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        Assert.AreEqual(original.AttemptId, verification.AttemptId);
        Assert.AreNotEqual(original.LeaseToken, verification.LeaseToken);
        Assert.AreEqual(JsonSerializer.Serialize(result), JsonSerializer.Serialize(verification.ProposedResult));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.RecordTaskAcceptanceAsync(original, Accepted(), Ct));
        await store.RecordTaskAcceptanceAsync(verification, Accepted(), Ct);
        Assert.AreEqual(TaskGraphTaskStatus.Succeeded, (await store.GetTaskGraphAsync("graph'quoted", Ct))!.Tasks[0].Status);
    }

    [TestMethod]
    public async Task ReopenQuarantinesExecutionButRecoversVerificationWithoutExecutingAgain()
    {
        TaskGraphClaim running;
        TaskGraphClaim verifying;
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            await store.AcceptTaskGraphAsync(Graph(Task("running"), Task("verifying")), Caller, 2, Ct);
            running = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
            verifying = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
            await store.RecordTaskResultAsync(verifying, Success(verifying), Ct);
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var recovered = (await reopened.GetTaskGraphAsync("graph'quoted", Ct))!;
        Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, recovered.Tasks[0].Status);
        Assert.AreEqual(running.AttemptId, recovered.Tasks[0].AttemptId);
        foreach (var state in recovered.Tasks)
        {
            Assert.IsNull(state.LeaseToken);
        }
        var claim = (await reopened.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        Assert.AreEqual("verifying", claim.Task.TaskId);
        Assert.AreEqual(verifying.AttemptId, claim.AttemptId);
        Assert.IsNotNull(claim.ProposedResult);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => reopened.RecordTaskResultAsync(running, Failure(running), Ct));
        await reopened.RecordTaskAcceptanceAsync(claim, Accepted(), Ct);
        Assert.IsNull(await reopened.ClaimTaskAsync("graph'quoted", Caller, Ct));
        var journal = await reopened.ReadTaskGraphJournalAsync("graph'quoted", Ct);
        Assert.Contains(entry => entry.EventType == "recovered", journal);
        Assert.AreSequenceEqual(Enumerable.Range(0, journal.Count), journal.Select(entry => entry.Revision));
    }

    [TestMethod]
    public async Task ReconciliationPreservesAttemptAndRequiresAcceptanceForSuccessfulKnownOutcome()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("a"), Task("b", "a")), Caller, 1, Ct);
        var original = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await store.AbandonTaskClaimAsync(original, "Interrupted external work", Ct);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.ReconcileTaskAsync("graph'quoted", "a", Success(original), "Observed outcome", Caller with { Id = "other" }, Ct));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.ReconcileTaskAsync("graph'quoted", "a", Success(original), " ", Caller, Ct));
        var reconciled = await store.ReconcileTaskAsync("graph'quoted", "a", Success(original), "Observed exact external revision", Caller, Ct);
        Assert.AreEqual(TaskGraphTaskStatus.AwaitingAcceptance, reconciled.Tasks[0].Status);
        Assert.AreEqual(original.AttemptId, reconciled.Tasks[0].AttemptId);
        var verification = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        Assert.IsNotNull(verification.ProposedResult);
        await store.RecordTaskAcceptanceAsync(verification, Accepted(), Ct);
        Assert.AreEqual("b", (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!.Task.TaskId);
    }

    [TestMethod]
    public async Task ControlsBlockAdmissionWhileKnownResultCleanupAndReadonlyVerificationCanSettle()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("a"), Task("b")), Caller, 1, Ct);
        var first = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Pause, Caller, Ct);
        Assert.IsNull(await store.ClaimTaskAsync("graph'quoted", Caller, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.None, Caller, Ct));
        await store.RecordTaskResultAsync(first, Success(first), Ct);
        await store.RecordTaskAcceptanceAsync(first, Accepted(), Ct);
        await store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.None, Caller, Ct);
        var second = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Cancel, Caller, Ct);
        await store.RecordTaskResultAsync(second, Failure(second), Ct);
        Assert.IsNull(await store.ClaimTaskAsync("graph'quoted", Caller, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowCancelledException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.None, Caller, Ct));
        Assert.AreEqual(WorkflowControl.Cancel, (await store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Cancel, Caller, Ct)).Control);
    }

    [TestMethod]
    public async Task CannotPauseCompletedGraphOrControlAnotherCallersGraph()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("a")), Caller, 1, Ct);
        var claim = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await store.RecordTaskResultAsync(claim, Failure(claim), Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Pause, Caller, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Cancel, Caller, Ct));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Cancel, Caller with { IsOwner = false }, Ct));
    }

    [TestMethod]
    public async Task FailedParentWithPermanentlyBlockedPendingDescendantCannotBePaused()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("parent"), Task("blocked", "parent")), Caller, 1, Ct);
        var claim = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await store.RecordTaskResultAsync(claim, Failure(claim), Ct);
        Assert.IsNull(await store.ClaimTaskAsync("graph'quoted", Caller, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Pause, Caller, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Cancel, Caller, Ct));
        var saved = (await store.GetTaskGraphAsync("graph'quoted", Ct))!;
        Assert.AreEqual(TaskGraphTaskStatus.Pending, saved.Tasks[1].Status);
        Assert.AreEqual(WorkflowControl.None, saved.Control);
    }

    [TestMethod]
    public async Task AcceptedSuccessfulGraphCannotBeCancelledAfterCompletion()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("a")), Caller, 1, Ct);
        var claim = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await store.RecordTaskResultAsync(claim, Success(claim), Ct);
        var completed = await store.RecordTaskAcceptanceAsync(claim, Accepted(), Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Cancel, Caller, Ct));
        var saved = (await store.GetTaskGraphAsync("graph'quoted", Ct))!;
        Assert.AreEqual(completed.Revision, saved.Revision);
        Assert.AreEqual(WorkflowControl.None, saved.Control);
        Assert.AreEqual(TaskGraphTaskStatus.Succeeded, saved.Tasks[0].Status);
        Assert.AreEqual(completed.Tasks[0].Acceptance, saved.Tasks[0].Acceptance);
    }

    [TestMethod]
    public async Task InvalidOrReplacedResultsAndForgedClaimsDoNotMutateJournal()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("a")), Caller, 1, Ct);
        var claim = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.RecordTaskResultAsync(claim, Success(claim) with { GraphFingerprint = "wrong" }, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.RecordTaskResultAsync(claim, Success(claim) with { Status = WorkflowTaskStatus.Running }, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.RecordTaskResultAsync(claim with { Task = claim.Task with { Objective = "Changed" } }, Success(claim), Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.RecordTaskAcceptanceAsync(claim, Accepted(), Ct));
        var saved = await store.RecordTaskResultAsync(claim, Success(claim), Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.RecordTaskResultAsync(claim, Success(claim) with { Revision = "other" }, Ct));
        Assert.AreEqual(saved.Revision, (await store.GetTaskGraphAsync("graph'quoted", Ct))!.Revision);
        Assert.AreEqual(3, (await store.ReadTaskGraphJournalAsync("graph'quoted", Ct)).Count);
    }

    [TestMethod]
    public async Task JournalFailureRollsBackClaimAndConcurrencyReservation()
    {
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
            await store.AcceptTaskGraphAsync(Graph(Task("a")), Caller, 1, Ct);
        await using (var database = await Database.OpenAsync(DatabasePath))
        {
            await using var result = await database.ExecuteAsync("INSERT INTO task_graph_journal VALUES (2, 'graph''quoted', 1, '2026-10-14T00:00:00+00:00', 'injected', NULL, NULL)");
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await Assert.ThrowsAsync<Exception>(() => reopened.ClaimTaskAsync("graph'quoted", Caller, Ct));
        var saved = (await reopened.GetTaskGraphAsync("graph'quoted", Ct))!;
        Assert.AreEqual(0, saved.Revision);
        Assert.AreEqual(TaskGraphTaskStatus.Pending, saved.Tasks[0].Status);
        Assert.IsNull(saved.Tasks[0].AttemptId);
        Assert.IsNull(saved.Tasks[0].LeaseToken);
    }

    [TestMethod]
    public async Task ForgedAcceptedResultDigestFailsClosedOnReopen()
    {
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            await store.AcceptTaskGraphAsync(Graph(Task("a")), Caller, 1, Ct);
            var claim = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
            await store.RecordTaskResultAsync(claim, Success(claim), Ct);
            await store.RecordTaskAcceptanceAsync(claim, Accepted(), Ct);
        }
        await using (var database = await Database.OpenAsync(DatabasePath))
        {
            JsonObject payload;
            await using (var read = await database.ExecuteAsync("SELECT payload FROM task_graphs"))
            {
                Assert.IsTrue(await read.MoveNextAsync());
                payload = JsonNode.Parse(read.Current[0].AsText)!.AsObject();
            }
            payload["Tasks"]![0]!["Acceptance"]!["ResultDigest"] = "sha256:forged";
            await using var result = await database.ExecuteAsync("UPDATE task_graphs SET payload = '" + payload.ToJsonString().Replace("'", "''", StringComparison.Ordinal) + "'");
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CSharpDbWorkflowStore.OpenAsync(DatabasePath));
    }

    [TestMethod]
    public async Task DeepInheritedPolicySurvivesGraphPersistenceAndRecovery()
    {
        ToolSelectionPolicy? inherited = null;
        for (var index = 0; index < 64; index++) inherited = new(ToolProfile.Minimal, InheritedPolicy: inherited);
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
            await store.AcceptTaskGraphAsync(Graph(Task("a") with { ToolPolicy = inherited! }), Caller, 1, Ct);
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var policy = (await reopened.GetTaskGraphAsync("graph'quoted", Ct))!.Graph.Tasks[0].ToolPolicy;
        var count = 0;
        for (; policy is not null; policy = policy.InheritedPolicy) count++;
        Assert.AreEqual(64, count);
    }

    [TestMethod]
    public async Task ImpossibleAttemptWithUnacceptedDependencyFailsClosedOnReopen()
    {
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            await store.AcceptTaskGraphAsync(Graph(Task("first"), Task("dependent", "first")), Caller, 1, Ct);
            var first = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
            await store.RecordTaskResultAsync(first, Success(first), Ct);
            await store.RecordTaskAcceptanceAsync(first, Accepted(), Ct);
            var dependent = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
            await store.AbandonTaskClaimAsync(dependent, "Lost outcome", Ct);
        }
        await using (var database = await Database.OpenAsync(DatabasePath))
        {
            JsonObject payload;
            await using (var read = await database.ExecuteAsync("SELECT payload FROM task_graphs"))
            {
                Assert.IsTrue(await read.MoveNextAsync());
                payload = JsonNode.Parse(read.Current[0].AsText)!.AsObject();
            }
            // Each individual state is consistent, but the dependency history is impossible.
            payload["Tasks"]![0]!["Status"] = (int)TaskGraphTaskStatus.Failed;
            payload["Tasks"]![0]!["Acceptance"]!["Outcome"] = (int)TaskAcceptanceOutcome.Rejected;
            await using var result = await database.ExecuteAsync("UPDATE task_graphs SET payload = '" + payload.ToJsonString().Replace("'", "''", StringComparison.Ordinal) + "'");
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CSharpDbWorkflowStore.OpenAsync(DatabasePath));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(65)]
    public async Task InvalidConcurrencyIsRejectedBeforeAcceptance(int concurrency)
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => store.AcceptTaskGraphAsync(Graph(Task("a")), Caller, concurrency, Ct));
        Assert.IsEmpty(await store.ListTaskGraphsAsync(Ct));
    }

    private static WorkflowTaskDefinition Task(string id, params string[] dependencies) => new(id, "Execute " + id)
    {
        Dependencies = dependencies.ToImmutableArray(),
        AcceptanceCriteria = [new("criterion", "Verify exact result")]
    };
    private static WorkflowTaskGraph Graph(params WorkflowTaskDefinition[] tasks) => new("graph'quoted", "Deliver objective", tasks.ToImmutableArray());
    private static WorkflowTaskResult Success(TaskGraphClaim claim) => new(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId,
        WorkflowTaskStatus.Succeeded, "revision-1", "Established candidate")
    {
        VerificationEvidence = [new("evidence", "criterion", "revision-1", TaskVerificationOutcome.Passed, "Host will independently inspect")]
    };
    private static WorkflowTaskResult Failure(TaskGraphClaim claim) => new(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId,
        WorkflowTaskStatus.Failed, "revision-1", "Established failure");
    private static TaskAcceptanceDecision Accepted() => new(TaskAcceptanceOutcome.Accepted, "trusted-host", "Checked exact revision and declared criteria");

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
