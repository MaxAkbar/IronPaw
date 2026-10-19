using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class RunOutcomeTests
{
    [TestMethod]
    public async Task RoundLimit_DoesNotReturnPreviousTurnOrEmitSuccess()
    {
        using var scope = new TestScope(new ScriptedClient([
            new ToolCallDelta("time-1", "current_time", JsonSerializer.SerializeToElement(new { })), new CompletedDelta()]));
        await scope.Transcripts.ReplaceAsync(scope.Key,
            [new ChatMessage(ChatRole.User, "old question"), new ChatMessage(ChatRole.Assistant, "old answer")], CancellationToken.None);
        var result = await scope.RunAsync(maxRounds: 1);
        Assert.AreEqual(AgentRunStatus.RoundLimitReached, result.Status);
        Assert.DoesNotContain("old answer", result.FinalAssistantMessage);
        Assert.Contains(evt => evt.Type == "run_round_limit_reached", scope.Sink.Events);
        Assert.DoesNotContain(evt => evt.Type == "run_completed", scope.Sink.Events);
        Assert.AreEqual(AgentRunStatus.RoundLimitReached, Assert.ContainsSingle(await scope.History.LoadAllAsync(CancellationToken.None)).Status);
        Assert.Contains(message => message.Role == ChatRole.Tool, await scope.Transcripts.LoadAsync(scope.Key, CancellationToken.None));
    }

    [TestMethod]
    public async Task ProviderFailure_RecordsHistoryAndInterruptedOutput()
    {
        using var scope = new TestScope(new ScriptedClient([new AssistantTextDelta("partial output"), new LlmErrorDelta("simulated failure")]));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scope.RunAsync());
        var summary = Assert.ContainsSingle(await scope.History.LoadAllAsync(CancellationToken.None));
        Assert.AreEqual(AgentRunStatus.Failed, summary.Status);
        Assert.IsNotNull(summary.Error);
        Assert.Contains("simulated failure", summary.Error);
        Assert.Contains(evt => evt.Type == "run_failed" && evt.RunId.Value == summary.RunId, scope.Sink.Events);
        Assert.DoesNotContain(evt => evt.Type == "run_completed", scope.Sink.Events);
        Assert.Contains(message => message.Content.Contains("partial output"), await scope.Transcripts.LoadAsync(scope.Key, CancellationToken.None));
    }

    [TestMethod]
    public async Task Cancellation_RecordsHistoryUsingIndependentToken()
    {
        using var cancellation = new CancellationTokenSource();
        using var scope = new TestScope(new ScriptedClient([new AssistantTextDelta("partial")], () => cancellation.Cancel()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => scope.RunAsync(ct: cancellation.Token));
        var summary = Assert.ContainsSingle(await scope.History.LoadAllAsync(CancellationToken.None));
        Assert.AreEqual(AgentRunStatus.Cancelled, summary.Status);
        Assert.Contains(evt => evt.Type == "run_cancelled", scope.Sink.Events);
        Assert.IsNotEmpty(await scope.Transcripts.LoadAsync(scope.Key, CancellationToken.None));
    }

    [TestMethod]
    public async Task QueuedCancellation_IsRecordedWithoutOverwritingActiveTranscript()
    {
        using var scope = new TestScope(new ScriptedClient([new AssistantTextDelta("done"), new CompletedDelta()]));
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var occupied = scope.Scheduler.EnqueueAsync(scope.Key, _ => release.Task, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var queued = scope.RunAsync(ct: cancellation.Token);
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
            Assert.AreEqual(AgentRunStatus.Cancelled, Assert.ContainsSingle(await scope.History.LoadAllAsync(CancellationToken.None)).Status);
            Assert.IsEmpty(await scope.Transcripts.LoadAsync(scope.Key, CancellationToken.None));
        }
        finally { release.SetResult(1); await occupied; }
        Assert.AreEqual(0, scope.Scheduler.ActiveLaneCount);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(6)]
    public async Task EmptyProviderOutput_IsRecordedAsFailure(int maxRounds)
    {
        using var scope = new TestScope(new ScriptedClient([new CompletedDelta()]));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scope.RunAsync(maxRounds: maxRounds));
        Assert.AreEqual(AgentRunStatus.Failed, Assert.ContainsSingle(await scope.History.LoadAllAsync(CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task SuccessfulRun_HasOneSuccessfulHistoryEntry()
    {
        using var scope = new TestScope(new ScriptedClient([new AssistantTextDelta("done"), new CompletedDelta()]));
        var result = await scope.RunAsync();
        Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
        Assert.AreEqual("done", result.FinalAssistantMessage);
        Assert.AreEqual(AgentRunStatus.Succeeded, Assert.ContainsSingle(await scope.History.LoadAllAsync(CancellationToken.None)).Status);
        Assert.ContainsSingle(evt => evt.Type == "run_completed", scope.Sink.Events);
    }

    [TestMethod]
    public async Task TerminalObserverFailure_DoesNotRewriteSuccessfulOutcome()
    {
        using var scope = new TestScope(new ScriptedClient([new AssistantTextDelta("done"), new CompletedDelta()]));
        scope.Sink.ThrowOnCompleted = true;
        var result = await scope.RunAsync();
        Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
        var deliveryError = result.Metadata["eventDeliveryError"]!.ToString();
        Assert.IsNotNull(deliveryError);
        Assert.Contains("observer failed", deliveryError);
        Assert.AreEqual(AgentRunStatus.Succeeded, Assert.ContainsSingle(await scope.History.LoadAllAsync(CancellationToken.None)).Status);
    }

    [TestMethod]
    [DataRow("run_started")]
    [DataRow("assistant_delta")]
    public async Task NonDurableIntermediateObserverFailureCannotReplaceTheActualOutcome(string eventType)
    {
        using var scope = new TestScope(new ScriptedClient([new AssistantTextDelta("done"), new CompletedDelta()]));
        scope.Sink.ThrowOnType = eventType;
        var result = await scope.RunAsync();
        Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
        Assert.AreEqual("done", result.FinalAssistantMessage);
        var deliveryError = result.Metadata["eventDeliveryError"]!.ToString();
        Assert.IsNotNull(deliveryError);
        Assert.Contains("observer failed", deliveryError);
        Assert.AreEqual(AgentRunStatus.Succeeded, Assert.ContainsSingle(await scope.History.LoadAllAsync(CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task InterruptedStream_DoesNotExecutePendingToolCalls()
    {
        using var scope = new TestScope(new ScriptedClient([
            new ToolCallDelta("time-1", "current_time", JsonSerializer.SerializeToElement(new { }))]));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scope.RunAsync());
        Assert.AreEqual(AgentRunStatus.Failed, Assert.ContainsSingle(await scope.History.LoadAllAsync(CancellationToken.None)).Status);
        Assert.DoesNotContain(evt => evt.Type == "tool_result", scope.Sink.Events);
    }

    [TestMethod]
    public async Task CorruptTranscript_IsPreservedWhenLoadingFails()
    {
        using var scope = new TestScope(new ScriptedClient([new AssistantTextDelta("done"), new CompletedDelta()]));
        var path = Path.Combine(scope.TranscriptsDirectory, "outcome-test.jsonl");
        await File.WriteAllTextAsync(path, "corrupt transcript");
        await Assert.ThrowsExactlyAsync<JsonException>(() => scope.RunAsync());
        Assert.AreEqual("corrupt transcript", await File.ReadAllTextAsync(path));
        Assert.AreEqual(AgentRunStatus.Failed, Assert.ContainsSingle(await scope.History.LoadAllAsync(CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task Clarification_IsAwaitingInputRatherThanCompleted()
    {
        using var scope = new TestScope(new ScriptedClient([
            new AssistantTextDelta("""{"needs_clarification":true,"clarification_questions":["Which project?"],"delegations":[]}"""),
            new CompletedDelta()]), includeAgents: true);
        var result = await scope.RunAsync();
        Assert.AreEqual(AgentRunStatus.AwaitingInput, result.Status);
        Assert.Contains("Which project?", result.FinalAssistantMessage);
        Assert.Contains(evt => evt.Type == "run_awaiting_input", scope.Sink.Events);
        Assert.DoesNotContain(evt => evt.Type == "run_completed", scope.Sink.Events);
        Assert.AreEqual(AgentRunStatus.AwaitingInput, Assert.ContainsSingle(await scope.History.LoadAllAsync(CancellationToken.None)).Status);
    }

    private sealed class TestScope : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        private readonly AgentOrchestrator _orchestrator;
        public SessionKey Key { get; } = new("outcome-test");
        public SessionLaneScheduler Scheduler { get; } = new();
        public string TranscriptsDirectory => Path.Combine(_root, "transcripts");
        public JsonTranscriptStore Transcripts { get; }
        public JsonRunHistoryStore History { get; }
        public CapturingSink Sink { get; } = new();

        public TestScope(ILlmClient client, bool includeAgents = false)
        {
            Directory.CreateDirectory(_root);
            Transcripts = new JsonTranscriptStore(TranscriptsDirectory);
            var tools = new InMemoryToolRegistry([new CurrentTimeTool()]);
            History = new JsonRunHistoryStore(Path.Combine(_root, "history.jsonl"));
            _orchestrator = new AgentOrchestrator(Scheduler,
                new FileSessionStore(new FileSessionStoreOptions(Path.Combine(_root, "sessions.json"), TimeSpan.Zero)),
                Transcripts, tools, new ToolExecutor(), new ToolSchemaNormalizer(),
                client, new TranscriptSanitizer(), new ToolResultTruncator(), new DefaultSystemPromptBuilder(), runHistoryStore: History);
            if (includeAgents)
            {
                var agents = new InMemoryAgentRegistry();
                agents.Register(new AgentDefinition("reviewer", "Reviewer", "Reviews code", "Review code",
                    new ToolSelectionPolicy(ToolProfile.Coding)));
                tools.RegisterTools([new ListAgentsTool(agents), new InvokeAgentTool(agents, _orchestrator)]);
            }
        }

        public Task<AgentRunResult> RunAsync(int maxRounds = 6, CancellationToken ct = default) =>
            _orchestrator.RunAsync(new AgentRunRequest(Key, "new question", "local", "test",
                new ToolSelectionPolicy(ToolProfile.Coding), CallerIdentity.LocalOwner(), MaxRounds: maxRounds), Sink, ct);

        public void Dispose()
        {
            Scheduler.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CapturingSink : IAgentEventSink
    {
        public List<AgentEvent> Events { get; } = [];
        public bool ThrowOnCompleted { get; set; }
        public string? ThrowOnType { get; set; }
        public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct)
        {
            if (evt.Type == ThrowOnType || ThrowOnCompleted && evt.Type == "run_completed") throw new InvalidOperationException("observer failed");
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedClient(IReadOnlyList<LlmDelta> deltas, Action? beforeStream = null) : ILlmClient
    {
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            beforeStream?.Invoke();
            foreach (var delta in deltas) { ct.ThrowIfCancellationRequested(); yield return delta; }
        }
    }
}
