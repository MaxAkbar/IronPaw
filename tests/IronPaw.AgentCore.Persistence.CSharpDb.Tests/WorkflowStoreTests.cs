using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using CSharpDB.Engine;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.AgentCore.Persistence.CSharpDb.Tests;

[TestClass]
public sealed class WorkflowStoreTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "IronPaw-CSharpDb-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "workflows.cdb");
    private static readonly CancellationToken Ct = CancellationToken.None;

    [TestMethod]
    public async Task ReopenPreservesFullCheckpointAndJournal()
    {
        var checkpoint = Checkpoint("run'quoted", "session'quoted") with
        {
            RootRunId = "root",
            ParentRunId = "parent",
            Request = Request("session'quoted") with
            {
                ParentRunId = new RunId("parent"),
                RootRunId = new RunId("root"),
                ParentSessionKey = new SessionKey("parent-session"),
                ToolPolicy = new ToolSelectionPolicy(ToolProfile.Full, InheritedPolicy: new ToolSelectionPolicy(ToolProfile.Minimal))
            },
            Budget = new WorkflowBudget(10, 11),
            Usage = new WorkflowUsage(1, 1, 13, 14, 15, 16, 1),
            InitialTranscript = [new ChatMessage(ChatRole.User, "Let's persist unicode: \u2603")],
            ModelCalls = [new WorkflowModelCall("request", [new WorkflowModelDelta("text", "saved")])],
            ToolExecutions = [Tool("execution", ToolResult.Success("confirmed"))]
        };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            await store.CreateAsync(Checkpoint("root", "root-session"), Ct);
            await store.CreateAsync(Checkpoint("parent", "parent-session") with { RootRunId = "root", ParentRunId = "root" }, Ct);
            await store.CreateAsync(checkpoint, Ct);
            checkpoint = await store.SaveAsync(checkpoint with
            {
                Status = WorkflowStatus.Succeeded,
                Transcript = [new ChatMessage(ChatRole.Assistant, "completed")],
                Result = new AgentRunResult(new RunId(checkpoint.RunId), checkpoint.Request.SessionKey, "completed",
                    [new ChatMessage(ChatRole.Assistant, "completed")], new Dictionary<string, object?> { ["number"] = 42 })
            }, 0, "completed'event", Ct);
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var actual = Assert.IsExactInstanceOfType<WorkflowCheckpoint>(await reopened.GetAsync(new RunId(checkpoint.RunId), Ct));
        Assert.AreEqual(1, actual.Revision);
        Assert.AreEqual(checkpoint.Request.SessionKey, actual.Request.SessionKey);
        Assert.AreEqual(checkpoint.Request.RootRunId, actual.Request.RootRunId);
        Assert.AreEqual(checkpoint.Request.ParentRunId, actual.Request.ParentRunId);
        Assert.AreEqual(checkpoint.Request.ParentSessionKey, actual.Request.ParentSessionKey);
        Assert.AreEqual(ToolProfile.Minimal, actual.Request.ToolPolicy.InheritedPolicy!.Profile);
        Assert.AreEqual(checkpoint.Usage, actual.Usage);
        Assert.AreEqual(checkpoint.Budget, actual.Budget);
        Assert.AreEqual("confirmed", actual.ToolExecutions![0].Result!.Content);
        Assert.AreEqual("saved", actual.ModelCalls![0].Response![0].Text);
        Assert.AreEqual(checkpoint.InitialTranscript![0], actual.InitialTranscript![0]);
        Assert.AreEqual(checkpoint.Result!.RunId, actual.Result!.RunId);
        Assert.AreEqual(42, ((JsonElement)actual.Result.Metadata["number"]!).GetInt32());
        Assert.AreSequenceEqual(new[] { "accepted", "completed'event" }, (await reopened.ReadJournalAsync(new RunId(actual.RunId), Ct)).Select(x => x.EventType));
    }

    [TestMethod]
    public async Task ConcurrentSavesOnlyCommitOneExpectedRevision()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var checkpoint = Checkpoint();
        await store.CreateAsync(checkpoint, Ct);
        async Task<bool> Save(int used)
        {
            try
            {
                await store.SaveAsync(checkpoint with { Usage = new WorkflowUsage(ModelCalls: used) }, 0, "usage", Ct);
                return true;
            }
            catch (WorkflowConflictException) { return false; }
        }
        var outcomes = await Task.WhenAll(Save(1), Save(2), Save(3), Save(4));
        Assert.ContainsSingle(x => x, outcomes);
        Assert.AreEqual(1, (await store.GetAsync(new RunId(checkpoint.RunId), Ct))!.Revision);
        Assert.AreEqual(2, (await store.ReadJournalAsync(new RunId(checkpoint.RunId), Ct)).Count);
    }

    [TestMethod]
    public async Task JournalWriteFailureRollsBackCheckpointAndUsage()
    {
        var checkpoint = Checkpoint();
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
            await store.CreateAsync(checkpoint, Ct);
        // Seed a conflicting next revision to force a real engine constraint
        // failure after the checkpoint UPDATE and before transaction COMMIT.
        await using (var database = await Database.OpenAsync(DatabasePath))
        {
            await using var result = await database.ExecuteAsync($"INSERT INTO workflow_journal VALUES (2, '{checkpoint.RunId}', 1, '{DateTimeOffset.UtcNow:O}', 'injected-conflict')");
        }
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            await Assert.ThrowsAsync<Exception>(() => store.SaveAsync(checkpoint with
            {
                Usage = new WorkflowUsage(7, 5),
                Status = WorkflowStatus.Running,
                LeaseOwner = "new-owner"
            }, 0, "must-roll-back", Ct));
            var actual = (await store.GetAsync(new RunId(checkpoint.RunId), Ct))!;
            Assert.AreEqual(0, actual.Revision);
            Assert.IsNull(actual.Usage);
            Assert.IsNull(actual.LeaseOwner);
            Assert.AreEqual(WorkflowStatus.Queued, actual.Status);
            Assert.AreEqual(2, (await store.ReadJournalAsync(new RunId(checkpoint.RunId), Ct)).Count);
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        Assert.AreEqual(0, (await reopened.GetAsync(new RunId(checkpoint.RunId), Ct))!.Revision);
    }

    [TestMethod]
    public async Task ReopenWithUnknownToolRequiresReconciliationAndPreservesConfirmedResults()
    {
        var checkpoint = Checkpoint() with
        {
            Status = WorkflowStatus.Running,
            LeaseOwner = "dead-process",
            ToolExecutions = [Tool("completed", ToolResult.Success("receipt")), Tool("unknown")],
            Usage = new WorkflowUsage(3, 2)
        };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
            await store.CreateAsync(checkpoint, Ct);
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var recovered = (await reopened.GetAsync(new RunId(checkpoint.RunId), Ct))!;
        Assert.AreEqual(WorkflowStatus.NeedsReconciliation, recovered.Status);
        Assert.IsNull(recovered.LeaseOwner);
        Assert.AreEqual(1, recovered.Revision);
        Assert.AreEqual("receipt", recovered.ToolExecutions![0].Result!.Content);
        Assert.IsNull(recovered.ToolExecutions[1].Result);
        Assert.AreEqual(checkpoint.Usage, recovered.Usage);
        Assert.AreSequenceEqual(new[] { "accepted", "recovered" }, (await reopened.ReadJournalAsync(new RunId(checkpoint.RunId), Ct)).Select(x => x.EventType));
    }

    [TestMethod]
    public async Task ReopenInterruptedModelClearsLeaseWithoutDiscardingResponseOrUsage()
    {
        var checkpoint = Checkpoint() with
        {
            Status = WorkflowStatus.Running,
            LeaseOwner = "dead-process",
            ModelCalls = [new WorkflowModelCall("complete", [new WorkflowModelDelta("text", "saved")]), new WorkflowModelCall("incomplete")],
            Usage = new WorkflowUsage(2, 0, InputTokens: 23)
        };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
            await store.CreateAsync(checkpoint, Ct);
        await using (var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            var recovered = (await reopened.GetAsync(new RunId(checkpoint.RunId), Ct))!;
            Assert.AreEqual(WorkflowStatus.Interrupted, recovered.Status);
            Assert.IsNull(recovered.LeaseOwner);
            Assert.AreEqual("saved", recovered.ModelCalls![0].Response![0].Text);
            Assert.IsNull(recovered.ModelCalls[1].Response);
            Assert.AreEqual(checkpoint.Usage, recovered.Usage);
        }
        await using var again = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        Assert.AreEqual(1, (await again.GetAsync(new RunId(checkpoint.RunId), Ct))!.Revision);
        Assert.AreEqual(2, (await again.ReadJournalAsync(new RunId(checkpoint.RunId), Ct)).Count);
    }

    [TestMethod]
    public async Task SecondLiveOwnerCannotOpenAndOwnershipReleasesOnDispose()
    {
        var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        try { await Assert.ThrowsAsync<IOException>(() => CSharpDbWorkflowStore.OpenAsync(DatabasePath)); }
        finally { store.Dispose(); }
        await using var next = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        Assert.IsEmpty(await next.ListAsync(Ct));
    }

    [TestMethod]
    [DataRow(WorkflowStatus.Queued)]
    [DataRow(WorkflowStatus.Interrupted)]
    [DataRow(WorkflowStatus.AwaitingInput)]
    [DataRow(WorkflowStatus.NeedsReconciliation)]
    [DataRow(WorkflowStatus.Cancelled)]
    public async Task ActiveSessionConflictLeavesNoAcceptedEntry(WorkflowStatus status)
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.CreateAsync(Checkpoint("first") with { Status = status }, Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateAsync(Checkpoint("second"), Ct));
        Assert.ContainsSingle(await store.ListAsync(Ct));
        Assert.IsEmpty(await store.ReadJournalAsync(new RunId("second"), Ct));
        await store.CreateAsync(Checkpoint("child", "child-session") with { ParentRunId = "first", RootRunId = "first" }, Ct);
        Assert.AreEqual(2, (await store.ListAsync(Ct)).Count);
    }

    [TestMethod]
    public async Task TerminalSessionAllowsNextWorkflowButDuplicateRunIdFails()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.CreateAsync(Checkpoint("first") with { Status = WorkflowStatus.Succeeded }, Ct);
        await store.CreateAsync(Checkpoint("second"), Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateAsync(Checkpoint("first", "another-session"), Ct));
        Assert.AreEqual(2, (await store.ListAsync(Ct)).Count);
    }

    [TestMethod]
    public async Task AwaitingInputWithResultAllowsReplyWhileResultCannotChange()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var checkpoint = Checkpoint("question");
        await store.CreateAsync(checkpoint, Ct);
        var result = new AgentRunResult(new RunId("question"), checkpoint.Request.SessionKey, "Which option?", [],
            new Dictionary<string, object?>(), AgentRunStatus.AwaitingInput);
        checkpoint = await store.SaveAsync(checkpoint with { Status = WorkflowStatus.AwaitingInput, Result = result }, 0, "question", Ct);
        await store.CreateAsync(Checkpoint("reply"), Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { Result = null }, checkpoint.Revision, "removed", Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { Result = result with { FinalAssistantMessage = "changed" } }, checkpoint.Revision, "changed", Ct));
        Assert.AreEqual(2, (await store.ReadJournalAsync(new RunId("question"), Ct)).Count);
    }

    [TestMethod]
    public async Task ChildLineageRequiresParentWithinRootAndOriginalCaller()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.CreateAsync(Checkpoint("root", "root-session"), Ct);
        await store.CreateAsync(Checkpoint("other-root", "other-session"), Ct);
        var child = Checkpoint("child", "child-session") with { RootRunId = "root", ParentRunId = "missing" };
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateAsync(child, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateAsync(child with { ParentRunId = "other-root" }, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateAsync(child with { ParentRunId = null }, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateAsync(child with { RootRunId = "child", ParentRunId = "root" }, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateAsync(child with { ParentRunId = "root", Request = child.Request with { Caller = new CallerIdentity("other", "Other", true) } }, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateAsync(child with { ParentRunId = "root", Request = child.Request with { Caller = child.Request.Caller with { IsOwner = false } } }, Ct));
        await store.CreateAsync(child with { ParentRunId = "root" }, Ct);
        Assert.AreEqual(3, (await store.ListAsync(Ct)).Count);
        Assert.ContainsSingle(await store.ReadJournalAsync(new RunId("child"), Ct));
    }

    [TestMethod]
    public async Task ChildrenCompeteAtomicallyForLastSharedModelCall()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.CreateAsync(Checkpoint("root", "root-session") with { Budget = new WorkflowBudget(1, 5) }, Ct);
        var first = Checkpoint("first", "first-session") with { RootRunId = "root", ParentRunId = "root" };
        var second = Checkpoint("second", "second-session") with { RootRunId = "root", ParentRunId = "root" };
        await store.CreateAsync(first, Ct);
        await store.CreateAsync(second, Ct);
        async Task<bool> Attempt(WorkflowCheckpoint child)
        {
            try { await store.SaveAsync(child with { Usage = new WorkflowUsage(ModelCalls: 1) }, 0, "model_started", Ct); return true; }
            catch (WorkflowBudgetExceededException) { return false; }
        }
        Assert.ContainsSingle(x => x, await Task.WhenAll(Attempt(first), Attempt(second)));
        Assert.AreEqual(1, (await ((IWorkflowStore)store).GetUsageAsync(new RunId("root"), Ct)).ModelCalls);
        Assert.AreEqual(1, ((await store.ReadJournalAsync(new RunId("first"), Ct)).Count - 1) + ((await store.ReadJournalAsync(new RunId("second"), Ct)).Count - 1));
    }

    [TestMethod]
    public async Task RootAndChildShareToolBudgetAndUsageSurvivesReopen()
    {
        var root = Checkpoint("root", "root-session") with { Budget = new WorkflowBudget(5, 1) };
        var child = Checkpoint("child", "child-session") with { RootRunId = "root", ParentRunId = "root" };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateAsync(child, Ct));
            await store.CreateAsync(root, Ct);
            await store.CreateAsync(child, Ct);
            await store.SaveAsync(child with { Usage = new WorkflowUsage(ToolCalls: 1) }, 0, "tool_started", Ct);
            await Assert.ThrowsExactlyAsync<WorkflowBudgetExceededException>(() => store.SaveAsync(root with { Usage = new WorkflowUsage(ToolCalls: 1) }, 0, "tool_started", Ct));
            Assert.AreEqual(0, (await store.GetAsync(new RunId("root"), Ct))!.Revision);
            Assert.ContainsSingle(await store.ReadJournalAsync(new RunId("root"), Ct));
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        Assert.AreEqual(1, (await ((IWorkflowStore)reopened).GetUsageAsync(new RunId("root"), Ct)).ToolCalls);
    }

    [TestMethod]
    public async Task AcceptedInputsBudgetAndSavedPromptCannotChange()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var checkpoint = Checkpoint() with { ModelCalls = [new WorkflowModelCall("fingerprint", SystemPrompt: "saved prompt")] };
        await store.CreateAsync(checkpoint, Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { Request = checkpoint.Request with { Caller = new CallerIdentity("different", "Other", true) } }, 0, "caller-changed", Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { Request = checkpoint.Request with { Model = "different" } }, 0, "model-changed", Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { Budget = new WorkflowBudget(200) }, 0, "budget-changed", Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { ModelCalls = [new WorkflowModelCall("fingerprint", SystemPrompt: "changed")] }, 0, "prompt-changed", Ct));
        checkpoint = await store.SaveAsync(checkpoint with { InitialTranscript = [], Request = checkpoint.Request with { ToolPolicy = checkpoint.Request.ToolPolicy with { SenderIsOwner = true } } }, 0, "initialized", Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { Request = checkpoint.Request with { ActiveMemories = [] } }, 1, "memories-changed", Ct));
    }

    [TestMethod]
    public async Task ConfirmedResultsAndConsumedUsageCannotBeRewritten()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var checkpoint = Checkpoint() with { ToolExecutions = [Tool("confirmed", ToolResult.Success("receipt"))], Usage = new WorkflowUsage(2, 1) };
        await store.CreateAsync(checkpoint, Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { ToolExecutions = [] }, 0, "removed", Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { ToolExecutions = [Tool("confirmed", ToolResult.Success("changed"))] }, 0, "changed", Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { Usage = new WorkflowUsage(1, 1) }, 0, "usage-reset", Ct));
        Assert.ContainsSingle(await store.ReadJournalAsync(new RunId(checkpoint.RunId), Ct));
    }

    [TestMethod]
    public async Task FutureSchemaFailsClosedBeforeRecoveryAndReleasesOwnership()
    {
        var checkpoint = Checkpoint() with { Status = WorkflowStatus.Running, LeaseOwner = "dead-process" };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => store.CreateAsync(checkpoint with { SchemaVersion = 4 }, Ct));
            await store.CreateAsync(checkpoint, Ct);
        }
        string payload;
        await using (var database = await Database.OpenAsync(DatabasePath))
        {
            await using (var result = await database.ExecuteAsync("SELECT payload FROM workflows WHERE id = 1"))
            {
                Assert.IsTrue(await result.MoveNextAsync());
                payload = result.Current[0].AsText.Replace("\"SchemaVersion\":3", "\"SchemaVersion\":4", StringComparison.Ordinal);
            }
            await using var update = await database.ExecuteAsync("UPDATE workflows SET payload = '" + payload.Replace("'", "''") + "' WHERE id = 1");
        }
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => CSharpDbWorkflowStore.OpenAsync(DatabasePath));
        await using var inspect = await Database.OpenAsync(DatabasePath);
        await using var actual = await inspect.ExecuteAsync("SELECT payload FROM workflows WHERE id = 1");
        Assert.IsTrue(await actual.MoveNextAsync());
        Assert.AreEqual(payload, actual.Current[0].AsText);
    }

    [TestMethod]
    public async Task AbruptProcessKillRecoversCommittedWalAndUnknownEffects()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(WorkflowStoreTests).Assembly.Location);
        start.ArgumentList.Add("crash-writer");
        start.ArgumentList.Add(DatabasePath);
        using var child = Process.Start(start)!;
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.AreEqual("committed", ready);
            await Assert.ThrowsAsync<IOException>(() => CSharpDbWorkflowStore.OpenAsync(DatabasePath));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await using var recoveredStore = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
            var checkpoint = Assert.IsExactInstanceOfType<WorkflowCheckpoint>(await recoveredStore.GetAsync(new RunId("crash-run"), Ct));
            Assert.AreEqual(WorkflowStatus.NeedsReconciliation, checkpoint.Status);
            Assert.AreEqual(1, checkpoint.Revision);
            Assert.IsNull(checkpoint.LeaseOwner);
            Assert.AreEqual("durable receipt", checkpoint.ToolExecutions![0].Result!.Content);
            Assert.IsNull(checkpoint.ToolExecutions[1].Result);
            Assert.AreEqual("durable response", checkpoint.ModelCalls![0].Response![0].Text);
            Assert.AreEqual(new WorkflowUsage(2, 2, InputTokens: 17), checkpoint.Usage);
            Assert.AreSequenceEqual(new[] { "accepted", "recovered" }, (await recoveredStore.ReadJournalAsync(new RunId("crash-run"), Ct)).Select(x => x.EventType));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestMethod]
    public async Task CancellationBeforeCreateDoesNotWriteAnything()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.CreateAsync(Checkpoint(), cancellation.Token));
        Assert.IsEmpty(await store.ListAsync(Ct));
    }

    [TestMethod]
    public async Task LegacyClarificationMigratesIntoDurableInputWithoutRewritingEvidence()
    {
        var checkpoint = Checkpoint() with
        {
            ModelCalls = [new("auto-skill", [new("text", "noop")]),
                new("planner", [new("text", "{\"needs_clarification\":true,\"questions\":[\"Which file?\"]}")])],
            ToolExecutions = [Tool("confirmed", ToolResult.Success("receipt")), Tool("unknown")],
            Status = WorkflowStatus.AwaitingInput,
            Result = new(new RunId("run"), new SessionKey("session"), "Which file?", [],
                new Dictionary<string, object?>(), AgentRunStatus.AwaitingInput)
        };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
            await store.CreateAsync(checkpoint, Ct);
        await RewritePayloadAsync("run", payload =>
        {
            payload["SchemaVersion"] = 1;
            foreach (var item in payload["ToolExecutions"]!.AsArray()) item!.AsObject().Remove("State");
        });
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var actual = (await reopened.GetAsync(new RunId("run"), Ct))!;
        Assert.AreEqual(3, actual.SchemaVersion);
        Assert.IsNull(actual.Result);
        Assert.AreEqual("Which file?", actual.Suspension!.FinalAssistantMessage);
        var request = Assert.ContainsSingle(actual.HumanRequests!);
        Assert.AreEqual("route:1", request.SourceKey);
        Assert.AreEqual("Which file?", request.Prompt);
        Assert.IsNull(request.Response);
        Assert.AreEqual(WorkflowToolState.Completed, actual.ToolExecutions![0].State);
        Assert.AreEqual(WorkflowToolState.Started, actual.ToolExecutions[1].State);
        Assert.AreEqual("receipt", actual.ToolExecutions[0].Result!.Content);
        Assert.AreSequenceEqual(new[] { "accepted", "migrated_v3" }, (await reopened.ReadJournalAsync(new RunId("run"), Ct)).Select(x => x.EventType));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => reopened.CreateAsync(Checkpoint("replacement"), Ct));
    }

    [TestMethod]
    public async Task LegacyHistoricalQuestionDoesNotRewindNewerConversation()
    {
        var checkpoint = Checkpoint("old") with
        {
            Status = WorkflowStatus.AwaitingInput,
            Result = new(new RunId("old"), new SessionKey("session"), "Old question", [],
                new Dictionary<string, object?>(), AgentRunStatus.AwaitingInput)
        };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            await store.CreateAsync(checkpoint, Ct);
            await store.CreateAsync(Checkpoint("new") with { CreatedAt = checkpoint.CreatedAt.AddSeconds(1) }, Ct);
        }
        await RewritePayloadAsync("old", payload => payload["SchemaVersion"] = 1);
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var old = (await reopened.GetAsync(new RunId("old"), Ct))!;
        Assert.AreEqual("Old question", old.Result!.FinalAssistantMessage);
        Assert.IsNull(old.HumanRequests);
        Assert.IsNull(old.Suspension);
    }

    [TestMethod]
    public async Task FutureVersionPreventsAllLegacyMigrations()
    {
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            await store.CreateAsync(Checkpoint("legacy", "legacy-session"), Ct);
            await store.CreateAsync(Checkpoint("future", "future-session"), Ct);
        }
        await RewritePayloadAsync("legacy", payload => payload["SchemaVersion"] = 1);
        await RewritePayloadAsync("future", payload => payload["SchemaVersion"] = 4);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => CSharpDbWorkflowStore.OpenAsync(DatabasePath));
        await using var database = await Database.OpenAsync(DatabasePath);
        await using var result = await database.ExecuteAsync("SELECT payload FROM workflows WHERE run_id = 'legacy'");
        Assert.IsTrue(await result.MoveNextAsync());
        Assert.AreEqual(1, JsonNode.Parse(result.Current[0].AsText)!["SchemaVersion"]!.GetValue<int>());
        await using var journal = await database.ExecuteAsync("SELECT revision FROM workflow_journal WHERE run_id = 'legacy'");
        Assert.IsTrue(await journal.MoveNextAsync());
        Assert.AreEqual(0, journal.Current[0].AsInteger);
        Assert.IsFalse(await journal.MoveNextAsync());
    }

    [TestMethod]
    public async Task MigrationJournalFailureRollsBackEveryCheckpoint()
    {
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            await store.CreateAsync(Checkpoint("first", "first-session"), Ct);
            await store.CreateAsync(Checkpoint("second", "second-session"), Ct);
        }
        await RewritePayloadAsync("first", payload => payload["SchemaVersion"] = 1);
        await RewritePayloadAsync("second", payload => payload["SchemaVersion"] = 1);
        await using (var database = await Database.OpenAsync(DatabasePath))
        {
            await using var conflict = await database.ExecuteAsync(
                $"INSERT INTO workflow_journal VALUES (3, 'second', 1, '{DateTimeOffset.UtcNow:O}', 'injected-conflict')");
        }
        await Assert.ThrowsAsync<Exception>(() => CSharpDbWorkflowStore.OpenAsync(DatabasePath));
        await using var inspect = await Database.OpenAsync(DatabasePath);
        await using var actual = await inspect.ExecuteAsync("SELECT payload FROM workflows ORDER BY id");
        for (var i = 0; i < 2; i++)
        {
            Assert.IsTrue(await actual.MoveNextAsync());
            var payload = JsonNode.Parse(actual.Current[0].AsText)!;
            Assert.AreEqual(1, payload["SchemaVersion"]!.GetValue<int>());
            Assert.AreEqual(0, payload["Revision"]!.GetValue<int>());
        }
    }

    [TestMethod]
    [DataRow(WorkflowHumanRequestKind.Input)]
    [DataRow(WorkflowHumanRequestKind.ToolApproval)]
    public async Task HumanResponsesAreDurableAndImmutable(WorkflowHumanRequestKind kind)
    {
        var checkpoint = Checkpoint() with
        {
            ToolExecutions = [Tool("operation") with { State = WorkflowToolState.Planned }],
            HumanRequests = [new("human", kind, "Question", "tool:operation", DateTimeOffset.UtcNow,
                kind == WorkflowHumanRequestKind.ToolApproval ? "operation" : null,
                kind == WorkflowHumanRequestKind.ToolApproval ? "exact-operation-hash" : null)],
            Status = WorkflowStatus.AwaitingInput
        };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            await store.CreateAsync(checkpoint, Ct);
            var response = new WorkflowHumanResponse(kind == WorkflowHumanRequestKind.Input ? "Answer" : null,
                kind == WorkflowHumanRequestKind.ToolApproval ? true : null, "owner", DateTimeOffset.UtcNow);
            checkpoint = await store.SaveAsync(checkpoint with
            {
                HumanRequests = [checkpoint.HumanRequests![0] with { Response = response }]
            }, 0, "human_answered", Ct);
            await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with
            {
                HumanRequests = [checkpoint.HumanRequests![0] with { Response = response with { CallerId = "other" } }]
            }, 1, "spoof", Ct));
            await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with
            {
                HumanRequests = [checkpoint.HumanRequests![0] with { Prompt = "Other operation" }]
            }, 1, "rewrite", Ct));
            await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with
            {
                HumanRequests = [checkpoint.HumanRequests![0] with { Response = null }]
            }, 1, "clear", Ct));
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var actual = (await reopened.GetAsync(new RunId("run"), Ct))!;
        Assert.AreEqual(checkpoint.HumanRequests![0], Assert.ContainsSingle(actual.HumanRequests!));
    }

    [TestMethod]
    [DataRow(WorkflowControl.Pause)]
    [DataRow(WorkflowControl.Cancel)]
    public async Task RootControlRejectsNewDescendantWorkButAllowsInflightOutcomes(WorkflowControl control)
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var root = Checkpoint("root", "root-session");
        var child = Checkpoint("child", "child-session") with
        {
            RootRunId = "root",
            ParentRunId = "root",
            ModelCalls = [new("inflight")],
            Usage = new(1, 1),
            ToolExecutions = [Tool("inflight"), Tool("planned") with { State = WorkflowToolState.Planned }]
        };
        await store.CreateAsync(root, Ct);
        await store.CreateAsync(child, Ct);
        root = await store.SaveAsync(root with { Control = control }, 0, "control_requested", Ct);
        async Task RejectAdmission(WorkflowCheckpoint next)
        {
            if (control == WorkflowControl.Cancel)
                await Assert.ThrowsExactlyAsync<WorkflowCancelledException>(() => store.SaveAsync(next, child.Revision, "admission", Ct));
            else await Assert.ThrowsExactlyAsync<WorkflowSuspendedException>(() => store.SaveAsync(next, child.Revision, "admission", Ct));
        }
        await RejectAdmission(child with { Usage = new(2, 1) });
        await RejectAdmission(child with { ToolExecutions = [child.ToolExecutions![0], child.ToolExecutions[1] with { State = WorkflowToolState.Started }] });
        child = await store.SaveAsync(child with
        {
            ModelCalls = [new("inflight", [new("text", "response")])],
            ToolExecutions = [child.ToolExecutions![0] with { State = WorkflowToolState.Completed, Result = ToolResult.Success("receipt") }, child.ToolExecutions[1]],
            Usage = child.Usage! with { InputTokens = 11 }
        }, 0, "inflight_completed", Ct);
        Assert.AreEqual("receipt", child.ToolExecutions![0].Result!.Content);
        if (control == WorkflowControl.Cancel)
            await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(root with { Control = WorkflowControl.None }, root.Revision, "undo_cancel", Ct));
        else await store.SaveAsync(root with { Control = WorkflowControl.None }, root.Revision, "resume", Ct);
    }

    [TestMethod]
    [DataRow(WorkflowHumanRequestKind.Input)]
    [DataRow(WorkflowHumanRequestKind.ToolApproval)]
    public async Task RootCancellationAtomicallyRejectsDescendantHumanResponse(WorkflowHumanRequestKind kind)
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var root = Checkpoint("root", "root-session");
        var child = Checkpoint("child", "child-session") with
        {
            RootRunId = "root",
            ParentRunId = "root",
            ToolExecutions = [Tool("operation") with { State = WorkflowToolState.Planned }, Tool("inflight")],
            HumanRequests = [new("human", kind, "Question", "tool:operation", DateTimeOffset.UtcNow,
                kind == WorkflowHumanRequestKind.ToolApproval ? "operation" : null,
                kind == WorkflowHumanRequestKind.ToolApproval ? "operation-hash" : null)]
        };
        await store.CreateAsync(root, Ct);
        await store.CreateAsync(child, Ct);
        // A child command may have read its unchanged revision before root
        // cancellation committed. Only an in-transaction root check closes it.
        await store.SaveAsync(root with { Control = WorkflowControl.Cancel }, 0, "cancel_requested", Ct);
        var response = new WorkflowHumanResponse(kind == WorkflowHumanRequestKind.Input ? "Answer" : null,
            kind == WorkflowHumanRequestKind.ToolApproval ? true : null, "owner", DateTimeOffset.UtcNow);
        await Assert.ThrowsExactlyAsync<WorkflowCancelledException>(() => store.SaveAsync(child with
        {
            HumanRequests = [child.HumanRequests![0] with { Response = response }]
        }, 0, "human_answered", Ct));
        child = await store.SaveAsync(child with
        {
            ToolExecutions = [child.ToolExecutions![0], child.ToolExecutions[1] with {
            Result = ToolResult.Success("known receipt"), State = WorkflowToolState.Completed }]
        }, 0, "tool_completed", Ct);
        Assert.IsNull(child.HumanRequests![0].Response);
        Assert.AreEqual("known receipt", child.ToolExecutions![1].Result!.Content);
        Assert.AreSequenceEqual(new[] { "accepted", "tool_completed" }, (await store.ReadJournalAsync(new RunId("child"), Ct)).Select(x => x.EventType));
    }

    [TestMethod]
    [DataRow(WorkflowControl.Pause, AgentRunStatus.Succeeded)]
    [DataRow(WorkflowControl.Cancel, AgentRunStatus.Succeeded)]
    [DataRow(WorkflowControl.Pause, AgentRunStatus.BudgetExhausted)]
    [DataRow(WorkflowControl.Cancel, AgentRunStatus.BudgetExhausted)]
    public async Task RootControlAtomicallyRejectsDescendantTerminalResultButAllowsSettling(WorkflowControl control, AgentRunStatus resultStatus)
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var root = Checkpoint("root", "root-session");
        var child = Checkpoint("child", "child-session") with
        {
            RootRunId = "root",
            ParentRunId = "root",
            LeaseOwner = "live-child",
            Status = WorkflowStatus.Running,
            ModelCalls = [new("saved", [new("text", "completed response")])]
        };
        await store.CreateAsync(root, Ct);
        await store.CreateAsync(child, Ct);
        await store.SaveAsync(root with { Control = control }, 0, "control_requested", Ct);
        var success = new AgentRunResult(new RunId("child"), child.Request.SessionKey, "Completed", [], new Dictionary<string, object?>(), resultStatus);
        var proposed = child with { Result = success, Status = resultStatus == AgentRunStatus.Succeeded ? WorkflowStatus.Succeeded : WorkflowStatus.BudgetExhausted, LeaseOwner = null };
        if (control == WorkflowControl.Cancel)
            await Assert.ThrowsExactlyAsync<WorkflowCancelledException>(() => store.SaveAsync(proposed, 0, "run_finished", Ct));
        else await Assert.ThrowsExactlyAsync<WorkflowSuspendedException>(() => store.SaveAsync(proposed, 0, "run_finished", Ct));
        var settledResult = success with { Status = control == WorkflowControl.Cancel ? AgentRunStatus.Cancelled : AgentRunStatus.Paused };
        var settled = await store.SaveAsync(child with
        {
            Result = control == WorkflowControl.Cancel ? settledResult : null,
            Suspension = control == WorkflowControl.Pause ? settledResult : null,
            Status = control == WorkflowControl.Cancel ? WorkflowStatus.Cancelled : WorkflowStatus.Paused,
            LeaseOwner = null
        }, 0, "control_settled", Ct);
        Assert.IsNull(settled.LeaseOwner);
        Assert.AreEqual("completed response", settled.ModelCalls![0].Response![0].Text);
    }

    [TestMethod]
    [DataRow(WorkflowControl.Pause, WorkflowStatus.Paused)]
    [DataRow(WorkflowControl.Cancel, WorkflowStatus.Cancelled)]
    public async Task RecoveryPreservesControlAndClearsStaleLeases(WorkflowControl control, WorkflowStatus expected)
    {
        var checkpoint = Checkpoint() with
        {
            Control = control,
            Status = WorkflowStatus.Running,
            LeaseOwner = "dead-host",
            ToolExecutions = [Tool("planned") with { State = WorkflowToolState.Planned }]
        };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
            await store.CreateAsync(checkpoint, Ct);
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var actual = (await reopened.GetAsync(new RunId("run"), Ct))!;
        Assert.AreEqual(control, actual.Control);
        Assert.AreEqual(expected, actual.Status);
        Assert.IsNull(actual.LeaseOwner);
        Assert.IsFalse(WorkflowIdentity.HasUnknownEffects(actual));
    }

    [TestMethod]
    public async Task RootPauseIsAppliedWhenRecoveringDescendantLease()
    {
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            var root = Checkpoint("root", "root-session");
            await store.CreateAsync(root, Ct);
            await store.CreateAsync(Checkpoint("child", "child-session") with
            {
                RootRunId = "root",
                ParentRunId = "root",
                LeaseOwner = "dead-child",
                Status = WorkflowStatus.Running
            }, Ct);
            await store.SaveAsync(root with { Control = WorkflowControl.Pause, Status = WorkflowStatus.Paused }, 0, "root_paused", Ct);
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var child = (await reopened.GetAsync(new RunId("child"), Ct))!;
        Assert.AreEqual(WorkflowStatus.Paused, child.Status);
        Assert.IsNull(child.LeaseOwner);
    }

    [TestMethod]
    public async Task CancellationRequestKeepsSessionReservedUntilActiveLeaseSettles()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var checkpoint = Checkpoint() with { LeaseOwner = "live-host", Status = WorkflowStatus.Running };
        await store.CreateAsync(checkpoint, Ct);
        checkpoint = await store.SaveAsync(checkpoint with { Control = WorkflowControl.Cancel }, 0, "cancel_requested", Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateAsync(Checkpoint("next"), Ct));
        await store.SaveAsync(checkpoint with { LeaseOwner = null, Status = WorkflowStatus.Cancelled }, checkpoint.Revision, "cancel_settled", Ct);
        await store.CreateAsync(Checkpoint("next"), Ct);
    }

    [TestMethod]
    public async Task CancelledUnknownEffectsRemainReconcilableWithoutRestoringExecution()
    {
        var checkpoint = Checkpoint() with
        {
            Control = WorkflowControl.Cancel,
            Status = WorkflowStatus.Running,
            LeaseOwner = "dead-host",
            ToolExecutions = [Tool("unknown")],
            Usage = new(1, 1)
        };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            // Cancellation races with an already admitted tool, not acceptance.
            await store.CreateAsync(checkpoint with { Control = WorkflowControl.None }, Ct);
            checkpoint = await store.SaveAsync(checkpoint, 0, "cancel_requested", Ct);
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var actual = (await reopened.GetAsync(new RunId("run"), Ct))!;
        Assert.AreEqual(WorkflowStatus.NeedsReconciliation, actual.Status);
        actual = await reopened.SaveAsync(actual with
        {
            ToolExecutions = [actual.ToolExecutions![0] with {
            State = WorkflowToolState.Completed, Result = ToolResult.Success("verified receipt"), ReconciliationNote = "Inspected destination" }],
            Status = WorkflowStatus.Cancelled
        }, actual.Revision, "tool_reconciled", Ct);
        Assert.AreEqual(WorkflowControl.Cancel, actual.Control);
        await Assert.ThrowsExactlyAsync<WorkflowCancelledException>(() => reopened.SaveAsync(actual with { Usage = new(2, 1) }, actual.Revision, "model_started", Ct));
        await reopened.CreateAsync(Checkpoint("next"), Ct);
    }

    [TestMethod]
    public async Task OrderedIntentAndHumanRequestRecordsCannotBeReordered()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var checkpoint = Checkpoint() with
        {
            ToolExecutions = [Tool("a"), Tool("b")],
            HumanRequests = [new("a", WorkflowHumanRequestKind.Input, "Question A", "route:0", DateTimeOffset.UtcNow),
                new("b", WorkflowHumanRequestKind.Input, "Question B", "route:1", DateTimeOffset.UtcNow)]
        };
        await store.CreateAsync(checkpoint, Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with
        {
            ToolExecutions = checkpoint.ToolExecutions!.Reverse().ToArray()
        }, 0, "reordered_tools", Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with
        {
            HumanRequests = checkpoint.HumanRequests!.Reverse().ToArray()
        }, 0, "reordered_questions", Ct));
    }

    private async Task RewritePayloadAsync(string runId, Action<JsonObject> edit)
    {
        await using var database = await Database.OpenAsync(DatabasePath);
        JsonObject payload;
        await using (var query = await database.ExecuteAsync($"SELECT payload FROM workflows WHERE run_id = '{runId}'"))
        {
            Assert.IsTrue(await query.MoveNextAsync());
            payload = JsonNode.Parse(query.Current[0].AsText)!.AsObject();
        }
        edit(payload);
        await using var update = await database.ExecuteAsync($"UPDATE workflows SET payload = '{payload.ToJsonString().Replace("'", "''")}' WHERE run_id = '{runId}'");
    }

    private static WorkflowCheckpoint Checkpoint(string runId = "run", string session = "session") =>
        new(runId, runId, null, "task-" + runId, Request(session), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static AgentRunRequest Request(string session) => new(new SessionKey(session), "objective", "fake", "fake-model",
        new ToolSelectionPolicy(ToolProfile.Full), new CallerIdentity("owner", "Owner", true));

    private static WorkflowToolExecution Tool(string executionId, ToolResult? result = null) =>
        new(executionId, new ToolCall("call-" + executionId, "fake-tool", JsonSerializer.SerializeToElement(new { path = "a'b" })), result,
            State: result is null ? WorkflowToolState.Started : WorkflowToolState.Completed);

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
