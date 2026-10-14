using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

public sealed class WorkflowRecoveryTests
{
    [Fact]
    public async Task InterruptedTool_RequiresReconciliationBeforeResume_ThenReusesEstablishedResult()
    {
        using var cancellation = new CancellationTokenSource();
        var tool = new CountingTool((_, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ToolResult.Success("unreachable"));
        });
        using var scope = new TestScope(tool);
        var initialClient = new ScriptedClient(ToolResponse());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.CreateOrchestrator(initialClient).RunAsync(scope.Request, NullAgentEventSink.Instance, cancellation.Token));

        var checkpoint = Assert.Single(await scope.Store.ListAsync(CancellationToken.None));
        var execution = Assert.Single(checkpoint.ToolExecutions!);
        Assert.Null(execution.Result);
        Assert.False(string.IsNullOrWhiteSpace(execution.ExecutionId));
        Assert.Equal(execution.ExecutionId, Assert.Single(tool.Contexts).ToolExecutionId);
        Assert.Equal(1, checkpoint.Usage!.ToolCalls);

        var resumedClient = new ScriptedClient(FinalResponse());
        var resumed = scope.CreateOrchestrator(resumedClient);
        await Assert.ThrowsAsync<WorkflowReconciliationRequiredException>(() =>
            resumed.ResumeAsync(new RunId(checkpoint.RunId), scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
        Assert.Equal(0, resumedClient.CallCount);
        Assert.Equal(1, tool.CallCount);

        await resumed.ReconcileToolAsync(new RunId(checkpoint.RunId), execution.ExecutionId,
            ToolResult.Success("verified completed effect"), "Checked the destination after interruption",
            scope.Request.Caller, CancellationToken.None);
        var result = await resumed.ResumeAsync(new RunId(checkpoint.RunId), scope.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);

        Assert.Equal(checkpoint.RunId, result.RunId.Value);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(1, resumedClient.CallCount);
        Assert.Equal(1, tool.CallCount);
        Assert.Equal("verified completed effect", Assert.Single(result.Transcript, message => message.Role == ChatRole.Tool).Content);
        var saved = await scope.Store.GetAsync(result.RunId, CancellationToken.None);
        Assert.Equal(WorkflowStatus.Succeeded, saved!.Status);
        Assert.Equal("Checked the destination after interruption", Assert.Single(saved.ToolExecutions!).ReconciliationNote);
    }

    [Fact]
    public async Task FailedSecondModelCall_ReplaysCommittedResponseAndToolWithoutRepeatingThem()
    {
        var tool = new CountingTool();
        using var scope = new TestScope(tool);
        var initialClient = new ScriptedClient(ToolResponse(), FailedResponse());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.CreateOrchestrator(initialClient).RunAsync(scope.Request, NullAgentEventSink.Instance, CancellationToken.None));
        var interrupted = Assert.Single(await scope.Store.ListAsync(CancellationToken.None));
        Assert.Equal(2, initialClient.CallCount);
        Assert.Equal(1, tool.CallCount);
        Assert.NotNull(Assert.Single(interrupted.ToolExecutions!).Result);

        var resumedClient = new ScriptedClient(FinalResponse());
        var result = await scope.CreateOrchestrator(resumedClient).ResumeAsync(new RunId(interrupted.RunId),
            scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None);

        Assert.Equal(interrupted.RunId, result.RunId.Value);
        Assert.Equal("finished", result.FinalAssistantMessage);
        Assert.Equal(1, resumedClient.CallCount);
        Assert.Equal(1, tool.CallCount);
        Assert.Single(result.Transcript, message => message.Role == ChatRole.User);
        Assert.Single(result.Transcript, message => message.Role == ChatRole.Tool);
        Assert.Contains(resumedClient.Requests[0].Messages,
            message => message.Role == ChatRole.Tool && message.Content == "effect completed");
        var saved = await scope.Store.GetAsync(result.RunId, CancellationToken.None);
        Assert.Equal(3, saved!.Usage!.ModelCalls);
        Assert.Equal(1, saved.Usage.ToolCalls);
        Assert.Equal(WorkflowStatus.Succeeded, saved.Status);
    }

    [Fact]
    public async Task CompletedWorkflow_ResumeReturnsSavedResultWithoutCallingProviderOrTools()
    {
        var tool = new CountingTool();
        using var scope = new TestScope(tool);
        var completed = await scope.CreateOrchestrator(new ScriptedClient(ToolResponse(), FinalResponse()))
            .RunAsync(scope.Request, NullAgentEventSink.Instance, CancellationToken.None);
        var unusedClient = new ScriptedClient();
        var result = await scope.CreateOrchestrator(unusedClient).ResumeAsync(completed.RunId,
            scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None);

        Assert.Equal(completed.RunId, result.RunId);
        Assert.Equal(completed.FinalAssistantMessage, result.FinalAssistantMessage);
        Assert.Equal(completed.Transcript, result.Transcript);
        Assert.Equal(0, unusedClient.CallCount);
        Assert.Equal(1, tool.CallCount);
    }

    [Fact]
    public async Task HistoryProjectionFailure_DoesNotRewriteDurablyCommittedSuccess()
    {
        using var scope = new TestScope(new CountingTool());
        var result = await scope.CreateOrchestrator(new ScriptedClient(FinalResponse()), history: new ThrowingHistoryStore())
            .RunAsync(scope.Request, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal("history projection failed", result.Metadata["historyProjectionError"]);
        var checkpoint = await scope.Store.GetAsync(result.RunId, CancellationToken.None);
        Assert.Equal(WorkflowStatus.Succeeded, checkpoint!.Status);
        Assert.Equal(AgentRunStatus.Succeeded, checkpoint.Result!.Status);
        Assert.Null(checkpoint.Error);
    }

    [Theory]
    [InlineData(WorkflowStatus.BudgetExhausted)]
    [InlineData(WorkflowStatus.Failed)]
    public async Task TerminalWorkflowWithoutResult_CannotResumeOrOverwriteLaterConversation(WorkflowStatus status)
    {
        using var scope = new TestScope(new CountingTool());
        var now = DateTimeOffset.UtcNow;
        var id = RunId.New();
        var checkpoint = new WorkflowCheckpoint(id.Value, id.Value, null, "terminal-task", scope.Request, now, now,
            Status: status, Budget: new WorkflowBudget(1, 1), Usage: new WorkflowUsage(ModelCalls: 1));
        await scope.Store.CreateAsync(checkpoint, CancellationToken.None);
        ChatMessage[] laterConversation = [new(ChatRole.User, "later question"), new(ChatRole.Assistant, "later answer")];
        await scope.Transcripts.ReplaceAsync(scope.Request.SessionKey, laterConversation, CancellationToken.None);
        var client = new ScriptedClient(FinalResponse());
        var orchestrator = scope.CreateOrchestrator(client);
        if (status == WorkflowStatus.BudgetExhausted)
            await Assert.ThrowsAsync<WorkflowBudgetExceededException>(() => orchestrator.ResumeAsync(id,
                scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ResumeAsync(id,
                scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
        Assert.Equal(0, client.CallCount);
        Assert.Equal(laterConversation, await scope.Transcripts.LoadAsync(scope.Request.SessionKey, CancellationToken.None));
        var saved = await scope.Store.GetAsync(id, CancellationToken.None);
        Assert.Equal(status, saved!.Status);
        Assert.Equal(0, saved.Revision);
        Assert.Null(saved.Result);
    }

    [Theory]
    [InlineData("different-caller", false)]
    [InlineData("workflow-owner", true)]
    public async Task Resume_RejectsChangedCallerEvenForCompletedWorkflow(string callerId, bool isOwner)
    {
        using var scope = new TestScope(new CountingTool());
        var completed = await scope.CreateOrchestrator(new ScriptedClient(FinalResponse()))
            .RunAsync(scope.Request, NullAgentEventSink.Instance, CancellationToken.None);
        var client = new ScriptedClient();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.CreateOrchestrator(client).ResumeAsync(
            completed.RunId, new CallerIdentity(callerId, "Changed caller", isOwner),
            NullAgentEventSink.Instance, CancellationToken.None));
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task ConcurrentResume_OnlyOneHostCanAcquireWorkflow()
    {
        using var scope = new TestScope(new CountingTool());
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CreateOrchestrator(new ScriptedClient(FailedResponse()))
            .RunAsync(scope.Request, NullAgentEventSink.Instance, CancellationToken.None));
        var checkpoint = Assert.Single(await scope.Store.ListAsync(CancellationToken.None));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeClient = new BlockingClient(entered, release);
        var active = scope.CreateOrchestrator(activeClient).ResumeAsync(new RunId(checkpoint.RunId), scope.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var duplicateClient = new ScriptedClient(FinalResponse());
        try
        {
            await Assert.ThrowsAsync<WorkflowConflictException>(() => scope.CreateOrchestrator(duplicateClient)
                .ResumeAsync(new RunId(checkpoint.RunId), scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
            Assert.Equal(0, duplicateClient.CallCount);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(AgentRunStatus.Succeeded, (await active).Status);
    }

    [Fact]
    public async Task ModelBudget_RemainsConsumedWhenAnAttemptFailsAndWorkflowResumes()
    {
        using var scope = new TestScope(new CountingTool());
        var request = scope.Request with { Budget = new WorkflowBudget(MaxModelCalls: 1) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CreateOrchestrator(new ScriptedClient(FailedResponse()))
            .RunAsync(request, NullAgentEventSink.Instance, CancellationToken.None));
        var checkpoint = Assert.Single(await scope.Store.ListAsync(CancellationToken.None));
        var client = new ScriptedClient(FinalResponse());
        Assert.Equal(AgentRunStatus.BudgetExhausted, (await scope.CreateOrchestrator(client).ResumeAsync(
            new RunId(checkpoint.RunId), request.Caller, NullAgentEventSink.Instance, CancellationToken.None)).Status);
        Assert.Equal(0, client.CallCount);
        var saved = await scope.Store.GetAsync(new RunId(checkpoint.RunId), CancellationToken.None);
        Assert.Equal(1, saved!.Usage!.ModelCalls);
        Assert.Equal(WorkflowStatus.BudgetExhausted, saved.Status);
    }

    [Fact]
    public async Task UsageLedger_CountsFailedAttemptsAndDoesNotDoubleCountReplayedResponses()
    {
        using var scope = new TestScope(new CountingTool());
        LlmDelta[] firstResponse = [new UsageDelta(10, 2, 1, 0), .. ToolResponse()];
        LlmDelta[] failedResponse = [new UsageDelta(3, 1, 0, 2), .. FailedResponse()];
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.CreateOrchestrator(new ScriptedClient(firstResponse, failedResponse))
                .RunAsync(scope.Request, NullAgentEventSink.Instance, CancellationToken.None));
        var checkpoint = Assert.Single(await scope.Store.ListAsync(CancellationToken.None));
        Assert.Equal(13, checkpoint.Usage!.InputTokens);
        LlmDelta[] finalResponse = [new UsageDelta(5, 4, 1, 0), .. FinalResponse()];
        var result = await scope.CreateOrchestrator(new ScriptedClient(finalResponse))
            .ResumeAsync(new RunId(checkpoint.RunId), scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None);
        var saved = await scope.Store.GetAsync(result.RunId, CancellationToken.None);
        Assert.Equal(18, saved!.Usage!.InputTokens);
        Assert.Equal(7, saved.Usage.OutputTokens);
        Assert.Equal(2, saved.Usage.CacheReadTokens);
        Assert.Equal(2, saved.Usage.CacheWriteTokens);
        Assert.Equal(3, saved.Usage.ModelCalls);
        Assert.Equal(1, saved.Usage.InterruptedModelCalls);
    }

    [Fact]
    public async Task ToolBudget_RemainsConsumedWhenCompletedToolIsReplayedAfterResume()
    {
        var tool = new CountingTool();
        using var scope = new TestScope(tool);
        var request = scope.Request with { Budget = new WorkflowBudget(MaxToolCalls: 1) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CreateOrchestrator(new ScriptedClient(ToolResponse(), FailedResponse()))
            .RunAsync(request, NullAgentEventSink.Instance, CancellationToken.None));
        var checkpoint = Assert.Single(await scope.Store.ListAsync(CancellationToken.None));
        var client = new ScriptedClient(ToolResponse("second-call"));
        Assert.Equal(AgentRunStatus.BudgetExhausted, (await scope.CreateOrchestrator(client).ResumeAsync(
            new RunId(checkpoint.RunId), request.Caller, NullAgentEventSink.Instance, CancellationToken.None)).Status);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(1, tool.CallCount);
        var saved = await scope.Store.GetAsync(new RunId(checkpoint.RunId), CancellationToken.None);
        Assert.Equal(1, saved!.Usage!.ToolCalls);
        Assert.Equal(WorkflowStatus.BudgetExhausted, saved.Status);
    }

    [Fact]
    public async Task ChangedToolSchema_RejectsReplayBeforeCallingProviderOrExecutingTool()
    {
        var originalTool = new CountingTool();
        using var scope = new TestScope(originalTool);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.CreateOrchestrator(new ScriptedClient(ToolResponse(), FailedResponse()))
                .RunAsync(scope.Request, NullAgentEventSink.Instance, CancellationToken.None));
        var checkpoint = Assert.Single(await scope.Store.ListAsync(CancellationToken.None));
        var changedTool = new CountingTool(schema: """{"type":"object","properties":{"new_parameter":{"type":"string"}}}""");
        var client = new ScriptedClient(FinalResponse());
        var error = await Assert.ThrowsAsync<WorkflowConflictException>(() => scope.CreateOrchestrator(client, changedTool)
            .ResumeAsync(new RunId(checkpoint.RunId), scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
        Assert.Contains("schemas", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.CallCount);
        Assert.Equal(0, changedTool.CallCount);
        Assert.Equal(1, originalTool.CallCount);
    }

    [Fact]
    public async Task Resume_RetainsOriginalDynamicPromptForInterruptedModelAttempt()
    {
        using var scope = new TestScope(new CountingTool());
        var request = scope.Request with { SystemPromptOverride = null };
        var initialClient = new ScriptedClient(FailedResponse());
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CreateOrchestrator(initialClient,
            promptBuilder: new FixedPromptBuilder("original runtime clock and memory snapshot"))
            .RunAsync(request, NullAgentEventSink.Instance, CancellationToken.None));
        var checkpoint = Assert.Single(await scope.Store.ListAsync(CancellationToken.None));
        var resumedClient = new ScriptedClient(FinalResponse());
        var result = await scope.CreateOrchestrator(resumedClient,
            promptBuilder: new FixedPromptBuilder("changed runtime clock and memory snapshot"))
            .ResumeAsync(new RunId(checkpoint.RunId), request.Caller, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(initialClient.Requests[0].SystemPrompt, resumedClient.Requests[0].SystemPrompt);
    }

    [Fact]
    public async Task QueuedCancellation_CanResumeWithoutAddingUserInputTwice()
    {
        using var scope = new TestScope(new CountingTool());
        var scheduler = new SessionLaneScheduler();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var occupied = scheduler.EnqueueAsync(scope.Request.SessionKey, async _ => { await release.Task; return 0; }, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var initialClient = new ScriptedClient(FinalResponse());
        var queued = scope.CreateOrchestrator(initialClient, scheduler: scheduler)
            .RunAsync(scope.Request, NullAgentEventSink.Instance, cancellation.Token);
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Equal(0, initialClient.CallCount);
            var checkpoint = Assert.Single(await scope.Store.ListAsync(CancellationToken.None));
            var result = await scope.CreateOrchestrator(new ScriptedClient(FinalResponse()))
                .ResumeAsync(new RunId(checkpoint.RunId), scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None);
            Assert.Single(result.Transcript, message => message.Role == ChatRole.User);
            Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        }
        finally { release.TrySetResult(); await occupied; }
    }

    private static LlmDelta[] ToolResponse(string callId = "effect-call") =>
        [new ToolCallDelta(callId, "record_effect", JsonSerializer.SerializeToElement(new { })), new CompletedDelta()];

    private static LlmDelta[] FinalResponse() => [new AssistantTextDelta("finished"), new CompletedDelta()];

    private static LlmDelta[] FailedResponse() => [new AssistantTextDelta("interrupted output"), new LlmErrorDelta("simulated provider interruption")];

    private sealed class TestScope : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        private readonly CountingTool _tool;
        private readonly List<SessionLaneScheduler> _schedulers = [];
        public OptimisticWorkflowStore Store { get; } = new();
        public JsonTranscriptStore Transcripts => new(Path.Combine(_root, "transcripts"));
        public AgentRunRequest Request { get; } = new(new SessionKey("workflow-recovery"), "perform the action", "local", "test",
            new ToolSelectionPolicy(ToolProfile.Coding), CallerIdentity.Authenticated("workflow-owner", "Workflow owner"),
            SystemPromptOverride: "A fixed recovery test prompt");

        public TestScope(CountingTool tool) { _tool = tool; Directory.CreateDirectory(_root); }

        public IResumableAgentOrchestrator CreateOrchestrator(ILlmClient client, CountingTool? tool = null,
            SessionLaneScheduler? scheduler = null, ISystemPromptBuilder? promptBuilder = null, IRunHistoryStore? history = null)
        {
            scheduler ??= new SessionLaneScheduler();
            _schedulers.Add(scheduler);
            return new AgentOrchestrator(scheduler,
                new FileSessionStore(new FileSessionStoreOptions(Path.Combine(_root, "sessions.json"), TimeSpan.Zero)),
                new JsonTranscriptStore(Path.Combine(_root, "transcripts")), new InMemoryToolRegistry([tool ?? _tool]),
                new ToolExecutor(), new ToolSchemaNormalizer(), client, new TranscriptSanitizer(),
                new ToolResultTruncator(), promptBuilder ?? new DefaultSystemPromptBuilder(), runHistoryStore: history, workflowStore: Store);
        }

        public void Dispose()
        {
            foreach (var scheduler in _schedulers) scheduler.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedPromptBuilder(string prompt) : ISystemPromptBuilder
    {
        public string BuildPrompt(AgentRunRequest request, IReadOnlyList<ITool> tools) => prompt;
    }

    private sealed class ThrowingHistoryStore : IRunHistoryStore
    {
        public Task RecordAsync(RunSummary summary, CancellationToken ct) => throw new IOException("history projection failed");
        public Task<IReadOnlyList<RunSummary>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RunSummary>>([]);
        public Task<IReadOnlyList<RunSummary>> LoadBySessionAsync(SessionKey sessionKey, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RunSummary>>([]);
        public Task<RunSummary?> GetAsync(string runId, CancellationToken ct) => Task.FromResult<RunSummary?>(null);
    }

    private sealed class CountingTool : ITool
    {
        private readonly Func<ToolExecutionContext, CancellationToken, Task<ToolResult>>? _action;
        public int CallCount { get; private set; }
        public List<ToolExecutionContext> Contexts { get; } = [];
        public ToolDescriptor Descriptor { get; }

        public CountingTool(Func<ToolExecutionContext, CancellationToken, Task<ToolResult>>? action = null,
            string schema = """{"type":"object","properties":{}}""")
        {
            _action = action;
            Descriptor = new ToolDescriptor("record_effect", "Records an externally visible effect", JsonSerializer.Deserialize<JsonElement>(schema), "group:system");
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
        {
            CallCount++;
            Contexts.Add(context);
            return _action is null ? ToolResult.Success("effect completed") : await _action(context, ct);
        }
    }

    private sealed class ScriptedClient(params LlmDelta[][] responses) : ILlmClient
    {
        public int CallCount { get; private set; }
        public List<LlmRequest> Requests { get; } = [];
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var index = CallCount++;
            Requests.Add(request);
            if (index >= responses.Length) throw new InvalidOperationException("Unexpected provider call during replay");
            foreach (var delta in responses[index]) { ct.ThrowIfCancellationRequested(); yield return delta; }
        }
    }

    private sealed class BlockingClient(TaskCompletionSource entered, TaskCompletionSource release) : ILlmClient
    {
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return new AssistantTextDelta("finished");
            yield return new CompletedDelta();
        }
    }

    private sealed class OptimisticWorkflowStore : IWorkflowStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, WorkflowCheckpoint> _checkpoints = [];
        private readonly List<WorkflowJournalEntry> _journal = [];

        public Task CreateAsync(WorkflowCheckpoint checkpoint, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_checkpoints.TryAdd(checkpoint.RunId, checkpoint)) throw new WorkflowConflictException("Workflow already exists");
                _journal.Add(new WorkflowJournalEntry(checkpoint.RunId, checkpoint.Revision, checkpoint.UpdatedAt, "accepted"));
            }
            return Task.CompletedTask;
        }

        public Task<WorkflowCheckpoint?> GetAsync(RunId runId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate) return Task.FromResult(_checkpoints.GetValueOrDefault(runId.Value));
        }

        public Task<IReadOnlyList<WorkflowCheckpoint>> ListAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate) return Task.FromResult<IReadOnlyList<WorkflowCheckpoint>>(_checkpoints.Values.ToArray());
        }

        public Task<WorkflowCheckpoint> SaveAsync(WorkflowCheckpoint checkpoint, int expectedRevision, string eventType, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_checkpoints.TryGetValue(checkpoint.RunId, out var current) || current.Revision != expectedRevision)
                    throw new WorkflowConflictException("Checkpoint revision changed");
                var saved = checkpoint with { Revision = expectedRevision + 1, UpdatedAt = DateTimeOffset.UtcNow };
                _checkpoints[checkpoint.RunId] = saved;
                _journal.Add(new WorkflowJournalEntry(saved.RunId, saved.Revision, saved.UpdatedAt, eventType));
                return Task.FromResult(saved);
            }
        }

        public Task<IReadOnlyList<WorkflowJournalEntry>> ReadJournalAsync(RunId runId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate) return Task.FromResult<IReadOnlyList<WorkflowJournalEntry>>(_journal.Where(entry => entry.RunId == runId.Value).ToArray());
        }
    }
}
