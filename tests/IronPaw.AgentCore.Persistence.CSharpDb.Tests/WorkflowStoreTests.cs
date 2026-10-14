using System.Text.Json;
using System.Diagnostics;
using CSharpDB.Engine;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.AgentCore.Persistence.CSharpDb.Tests;

public sealed class WorkflowStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "IronPaw-CSharpDb-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "workflows.cdb");
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task ReopenPreservesFullCheckpointAndJournal()
    {
        var checkpoint = Checkpoint("run'quoted", "session'quoted") with
        {
            RootRunId = "root",
            ParentRunId = "parent",
            Request = Request("session'quoted") with
            {
                ParentRunId = new RunId("parent"), RootRunId = new RunId("root"),
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
        var actual = Assert.IsType<WorkflowCheckpoint>(await reopened.GetAsync(new RunId(checkpoint.RunId), Ct));
        Assert.Equal(1, actual.Revision);
        Assert.Equal(checkpoint.Request.SessionKey, actual.Request.SessionKey);
        Assert.Equal(checkpoint.Request.RootRunId, actual.Request.RootRunId);
        Assert.Equal(checkpoint.Request.ParentRunId, actual.Request.ParentRunId);
        Assert.Equal(checkpoint.Request.ParentSessionKey, actual.Request.ParentSessionKey);
        Assert.Equal(ToolProfile.Minimal, actual.Request.ToolPolicy.InheritedPolicy!.Profile);
        Assert.Equal(checkpoint.Usage, actual.Usage);
        Assert.Equal(checkpoint.Budget, actual.Budget);
        Assert.Equal("confirmed", actual.ToolExecutions![0].Result!.Content);
        Assert.Equal("saved", actual.ModelCalls![0].Response![0].Text);
        Assert.Equal(checkpoint.InitialTranscript![0], actual.InitialTranscript![0]);
        Assert.Equal(checkpoint.Result!.RunId, actual.Result!.RunId);
        Assert.Equal(42, ((JsonElement)actual.Result.Metadata["number"]!).GetInt32());
        Assert.Equal(new[] { "accepted", "completed'event" }, (await reopened.ReadJournalAsync(new RunId(actual.RunId), Ct)).Select(x => x.EventType));
    }

    [Fact]
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
        Assert.Single(outcomes, x => x);
        Assert.Equal(1, (await store.GetAsync(new RunId(checkpoint.RunId), Ct))!.Revision);
        Assert.Equal(2, (await store.ReadJournalAsync(new RunId(checkpoint.RunId), Ct)).Count);
    }

    [Fact]
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
            await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(checkpoint with
            {
                Usage = new WorkflowUsage(7, 5), Status = WorkflowStatus.Running, LeaseOwner = "new-owner"
            }, 0, "must-roll-back", Ct));
            var actual = (await store.GetAsync(new RunId(checkpoint.RunId), Ct))!;
            Assert.Equal(0, actual.Revision);
            Assert.Null(actual.Usage);
            Assert.Null(actual.LeaseOwner);
            Assert.Equal(WorkflowStatus.Queued, actual.Status);
            Assert.Equal(2, (await store.ReadJournalAsync(new RunId(checkpoint.RunId), Ct)).Count);
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        Assert.Equal(0, (await reopened.GetAsync(new RunId(checkpoint.RunId), Ct))!.Revision);
    }

    [Fact]
    public async Task ReopenWithUnknownToolRequiresReconciliationAndPreservesConfirmedResults()
    {
        var checkpoint = Checkpoint() with
        {
            Status = WorkflowStatus.Running, LeaseOwner = "dead-process",
            ToolExecutions = [Tool("completed", ToolResult.Success("receipt")), Tool("unknown")],
            Usage = new WorkflowUsage(3, 2)
        };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
            await store.CreateAsync(checkpoint, Ct);
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var recovered = (await reopened.GetAsync(new RunId(checkpoint.RunId), Ct))!;
        Assert.Equal(WorkflowStatus.NeedsReconciliation, recovered.Status);
        Assert.Null(recovered.LeaseOwner);
        Assert.Equal(1, recovered.Revision);
        Assert.Equal("receipt", recovered.ToolExecutions![0].Result!.Content);
        Assert.Null(recovered.ToolExecutions[1].Result);
        Assert.Equal(checkpoint.Usage, recovered.Usage);
        Assert.Equal(new[] { "accepted", "recovered" }, (await reopened.ReadJournalAsync(new RunId(checkpoint.RunId), Ct)).Select(x => x.EventType));
    }

    [Fact]
    public async Task ReopenInterruptedModelClearsLeaseWithoutDiscardingResponseOrUsage()
    {
        var checkpoint = Checkpoint() with
        {
            Status = WorkflowStatus.Running, LeaseOwner = "dead-process",
            ModelCalls = [new WorkflowModelCall("complete", [new WorkflowModelDelta("text", "saved")]), new WorkflowModelCall("incomplete")],
            Usage = new WorkflowUsage(2, 0, InputTokens: 23)
        };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
            await store.CreateAsync(checkpoint, Ct);
        await using (var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            var recovered = (await reopened.GetAsync(new RunId(checkpoint.RunId), Ct))!;
            Assert.Equal(WorkflowStatus.Interrupted, recovered.Status);
            Assert.Null(recovered.LeaseOwner);
            Assert.Equal("saved", recovered.ModelCalls![0].Response![0].Text);
            Assert.Null(recovered.ModelCalls[1].Response);
            Assert.Equal(checkpoint.Usage, recovered.Usage);
        }
        await using var again = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        Assert.Equal(1, (await again.GetAsync(new RunId(checkpoint.RunId), Ct))!.Revision);
        Assert.Equal(2, (await again.ReadJournalAsync(new RunId(checkpoint.RunId), Ct)).Count);
    }

    [Fact]
    public async Task SecondLiveOwnerCannotOpenAndOwnershipReleasesOnDispose()
    {
        var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        try { await Assert.ThrowsAnyAsync<IOException>(() => CSharpDbWorkflowStore.OpenAsync(DatabasePath)); }
        finally { store.Dispose(); }
        await using var next = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        Assert.Empty(await next.ListAsync(Ct));
    }

    [Theory]
    [InlineData(WorkflowStatus.Queued)]
    [InlineData(WorkflowStatus.Interrupted)]
    [InlineData(WorkflowStatus.AwaitingInput)]
    [InlineData(WorkflowStatus.NeedsReconciliation)]
    [InlineData(WorkflowStatus.Cancelled)]
    public async Task ActiveSessionConflictLeavesNoAcceptedEntry(WorkflowStatus status)
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.CreateAsync(Checkpoint("first") with { Status = status }, Ct);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.CreateAsync(Checkpoint("second"), Ct));
        Assert.Single(await store.ListAsync(Ct));
        Assert.Empty(await store.ReadJournalAsync(new RunId("second"), Ct));
        await store.CreateAsync(Checkpoint("child", "child-session") with { ParentRunId = "first", RootRunId = "first" }, Ct);
        Assert.Equal(2, (await store.ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task TerminalSessionAllowsNextWorkflowButDuplicateRunIdFails()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.CreateAsync(Checkpoint("first") with { Status = WorkflowStatus.Succeeded }, Ct);
        await store.CreateAsync(Checkpoint("second"), Ct);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.CreateAsync(Checkpoint("first", "another-session"), Ct));
        Assert.Equal(2, (await store.ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task AwaitingInputWithResultAllowsReplyWhileResultCannotChange()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var checkpoint = Checkpoint("question");
        await store.CreateAsync(checkpoint, Ct);
        var result = new AgentRunResult(new RunId("question"), checkpoint.Request.SessionKey, "Which option?", [],
            new Dictionary<string, object?>(), AgentRunStatus.AwaitingInput);
        checkpoint = await store.SaveAsync(checkpoint with { Status = WorkflowStatus.AwaitingInput, Result = result }, 0, "question", Ct);
        await store.CreateAsync(Checkpoint("reply"), Ct);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { Result = null }, checkpoint.Revision, "removed", Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { Result = result with { FinalAssistantMessage = "changed" } }, checkpoint.Revision, "changed", Ct));
        Assert.Equal(2, (await store.ReadJournalAsync(new RunId("question"), Ct)).Count);
    }

    [Fact]
    public async Task ChildLineageRequiresParentWithinRootAndOriginalCaller()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.CreateAsync(Checkpoint("root", "root-session"), Ct);
        await store.CreateAsync(Checkpoint("other-root", "other-session"), Ct);
        var child = Checkpoint("child", "child-session") with { RootRunId = "root", ParentRunId = "missing" };
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.CreateAsync(child, Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.CreateAsync(child with { ParentRunId = "other-root" }, Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.CreateAsync(child with { ParentRunId = null }, Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.CreateAsync(child with { RootRunId = "child", ParentRunId = "root" }, Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.CreateAsync(child with { ParentRunId = "root", Request = child.Request with { Caller = new CallerIdentity("other", "Other", true) } }, Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.CreateAsync(child with { ParentRunId = "root", Request = child.Request with { Caller = child.Request.Caller with { IsOwner = false } } }, Ct));
        await store.CreateAsync(child with { ParentRunId = "root" }, Ct);
        Assert.Equal(3, (await store.ListAsync(Ct)).Count);
        Assert.Single(await store.ReadJournalAsync(new RunId("child"), Ct));
    }

    [Fact]
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
        Assert.Single(await Task.WhenAll(Attempt(first), Attempt(second)), x => x);
        Assert.Equal(1, (await ((IWorkflowStore)store).GetUsageAsync(new RunId("root"), Ct)).ModelCalls);
        Assert.Equal(1, ((await store.ReadJournalAsync(new RunId("first"), Ct)).Count - 1) + ((await store.ReadJournalAsync(new RunId("second"), Ct)).Count - 1));
    }

    [Fact]
    public async Task RootAndChildShareToolBudgetAndUsageSurvivesReopen()
    {
        var root = Checkpoint("root", "root-session") with { Budget = new WorkflowBudget(5, 1) };
        var child = Checkpoint("child", "child-session") with { RootRunId = "root", ParentRunId = "root" };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            await Assert.ThrowsAsync<WorkflowConflictException>(() => store.CreateAsync(child, Ct));
            await store.CreateAsync(root, Ct);
            await store.CreateAsync(child, Ct);
            await store.SaveAsync(child with { Usage = new WorkflowUsage(ToolCalls: 1) }, 0, "tool_started", Ct);
            await Assert.ThrowsAsync<WorkflowBudgetExceededException>(() => store.SaveAsync(root with { Usage = new WorkflowUsage(ToolCalls: 1) }, 0, "tool_started", Ct));
            Assert.Equal(0, (await store.GetAsync(new RunId("root"), Ct))!.Revision);
            Assert.Single(await store.ReadJournalAsync(new RunId("root"), Ct));
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        Assert.Equal(1, (await ((IWorkflowStore)reopened).GetUsageAsync(new RunId("root"), Ct)).ToolCalls);
    }

    [Fact]
    public async Task AcceptedInputsBudgetAndSavedPromptCannotChange()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var checkpoint = Checkpoint() with { ModelCalls = [new WorkflowModelCall("fingerprint", SystemPrompt: "saved prompt")] };
        await store.CreateAsync(checkpoint, Ct);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { Request = checkpoint.Request with { Caller = new CallerIdentity("different", "Other", true) } }, 0, "caller-changed", Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { Request = checkpoint.Request with { Model = "different" } }, 0, "model-changed", Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { Budget = new WorkflowBudget(200) }, 0, "budget-changed", Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { ModelCalls = [new WorkflowModelCall("fingerprint", SystemPrompt: "changed")] }, 0, "prompt-changed", Ct));
        checkpoint = await store.SaveAsync(checkpoint with { InitialTranscript = [], Request = checkpoint.Request with { ToolPolicy = checkpoint.Request.ToolPolicy with { SenderIsOwner = true } } }, 0, "initialized", Ct);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { Request = checkpoint.Request with { ActiveMemories = [] } }, 1, "memories-changed", Ct));
    }

    [Fact]
    public async Task ConfirmedResultsAndConsumedUsageCannotBeRewritten()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var checkpoint = Checkpoint() with { ToolExecutions = [Tool("confirmed", ToolResult.Success("receipt"))], Usage = new WorkflowUsage(2, 1) };
        await store.CreateAsync(checkpoint, Ct);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { ToolExecutions = [] }, 0, "removed", Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { ToolExecutions = [Tool("confirmed", ToolResult.Success("changed"))] }, 0, "changed", Ct));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.SaveAsync(checkpoint with { Usage = new WorkflowUsage(1, 1) }, 0, "usage-reset", Ct));
        Assert.Single(await store.ReadJournalAsync(new RunId(checkpoint.RunId), Ct));
    }

    [Fact]
    public async Task FutureSchemaFailsClosedBeforeRecoveryAndReleasesOwnership()
    {
        var checkpoint = Checkpoint() with { Status = WorkflowStatus.Running, LeaseOwner = "dead-process" };
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => store.CreateAsync(checkpoint with { SchemaVersion = 2 }, Ct));
            await store.CreateAsync(checkpoint, Ct);
        }
        string payload;
        await using (var database = await Database.OpenAsync(DatabasePath))
        {
            await using (var result = await database.ExecuteAsync("SELECT payload FROM workflows WHERE id = 1"))
            {
                Assert.True(await result.MoveNextAsync());
                payload = result.Current[0].AsText.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2", StringComparison.Ordinal);
            }
            await using var update = await database.ExecuteAsync("UPDATE workflows SET payload = '" + payload.Replace("'", "''") + "' WHERE id = 1");
        }
        await Assert.ThrowsAsync<NotSupportedException>(() => CSharpDbWorkflowStore.OpenAsync(DatabasePath));
        await using var inspect = await Database.OpenAsync(DatabasePath);
        await using var actual = await inspect.ExecuteAsync("SELECT payload FROM workflows WHERE id = 1");
        Assert.True(await actual.MoveNextAsync());
        Assert.Equal(payload, actual.Current[0].AsText);
    }

    [Fact]
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
            Assert.Equal("committed", ready);
            await Assert.ThrowsAnyAsync<IOException>(() => CSharpDbWorkflowStore.OpenAsync(DatabasePath));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await using var recoveredStore = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
            var checkpoint = Assert.IsType<WorkflowCheckpoint>(await recoveredStore.GetAsync(new RunId("crash-run"), Ct));
            Assert.Equal(WorkflowStatus.NeedsReconciliation, checkpoint.Status);
            Assert.Equal(1, checkpoint.Revision);
            Assert.Null(checkpoint.LeaseOwner);
            Assert.Equal("durable receipt", checkpoint.ToolExecutions![0].Result!.Content);
            Assert.Null(checkpoint.ToolExecutions[1].Result);
            Assert.Equal("durable response", checkpoint.ModelCalls![0].Response![0].Text);
            Assert.Equal(new WorkflowUsage(2, 2, InputTokens: 17), checkpoint.Usage);
            Assert.Equal(new[] { "accepted", "recovered" }, (await recoveredStore.ReadJournalAsync(new RunId("crash-run"), Ct)).Select(x => x.EventType));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task CancellationBeforeCreateDoesNotWriteAnything()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(Checkpoint(), cancellation.Token));
        Assert.Empty(await store.ListAsync(Ct));
    }

    private static WorkflowCheckpoint Checkpoint(string runId = "run", string session = "session") =>
        new(runId, runId, null, "task-" + runId, Request(session), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static AgentRunRequest Request(string session) => new(new SessionKey(session), "objective", "fake", "fake-model",
        new ToolSelectionPolicy(ToolProfile.Full), new CallerIdentity("owner", "Owner", true));

    private static WorkflowToolExecution Tool(string executionId, ToolResult? result = null) =>
        new(executionId, new ToolCall("call-" + executionId, "fake-tool", JsonSerializer.SerializeToElement(new { path = "a'b" })), result);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
