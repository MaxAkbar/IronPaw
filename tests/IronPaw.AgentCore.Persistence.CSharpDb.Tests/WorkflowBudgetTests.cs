using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpDB.Engine;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.AgentCore.Persistence.CSharpDb.Tests;

[TestClass]
public sealed class WorkflowBudgetTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "IronPaw-Budgets-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "workflows.cdb");
    private static readonly CancellationToken Ct = CancellationToken.None;

    [TestMethod]
    public async Task ChildrenCompeteForOneSharedTokenReservationWithoutChangingRootRevision()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.CreateAsync(Checkpoint("root") with { Budget = new(MaxTotalTokens: 100) }, Ct);
        var first = Child("first");
        var second = Child("second");
        await store.CreateAsync(first, Ct);
        await store.CreateAsync(second, Ct);
        async Task<bool> Reserve(WorkflowCheckpoint checkpoint)
        {
            try { await store.SaveAsync(StartAttempt(checkpoint, 60), 0, "model_started", Ct); return true; }
            catch (WorkflowBudgetExceededException) { return false; }
        }
        Assert.ContainsSingle(x => x, await Task.WhenAll(Reserve(first), Reserve(second)));
        var snapshot = await store.GetBudgetAsync(new RunId("root"), Ct);
        Assert.AreEqual(0, snapshot.ChargedTokens);
        Assert.AreEqual(60, snapshot.ReservedTokens);
        Assert.AreEqual(40, snapshot.RemainingTokens);
        Assert.AreEqual(0, (await store.GetAsync(new RunId("root"), Ct))!.Revision);
    }

    [TestMethod]
    public async Task UsageReceiptReleasesRemainderAndActualOvershootStillCommits()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var root = Checkpoint("root") with { Budget = new(MaxTotalTokens: 100) };
        await store.CreateAsync(root, Ct);
        root = await store.SaveAsync(StartAttempt(root, 100), 0, "model_started", Ct);
        root = await store.SaveAsync(Receipt(root, 30, WorkflowReservationState.Settled), root.Revision, "model_completed", Ct);
        var settled = await store.GetBudgetAsync(new RunId("root"), Ct);
        Assert.AreEqual(30, settled.ChargedTokens);
        Assert.AreEqual(0, settled.ReservedTokens);
        Assert.AreEqual(70, settled.RemainingTokens);
        root = await store.SaveAsync(StartAttempt(root, 70), root.Revision, "model_started", Ct);
        root = await store.SaveAsync(Receipt(root, 100, WorkflowReservationState.Settled), root.Revision, "actual_overshoot", Ct);
        var overshoot = await store.GetBudgetAsync(new RunId("root"), Ct);
        Assert.AreEqual(130, overshoot.ChargedTokens);
        Assert.AreEqual(0, overshoot.ReservedTokens);
        Assert.AreEqual(0, overshoot.RemainingTokens);
        await Assert.ThrowsExactlyAsync<WorkflowBudgetExceededException>(() => store.SaveAsync(StartAttempt(root, 1), root.Revision, "rejected", Ct));
        Assert.AreEqual(130, (await store.GetBudgetAsync(new RunId("root"), Ct)).ChargedTokens);
    }

    [TestMethod]
    [DataRow(WorkflowReservationState.Unknown)]
    [DataRow(WorkflowReservationState.Settled)]
    public async Task MissingUsageChargesWholeReservation(WorkflowReservationState state)
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var root = Checkpoint("root") with { Budget = new(MaxTotalTokens: 50) };
        await store.CreateAsync(root, Ct);
        root = await store.SaveAsync(StartAttempt(root, 40), 0, "model_started", Ct);
        var call = root.ModelCalls![0];
        root = await store.SaveAsync(root with { ModelCalls = [call with { Attempts = [call.Attempts![0] with { State = state }] }] }, root.Revision, "model_unreported", Ct);
        var snapshot = await store.GetBudgetAsync(new RunId("root"), Ct);
        Assert.AreEqual(40, snapshot.ChargedTokens);
        Assert.AreEqual(0, snapshot.ReservedTokens);
        await Assert.ThrowsExactlyAsync<WorkflowBudgetExceededException>(() => store.SaveAsync(StartAttempt(root, 11), root.Revision, "retry", Ct));
    }

    [TestMethod]
    public async Task RecoveryChargesAbandonedReservationAndUncleanTimeOnlyOnce()
    {
        var time = new ManualTimeProvider();
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, timeProvider: time))
        {
            var root = Checkpoint("root", time) with { Budget = new(MaxTotalTokens: 100, MaxExecutionTime: TimeSpan.FromSeconds(20)), LeaseOwner = "dead", Status = WorkflowStatus.Running };
            await store.CreateAsync(root, Ct);
            root = await store.SaveAsync(StartAttempt(root, 70), 0, "model_started", Ct);
            time.Advance(TimeSpan.FromSeconds(4));
            Assert.AreEqual(TimeSpan.FromSeconds(4), (await store.GetBudgetAsync(new RunId("root"), Ct)).ExecutionTime);
        }
        time.Advance(TimeSpan.FromSeconds(10));
        await using (var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, timeProvider: time))
        {
            var snapshot = await reopened.GetBudgetAsync(new RunId("root"), Ct);
            Assert.AreEqual(70, snapshot.ChargedTokens);
            Assert.AreEqual(0, snapshot.ReservedTokens);
            Assert.AreEqual(TimeSpan.FromSeconds(14), snapshot.ExecutionTime);
            Assert.AreEqual(TimeSpan.FromSeconds(6), snapshot.RemainingExecutionTime);
            var recovered = (await reopened.GetAsync(new RunId("root"), Ct))!;
            Assert.IsNull(recovered.LeaseOwner);
            Assert.AreEqual(WorkflowReservationState.Unknown, recovered.ModelCalls![0].Attempts![0].State);
            Assert.AreEqual(WorkflowStatus.Interrupted, recovered.Status);
        }
        time.Advance(TimeSpan.FromHours(1));
        await using var again = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, timeProvider: time);
        Assert.AreEqual(TimeSpan.FromSeconds(14), (await again.GetBudgetAsync(new RunId("root"), Ct)).ExecutionTime);
        Assert.AreEqual(70, (await again.GetBudgetAsync(new RunId("root"), Ct)).ChargedTokens);
    }

    [TestMethod]
    public async Task SubtreeExecutionClockCountsOverlapOnceAndExcludesSettledHumanWaits()
    {
        var time = new ManualTimeProvider();
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, timeProvider: time);
        var root = Checkpoint("root", time) with { Budget = new(MaxExecutionTime: TimeSpan.FromSeconds(10)), LeaseOwner = "root-owner", Status = WorkflowStatus.Running };
        await store.CreateAsync(root, Ct);
        time.Advance(TimeSpan.FromSeconds(3));
        var child = Child("child", time) with { LeaseOwner = "child-owner", Status = WorkflowStatus.Running };
        await store.CreateAsync(child, Ct);
        time.Advance(TimeSpan.FromSeconds(4));
        root = await store.SaveAsync(root with { LeaseOwner = null, Status = WorkflowStatus.AwaitingInput }, 0, "root_waiting", Ct);
        time.Advance(TimeSpan.FromSeconds(2));
        child = await store.SaveAsync(child with { LeaseOwner = null, Status = WorkflowStatus.AwaitingInput }, 0, "child_waiting", Ct);
        Assert.AreEqual(TimeSpan.FromSeconds(9), (await store.GetBudgetAsync(new RunId("root"), Ct)).ExecutionTime);
        time.Advance(TimeSpan.FromHours(5));
        Assert.AreEqual(TimeSpan.FromSeconds(9), (await store.GetBudgetAsync(new RunId("root"), Ct)).ExecutionTime);
        child = await store.SaveAsync(child with { LeaseOwner = "child-again", Status = WorkflowStatus.Running }, child.Revision, "resumed", Ct);
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsExactlyAsync<WorkflowBudgetExceededException>(() => store.SaveAsync(child with { Usage = new WorkflowUsage(ToolCalls: 1) }, child.Revision, "tool_started", Ct));
        Assert.AreEqual(TimeSpan.Zero, (await store.GetBudgetAsync(new RunId("root"), Ct)).RemainingExecutionTime);
        Assert.AreEqual(0, (await store.GetAsync(new RunId("child"), Ct))!.Usage?.ToolCalls ?? 0);
    }

    [TestMethod]
    public async Task TimeLimitDoesNotRejectReturnedModelUsageReceipt()
    {
        var time = new ManualTimeProvider();
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, timeProvider: time);
        var root = Checkpoint("root", time) with { Budget = new(MaxTotalTokens: 50, MaxExecutionTime: TimeSpan.FromSeconds(2)), LeaseOwner = "owner", Status = WorkflowStatus.Running };
        await store.CreateAsync(root, Ct);
        root = await store.SaveAsync(StartAttempt(root, 40), 0, "model_started", Ct);
        time.Advance(TimeSpan.FromSeconds(3));
        root = await store.SaveAsync(Receipt(root, 60, WorkflowReservationState.Settled) with { LeaseOwner = null, Status = WorkflowStatus.Interrupted }, root.Revision, "receipt", Ct);
        var snapshot = await store.GetBudgetAsync(new RunId("root"), Ct);
        Assert.AreEqual(60, snapshot.ChargedTokens);
        Assert.AreEqual(TimeSpan.FromSeconds(3), snapshot.ExecutionTime);
        Assert.AreEqual(TimeSpan.Zero, snapshot.RemainingExecutionTime);
    }

    [TestMethod]
    public async Task SuccessfulResultCommitChecksLatestClockAndSharedReceiptsAtomically()
    {
        var time = new ManualTimeProvider();
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, timeProvider: time);
        var root = Checkpoint("root", time) with { Budget = new(MaxTotalTokens: 100, MaxExecutionTime: TimeSpan.FromSeconds(5)), LeaseOwner = "owner", Status = WorkflowStatus.Running };
        await store.CreateAsync(root, Ct);
        var child = Child("child", time);
        await store.CreateAsync(child, Ct);
        child = await store.SaveAsync(StartAttempt(child, 50), 0, "model_started", Ct);
        var result = new AgentRunResult(new RunId("root"), root.Request.SessionKey, "done", [], new Dictionary<string, object?>());
        child = await store.SaveAsync(Receipt(child, 101, WorkflowReservationState.Settled), child.Revision, "overshoot", Ct);
        await Assert.ThrowsExactlyAsync<WorkflowBudgetExceededException>(() => store.SaveAsync(root with { Status = WorkflowStatus.Succeeded, Result = result, LeaseOwner = null }, 0, "run_finished", Ct));
        Assert.IsNull((await store.GetAsync(new RunId("root"), Ct))!.Result);
        Assert.AreEqual(101, (await store.GetBudgetAsync(new RunId("root"), Ct)).ChargedTokens);
        var other = Checkpoint("other", time) with { Budget = new(MaxExecutionTime: TimeSpan.FromSeconds(5)), LeaseOwner = "owner", Status = WorkflowStatus.Running };
        await store.CreateAsync(other, Ct);
        time.Advance(TimeSpan.FromSeconds(5));
        var otherResult = result with { RunId = new RunId("other"), SessionKey = other.Request.SessionKey };
        await Assert.ThrowsExactlyAsync<WorkflowBudgetExceededException>(() => store.SaveAsync(other with { Status = WorkflowStatus.Succeeded, Result = otherResult, LeaseOwner = null }, 0, "run_finished", Ct));
        Assert.IsNull((await store.GetAsync(new RunId("other"), Ct))!.Result);
    }

    [TestMethod]
    public async Task ExactTokenCapAllowsSuccessButUnknownEffectsStillReserveSession()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var root = Checkpoint("root") with { Budget = new(MaxTotalTokens: 50) };
        await store.CreateAsync(root, Ct);
        root = await store.SaveAsync(StartAttempt(root, 50), 0, "model_started", Ct);
        root = await store.SaveAsync(Receipt(root, 50, WorkflowReservationState.Settled), root.Revision, "receipt", Ct);
        var result = new AgentRunResult(new RunId("root"), root.Request.SessionKey, "done", [], new Dictionary<string, object?>());
        root = await store.SaveAsync(root with { Status = WorkflowStatus.Succeeded, Result = result }, root.Revision, "success", Ct);
        Assert.AreEqual(WorkflowStatus.Succeeded, root.Status);
        var unknown = Checkpoint("unknown") with
        {
            ToolExecutions = [new("effect", new ToolCall("call", "mutate", JsonSerializer.SerializeToElement(new { })))],
            Control = WorkflowControl.Cancel,
            Status = WorkflowStatus.Cancelled,
            Result = result with { RunId = new RunId("unknown"), SessionKey = new SessionKey("session-unknown"), Status = AgentRunStatus.Cancelled }
        };
        // Seed a recorded uncertain effect before cancellation; the control write
        // must not free the conversation while its external outcome is unknown.
        var accepted = unknown with { Control = WorkflowControl.None, Result = null, Status = WorkflowStatus.NeedsReconciliation };
        await store.CreateAsync(accepted, Ct);
        unknown = await store.SaveAsync(unknown, 0, "cancelled", Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateAsync(Checkpoint("fresh") with { Request = unknown.Request }, Ct));
    }

    [TestMethod]
    public async Task PartialOrAllNullUsageCannotRefundSettledReservation()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var root = Checkpoint("root") with { Budget = new(MaxTotalTokens: 100) };
        await store.CreateAsync(root, Ct);
        root = await store.SaveAsync(StartAttempt(root, 50), 0, "model_started", Ct);
        var call = root.ModelCalls![0];
        root = await store.SaveAsync(root with
        {
            ModelCalls = [call with {
            Attempts = [call.Attempts![0] with { ReportedTokens = 10, UsageReported = true, State = WorkflowReservationState.Settled }] }],
            Usage = root.Usage! with { InputTokens = 10 }
        }, root.Revision, "partial_receipt", Ct);
        Assert.AreEqual(50, (await store.GetBudgetAsync(new RunId("root"), Ct)).ChargedTokens);
        root = await store.SaveAsync(StartAttempt(root, 50), root.Revision, "model_started", Ct);
        var calls = root.ModelCalls!.ToArray();
        calls[^1] = calls[^1] with { Attempts = [calls[^1].Attempts![0] with { UsageReported = true, State = WorkflowReservationState.Settled }] };
        root = await store.SaveAsync(root with { ModelCalls = calls }, root.Revision, "null_receipt", Ct);
        Assert.AreEqual(100, (await store.GetBudgetAsync(new RunId("root"), Ct)).ChargedTokens);
        await Assert.ThrowsExactlyAsync<WorkflowBudgetExceededException>(() => store.SaveAsync(root with { Usage = root.Usage! with { ToolCalls = 1 } }, root.Revision, "tool_started", Ct));
    }

    [TestMethod]
    public async Task AbandonedUnleasedAttemptStillBecomesUnknownOnReopen()
    {
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            var root = Checkpoint("root") with { Budget = new(MaxTotalTokens: 100), Status = WorkflowStatus.Paused };
            await store.CreateAsync(root, Ct);
            await store.SaveAsync(StartAttempt(root, 60), 0, "model_started", Ct);
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var recovered = (await reopened.GetAsync(new RunId("root"), Ct))!;
        Assert.AreEqual(WorkflowStatus.Paused, recovered.Status);
        Assert.AreEqual(WorkflowReservationState.Unknown, recovered.ModelCalls![0].Attempts![0].State);
        Assert.AreEqual(60, (await reopened.GetBudgetAsync(new RunId("root"), Ct)).ChargedTokens);
    }

    [TestMethod]
    public async Task AdmissionRejectsOverlappingOrAlreadyReportedNewAttempts()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var root = Checkpoint("root");
        await store.CreateAsync(root, Ct);
        root = await store.SaveAsync(StartAttempt(root, 30), 0, "model_started", Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(StartAttempt(root, 30), root.Revision, "overlap", Ct));
        var call = root.ModelCalls![0];
        root = await store.SaveAsync(root with { ModelCalls = [call with { Attempts = [call.Attempts![0] with { State = WorkflowReservationState.Unknown }] }] }, root.Revision, "interrupted", Ct);
        var proposed = StartAttempt(root, 30);
        var calls = proposed.ModelCalls!.ToArray();
        calls[^1] = calls[^1] with { Attempts = [calls[^1].Attempts![0] with { UsageReported = true }] };
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(proposed with { ModelCalls = calls }, root.Revision, "fake_receipt", Ct));
        root = await store.SaveAsync(proposed, root.Revision, "valid_retry", Ct);
        Assert.AreEqual(2, root.Usage!.ModelCalls);
    }

    [TestMethod]
    public async Task AttemptIdentityReservationAndReceiptsCannotBeRewrittenOrDropped()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var root = Checkpoint("root");
        await store.CreateAsync(root, Ct);
        root = await store.SaveAsync(StartAttempt(root, 50), 0, "model_started", Ct);
        var call = root.ModelCalls![0];
        var attempt = call.Attempts![0];
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(root with { ModelCalls = [call with { Attempts = [] }] }, root.Revision, "drop", Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(root with { ModelCalls = [call with { Attempts = [attempt with { ReservedTokens = 0 }] }] }, root.Revision, "rewrite", Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(root with { ModelCalls = [call with { Attempts = [attempt with { ReportedTokens = 10, UsageReported = true }] }] }, root.Revision, "receipt_without_usage", Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(root with { Usage = new WorkflowUsage(1, InputTokens: 10) }, root.Revision, "usage_without_receipt", Ct));
        root = await store.SaveAsync(Receipt(root, 20, WorkflowReservationState.Settled), root.Revision, "receipt", Ct);
        call = root.ModelCalls![0];
        attempt = call.Attempts![0];
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(root with { ModelCalls = [call with { Attempts = [attempt with { State = WorkflowReservationState.Running }] }] }, root.Revision, "reopen", Ct));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task LegacyMigrationPreservesHumanReceiptsAndCompletedReplay(int schemaVersion)
    {
        var response = new WorkflowHumanResponse("chosen", null, "owner", DateTimeOffset.UtcNow);
        var root = Checkpoint("root") with
        {
            Status = WorkflowStatus.Paused,
            ModelCalls = [new("complete", [new("text", "saved response")], "saved prompt"), new("interrupted", SystemPrompt: "pending prompt")],
            HumanRequests = [new("request", WorkflowHumanRequestKind.Input, "Which?", "route:0", DateTimeOffset.UtcNow, Response: response)],
            Result = schemaVersion == 1 ? new AgentRunResult(new RunId("root"), new SessionKey("session-root"), "Which?", [],
                new Dictionary<string, object?>(), AgentRunStatus.AwaitingInput) : null
        };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath)) await store.CreateAsync(root, Ct);
        await using (var raw = await Database.OpenAsync(DatabasePath))
        {
            string json;
            await using (var query = await raw.ExecuteAsync("SELECT payload FROM workflows WHERE run_id = 'root'"))
            {
                Assert.IsTrue(await query.MoveNextAsync());
                var node = JsonNode.Parse(query.Current[0].AsText)!;
                node["SchemaVersion"] = schemaVersion;
                json = node.ToJsonString();
            }
            await using var update = await raw.ExecuteAsync("UPDATE workflows SET payload = '" + json.Replace("'", "''") + "' WHERE run_id = 'root'");
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var migrated = (await reopened.GetAsync(new RunId("root"), Ct))!;
        Assert.AreEqual(3, migrated.SchemaVersion);
        Assert.AreEqual(response, migrated.HumanRequests!.Single(x => x.RequestId == "request").Response);
        Assert.AreEqual("saved response", migrated.ModelCalls![0].Response![0].Text);
        Assert.IsNull(migrated.ModelCalls[0].Attempts);
        Assert.AreEqual(WorkflowReservationState.Unknown, Assert.ContainsSingle(migrated.ModelCalls[1].Attempts!).State);
        Assert.IsTrue((await reopened.GetBudgetAsync(new RunId("root"), Ct)).ChargedTokens > 0);
        Assert.AreSequenceEqual(new[] { "accepted", "migrated_v3" }, (await reopened.ReadJournalAsync(new RunId("root"), Ct)).Select(x => x.EventType));
    }

    private static WorkflowCheckpoint Checkpoint(string id, ManualTimeProvider? time = null)
    {
        var now = time?.GetUtcNow() ?? DateTimeOffset.UtcNow;
        var request = new AgentRunRequest(new SessionKey("session-" + id), "objective", "fake", "model",
            new ToolSelectionPolicy(ToolProfile.Full), new CallerIdentity("owner", "Owner", true));
        return new(id, id, null, "task-" + id, request, now, now);
    }
    private static WorkflowCheckpoint Child(string id, ManualTimeProvider? time = null) => Checkpoint(id, time) with { RootRunId = "root", ParentRunId = "root" };
    private static WorkflowCheckpoint StartAttempt(WorkflowCheckpoint checkpoint, long reserved)
    {
        var calls = (checkpoint.ModelCalls ?? []).ToList();
        calls.Add(new("fingerprint-" + calls.Count, SystemPrompt: "prompt",
            Attempts: [new("attempt-" + calls.Count, reserved, 1)]));
        return checkpoint with { ModelCalls = calls, Usage = (checkpoint.Usage ?? new()) with { ModelCalls = (checkpoint.Usage?.ModelCalls ?? 0) + 1 } };
    }
    private static WorkflowCheckpoint Receipt(WorkflowCheckpoint checkpoint, long tokens, WorkflowReservationState state)
    {
        var calls = checkpoint.ModelCalls!.ToList();
        var call = calls[^1];
        calls[^1] = call with { Attempts = [call.Attempts![0] with { ReportedTokens = tokens, UsageReported = true, UsageComplete = true, State = state }] };
        return checkpoint with { ModelCalls = calls, Usage = checkpoint.Usage! with { InputTokens = checkpoint.Usage.InputTokens + tokens } };
    }
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
