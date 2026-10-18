using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpDB.Engine;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.AgentCore.Persistence.CSharpDb.Tests;

public sealed class TaskGraphStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "IronPaw-TaskGraphs-" + Guid.NewGuid().ToString("n"));
    private string DatabasePath => Path.Combine(_directory, "workflows.cdb");
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly CallerIdentity Caller = new("caller", "Original caller", true);

    [Fact]
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
            Assert.Equal(["read_file"], accepted.Graph.Tasks[0].ToolPolicy.AllowList);
            Assert.Equal(0, (await store.AcceptTaskGraphAsync(accepted.Graph, Caller with { DisplayName = "Renamed" }, 2, Ct)).Revision);
            await Assert.ThrowsAsync<WorkflowConflictException>(() => store.AcceptTaskGraphAsync(accepted.Graph, Caller, 3, Ct));
            await Assert.ThrowsAsync<WorkflowConflictException>(() => store.AcceptTaskGraphAsync(accepted.Graph with { Objective = "Changed" }, Caller, 2, Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.AcceptTaskGraphAsync(accepted.Graph, Caller with { IsOwner = false }, 2, Ct));
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var saved = Assert.Single(await reopened.ListTaskGraphsAsync(Ct));
        Assert.Equal(fingerprint, saved.DefinitionFingerprint);
        Assert.Equal(["read_file"], saved.Graph.Tasks[0].ToolPolicy.AllowList);
        Assert.Equal(0, saved.Revision);
        Assert.Single(await reopened.ReadTaskGraphJournalAsync("graph'quoted", Ct));
    }

    [Fact]
    public async Task ConcurrentClaimsRespectConcurrencyAndDependenciesUntilTrustedAcceptance()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("dependent", "first"), Task("first"), Task("independent")), Caller, 1, Ct);
        var claims = await System.Threading.Tasks.Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.ClaimTaskAsync("graph'quoted", Caller, Ct)));
        var first = Assert.Single(claims.OfType<TaskGraphClaim>());
        Assert.Equal("first", first.Task.TaskId);
        var proposal = await store.RecordTaskResultAsync(first, Success(first), Ct);
        Assert.Equal(TaskGraphTaskStatus.AwaitingAcceptance, proposal.Tasks[1].Status);
        Assert.Null(await store.ClaimTaskAsync("graph'quoted", Caller, Ct));
        var accepted = await store.RecordTaskAcceptanceAsync(first, Accepted(), Ct);
        Assert.Equal(TaskGraphTaskStatus.Succeeded, accepted.Tasks[1].Status);
        var receipt = accepted.Tasks[1].Acceptance!;
        Assert.Equal(first.AttemptId, receipt.AttemptId);
        Assert.Equal(first.GraphFingerprint, receipt.GraphFingerprint);
        Assert.StartsWith("sha256:", receipt.ResultDigest);
        Assert.Equal("revision-1", receipt.Revision);
        Assert.Equal("dependent", (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!.Task.TaskId);
    }

    [Fact]
    public async Task SuccessCandidateCannotUnlockDependencyBeforeAcceptanceAndIndependentBranchCanRun()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("dependent", "first"), Task("first"), Task("independent")), Caller, 2, Ct);
        var first = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await store.RecordTaskResultAsync(first, Success(first), Ct);
        var second = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        Assert.Equal("independent", second.Task.TaskId);
        await store.RecordTaskResultAsync(second, Failure(second), Ct);
        await store.RecordTaskAcceptanceAsync(first, new(TaskAcceptanceOutcome.Rejected, "host-check", "Verification failed"), Ct);
        Assert.Null(await store.ClaimTaskAsync("graph'quoted", Caller, Ct));
        var saved = (await store.GetTaskGraphAsync("graph'quoted", Ct))!;
        Assert.Equal(TaskGraphTaskStatus.Pending, saved.Tasks[0].Status);
        Assert.Equal(TaskGraphTaskStatus.Failed, saved.Tasks[1].Status);
    }

    [Fact]
    public async Task InconclusiveVerificationReclaimsSavedCandidateWithNewFenceAndSameAttempt()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("a"), Task("b")), Caller, 1, Ct);
        var original = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        var result = Success(original);
        await store.RecordTaskResultAsync(original, result, Ct);
        await store.RecordTaskAcceptanceAsync(original, new(TaskAcceptanceOutcome.Inconclusive, "host-check", "Evidence temporarily unavailable"), Ct);
        var next = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct, ["a"]))!;
        Assert.Equal("b", next.Task.TaskId);
        await store.RecordTaskResultAsync(next, Failure(next), Ct);
        var verification = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        Assert.Equal(original.AttemptId, verification.AttemptId);
        Assert.NotEqual(original.LeaseToken, verification.LeaseToken);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(verification.ProposedResult));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.RecordTaskAcceptanceAsync(original, Accepted(), Ct));
        await store.RecordTaskAcceptanceAsync(verification, Accepted(), Ct);
        Assert.Equal(TaskGraphTaskStatus.Succeeded, (await store.GetTaskGraphAsync("graph'quoted", Ct))!.Tasks[0].Status);
    }

    [Fact]
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
        Assert.Equal(TaskGraphTaskStatus.NeedsReconciliation, recovered.Tasks[0].Status);
        Assert.Equal(running.AttemptId, recovered.Tasks[0].AttemptId);
        Assert.All(recovered.Tasks, state => Assert.Null(state.LeaseToken));
        var claim = (await reopened.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        Assert.Equal("verifying", claim.Task.TaskId);
        Assert.Equal(verifying.AttemptId, claim.AttemptId);
        Assert.NotNull(claim.ProposedResult);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => reopened.RecordTaskResultAsync(running, Failure(running), Ct));
        await reopened.RecordTaskAcceptanceAsync(claim, Accepted(), Ct);
        Assert.Null(await reopened.ClaimTaskAsync("graph'quoted", Caller, Ct));
        var journal = await reopened.ReadTaskGraphJournalAsync("graph'quoted", Ct);
        Assert.Contains(journal, entry => entry.EventType == "recovered");
        Assert.Equal(Enumerable.Range(0, journal.Count), journal.Select(entry => entry.Revision));
    }

    [Fact]
    public async Task ReconciliationPreservesAttemptAndRequiresAcceptanceForSuccessfulKnownOutcome()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("a"), Task("b", "a")), Caller, 1, Ct);
        var original = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await store.AbandonTaskClaimAsync(original, "Interrupted external work", Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReconcileTaskAsync("graph'quoted", "a", Success(original), "Observed outcome", Caller with { Id = "other" }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReconcileTaskAsync("graph'quoted", "a", Success(original), " ", Caller, Ct));
        var reconciled = await store.ReconcileTaskAsync("graph'quoted", "a", Success(original), "Observed exact external revision", Caller, Ct);
        Assert.Equal(TaskGraphTaskStatus.AwaitingAcceptance, reconciled.Tasks[0].Status);
        Assert.Equal(original.AttemptId, reconciled.Tasks[0].AttemptId);
        var verification = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        Assert.NotNull(verification.ProposedResult);
        await store.RecordTaskAcceptanceAsync(verification, Accepted(), Ct);
        Assert.Equal("b", (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!.Task.TaskId);
    }

    [Fact]
    public async Task ControlsBlockAdmissionWhileKnownResultCleanupAndReadonlyVerificationCanSettle()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("a"), Task("b")), Caller, 1, Ct);
        var first = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Pause, Caller, Ct);
        Assert.Null(await store.ClaimTaskAsync("graph'quoted", Caller, Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.None, Caller, Ct));
        await store.RecordTaskResultAsync(first, Success(first), Ct);
        await store.RecordTaskAcceptanceAsync(first, Accepted(), Ct);
        await store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.None, Caller, Ct);
        var second = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Cancel, Caller, Ct);
        await store.RecordTaskResultAsync(second, Failure(second), Ct);
        Assert.Null(await store.ClaimTaskAsync("graph'quoted", Caller, Ct));
        await Assert.ThrowsAsync<WorkflowCancelledException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.None, Caller, Ct));
        Assert.Equal(WorkflowControl.Cancel, (await store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Cancel, Caller, Ct)).Control);
    }

    [Fact]
    public async Task CannotPauseCompletedGraphOrControlAnotherCallersGraph()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("a")), Caller, 1, Ct);
        var claim = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await store.RecordTaskResultAsync(claim, Failure(claim), Ct);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Pause, Caller, Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Cancel, Caller, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Cancel, Caller with { IsOwner = false }, Ct));
    }

    [Fact]
    public async Task FailedParentWithPermanentlyBlockedPendingDescendantCannotBePaused()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("parent"), Task("blocked", "parent")), Caller, 1, Ct);
        var claim = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await store.RecordTaskResultAsync(claim, Failure(claim), Ct);
        Assert.Null(await store.ClaimTaskAsync("graph'quoted", Caller, Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Pause, Caller, Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Cancel, Caller, Ct));
        var saved = (await store.GetTaskGraphAsync("graph'quoted", Ct))!;
        Assert.Equal(TaskGraphTaskStatus.Pending, saved.Tasks[1].Status);
        Assert.Equal(WorkflowControl.None, saved.Control);
    }

    [Fact]
    public async Task AcceptedSuccessfulGraphCannotBeCancelledAfterCompletion()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("a")), Caller, 1, Ct);
        var claim = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await store.RecordTaskResultAsync(claim, Success(claim), Ct);
        var completed = await store.RecordTaskAcceptanceAsync(claim, Accepted(), Ct);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.ControlTaskGraphAsync("graph'quoted", WorkflowControl.Cancel, Caller, Ct));
        var saved = (await store.GetTaskGraphAsync("graph'quoted", Ct))!;
        Assert.Equal(completed.Revision, saved.Revision);
        Assert.Equal(WorkflowControl.None, saved.Control);
        Assert.Equal(TaskGraphTaskStatus.Succeeded, saved.Tasks[0].Status);
        Assert.Equal(completed.Tasks[0].Acceptance, saved.Tasks[0].Acceptance);
    }

    [Fact]
    public async Task InvalidOrReplacedResultsAndForgedClaimsDoNotMutateJournal()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.AcceptTaskGraphAsync(Graph(Task("a")), Caller, 1, Ct);
        var claim = (await store.ClaimTaskAsync("graph'quoted", Caller, Ct))!;
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.RecordTaskResultAsync(claim, Success(claim) with { GraphFingerprint = "wrong" }, Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.RecordTaskResultAsync(claim, Success(claim) with { Status = WorkflowTaskStatus.Running }, Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.RecordTaskResultAsync(claim with { Task = claim.Task with { Objective = "Changed" } }, Success(claim), Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.RecordTaskAcceptanceAsync(claim, Accepted(), Ct));
        var saved = await store.RecordTaskResultAsync(claim, Success(claim), Ct);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.RecordTaskResultAsync(claim, Success(claim) with { Revision = "other" }, Ct));
        Assert.Equal(saved.Revision, (await store.GetTaskGraphAsync("graph'quoted", Ct))!.Revision);
        Assert.Equal(3, (await store.ReadTaskGraphJournalAsync("graph'quoted", Ct)).Count);
    }

    [Fact]
    public async Task JournalFailureRollsBackClaimAndConcurrencyReservation()
    {
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
            await store.AcceptTaskGraphAsync(Graph(Task("a")), Caller, 1, Ct);
        await using (var database = await Database.OpenAsync(DatabasePath))
        {
            await using var result = await database.ExecuteAsync("INSERT INTO task_graph_journal VALUES (2, 'graph''quoted', 1, '2026-10-14T00:00:00+00:00', 'injected', NULL, NULL)");
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await Assert.ThrowsAnyAsync<Exception>(() => reopened.ClaimTaskAsync("graph'quoted", Caller, Ct));
        var saved = (await reopened.GetTaskGraphAsync("graph'quoted", Ct))!;
        Assert.Equal(0, saved.Revision);
        Assert.Equal(TaskGraphTaskStatus.Pending, saved.Tasks[0].Status);
        Assert.Null(saved.Tasks[0].AttemptId);
        Assert.Null(saved.Tasks[0].LeaseToken);
    }

    [Fact]
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
                Assert.True(await read.MoveNextAsync());
                payload = JsonNode.Parse(read.Current[0].AsText)!.AsObject();
            }
            payload["Tasks"]![0]!["Acceptance"]!["ResultDigest"] = "sha256:forged";
            await using var result = await database.ExecuteAsync("UPDATE task_graphs SET payload = '" + payload.ToJsonString().Replace("'", "''", StringComparison.Ordinal) + "'");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => CSharpDbWorkflowStore.OpenAsync(DatabasePath));
    }

    [Fact]
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
        Assert.Equal(64, count);
    }

    [Fact]
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
                Assert.True(await read.MoveNextAsync());
                payload = JsonNode.Parse(read.Current[0].AsText)!.AsObject();
            }
            // Each individual state is consistent, but the dependency history is impossible.
            payload["Tasks"]![0]!["Status"] = (int)TaskGraphTaskStatus.Failed;
            payload["Tasks"]![0]!["Acceptance"]!["Outcome"] = (int)TaskAcceptanceOutcome.Rejected;
            await using var result = await database.ExecuteAsync("UPDATE task_graphs SET payload = '" + payload.ToJsonString().Replace("'", "''", StringComparison.Ordinal) + "'");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => CSharpDbWorkflowStore.OpenAsync(DatabasePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task InvalidConcurrencyIsRejectedBeforeAcceptance(int concurrency)
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AcceptTaskGraphAsync(Graph(Task("a")), Caller, concurrency, Ct));
        Assert.Empty(await store.ListTaskGraphsAsync(Ct));
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

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
