using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class WorkflowBudgetTests
{
    [TestMethod]
    public async Task InputThatCannotFit_StopsBeforeProviderAndReturnsDurablePartialResult()
    {
        await using var scope = await Scope.OpenAsync();
        var client = new ScriptedClient(Final(10));
        var result = await scope.Runtime(client).RunAsync(scope.Request with { Budget = new(MaxTotalTokens: 1) },
            NullAgentEventSink.Instance, CancellationToken.None);
        Assert.AreEqual(AgentRunStatus.BudgetExhausted, result.Status);
        Assert.IsEmpty(client.Requests);
        var checkpoint = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        Assert.AreEqual(WorkflowStatus.BudgetExhausted, checkpoint.Status);
        Assert.AreEqual(result.Status, checkpoint.Result!.Status);
        Assert.AreEqual(result.FinalAssistantMessage, checkpoint.Result.FinalAssistantMessage);
        Assert.IsNull(checkpoint.LeaseOwner);
    }

    [TestMethod]
    public async Task BoundedOutputAndReportedUsage_SettleReservation_CompletedReplayCostsNothing()
    {
        await using var scope = await Scope.OpenAsync();
        var client = new ScriptedClient(Final(10));
        var result = await scope.Runtime(client).RunAsync(scope.Request with { Budget = new(MaxTotalTokens: 2048) },
            NullAgentEventSink.Instance, CancellationToken.None);
        Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
        var request = Assert.ContainsSingle(client.Requests);
        Assert.IsInRange(1, 2047, request.MaxOutputTokens);
        Assert.AreEqual(0, request.MaxAutoContinuationPasses);
        var before = await ((IWorkflowStore)scope.Store).GetBudgetAsync(result.RunId, CancellationToken.None);
        Assert.AreEqual(10, before.ChargedTokens);
        Assert.AreEqual(0, before.ReservedTokens);
        await scope.ReopenAsync();
        var unused = new ScriptedClient();
        Assert.AreEqual(AgentRunStatus.Succeeded, (await scope.Runtime(unused).ResumeAsync(result.RunId, scope.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None)).Status);
        Assert.IsEmpty(unused.Requests);
        Assert.AreEqual(before.ChargedTokens, (await ((IWorkflowStore)scope.Store).GetBudgetAsync(result.RunId, CancellationToken.None)).ChargedTokens);
    }

    [TestMethod]
    public async Task InterruptedAttemptWithoutReceipt_RemainsChargedAcrossReopenAndCannotBeRetriedForFree()
    {
        await using var scope = await Scope.OpenAsync();
        var request = scope.Request with { Budget = new(MaxTotalTokens: 2048) };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scope.Runtime(new ScriptedClient(
            [new AssistantTextDelta("partial"), new LlmErrorDelta("connection lost")])).RunAsync(request,
            NullAgentEventSink.Instance, CancellationToken.None));
        var checkpoint = Assert.ContainsSingle(await scope.Store.ListAsync(CancellationToken.None));
        var attempt = Assert.ContainsSingle(Assert.ContainsSingle(checkpoint.ModelCalls!).Attempts!);
        Assert.AreEqual(WorkflowReservationState.Unknown, attempt.State);
        Assert.AreEqual(2048, attempt.ReservedTokens);
        await scope.ReopenAsync();
        var unused = new ScriptedClient(Final(10));
        var result = await scope.Runtime(unused).ResumeAsync(new RunId(checkpoint.RunId), request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);
        Assert.AreEqual(AgentRunStatus.BudgetExhausted, result.Status);
        Assert.IsEmpty(unused.Requests);
        Assert.AreEqual(2048, (await ((IWorkflowStore)scope.Store).GetBudgetAsync(result.RunId, CancellationToken.None)).ChargedTokens);
    }

    [TestMethod]
    public async Task MissingUsageOnCompletedResponse_KeepsReservationCharged()
    {
        await using var scope = await Scope.OpenAsync();
        var result = await scope.Runtime(new ScriptedClient([new AssistantTextDelta("finished"), new CompletedDelta()]))
            .RunAsync(scope.Request with { Budget = new(MaxTotalTokens: 2048) }, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
        var budget = await ((IWorkflowStore)scope.Store).GetBudgetAsync(result.RunId, CancellationToken.None);
        Assert.AreEqual(2048, budget.ChargedTokens);
        Assert.AreEqual(0, budget.ReservedTokens);
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow(10L, null)]
    [DataRow(null, 10L)]
    public async Task EmptyOrPartialUsageReceipt_CannotRefundUnreportedTokens(long? input, long? output)
    {
        await using var scope = await Scope.OpenAsync();
        var result = await scope.Runtime(new ScriptedClient([new UsageDelta(input, output, null, null),
            new AssistantTextDelta("finished"), new CompletedDelta()])).RunAsync(scope.Request with
            {
                Budget = new(MaxTotalTokens: 2048)
            }, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
        Assert.AreEqual(2048, (await ((IWorkflowStore)scope.Store).GetBudgetAsync(result.RunId, CancellationToken.None)).ChargedTokens);
        var checkpoint = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        Assert.IsFalse(Assert.ContainsSingle(Assert.ContainsSingle(checkpoint.ModelCalls!).Attempts!).UsageComplete);
    }

    [TestMethod]
    public async Task SplitInputAndOutputReceipts_CanSettleCompleteUsage()
    {
        await using var scope = await Scope.OpenAsync();
        var result = await scope.Runtime(new ScriptedClient([new UsageDelta(10, null, null, null),
            new UsageDelta(null, 5, null, null), new AssistantTextDelta("finished"), new CompletedDelta()]))
            .RunAsync(scope.Request with { Budget = new(MaxTotalTokens: 2048) }, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.AreEqual(15, (await ((IWorkflowStore)scope.Store).GetBudgetAsync(result.RunId, CancellationToken.None)).ChargedTokens);
    }

    [TestMethod]
    public async Task ProviderReceiptBeyondReservation_IsPreservedAndStopsBeforeToolEffect()
    {
        await using var scope = await Scope.OpenAsync();
        var effect = new EffectTool();
        var client = new ScriptedClient([new UsageDelta(3000, 10, 0, 0),
            new ToolCallDelta("effect", "effect", JsonSerializer.SerializeToElement(new { })), new CompletedDelta()]);
        var result = await scope.Runtime(client, effect).RunAsync(scope.Request with { Budget = new(MaxTotalTokens: 2048) },
            NullAgentEventSink.Instance, CancellationToken.None);
        Assert.AreEqual(AgentRunStatus.BudgetExhausted, result.Status);
        Assert.AreEqual(0, effect.Calls);
        var checkpoint = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        Assert.AreEqual(3000, checkpoint.Usage!.InputTokens);
        Assert.IsNotNull(Assert.ContainsSingle(checkpoint.ModelCalls!).Response);
        Assert.AreEqual(3010, (await ((IWorkflowStore)scope.Store).GetBudgetAsync(result.RunId, CancellationToken.None)).ChargedTokens);
    }

    [TestMethod]
    public async Task NegativeProviderUsage_CannotReduceSharedLedger()
    {
        await using var scope = await Scope.OpenAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scope.Runtime(new ScriptedClient(
            [new UsageDelta(-1, 0, 0, 0), new CompletedDelta()])).RunAsync(scope.Request,
            NullAgentEventSink.Instance, CancellationToken.None));
        var checkpoint = Assert.ContainsSingle(await scope.Store.ListAsync(CancellationToken.None));
        Assert.AreEqual(0, checkpoint.Usage!.InputTokens);
        Assert.AreEqual(WorkflowReservationState.Unknown, Assert.ContainsSingle(Assert.ContainsSingle(checkpoint.ModelCalls!).Attempts!).State);
    }

    [TestMethod]
    public async Task ExecutionDeadline_CancelsProviderAndPreservesPartialOutput()
    {
        await using var scope = await Scope.OpenAsync();
        var client = new BlockingClient();
        var result = await scope.Runtime(client).RunAsync(scope.Request with
        {
            Budget = new(MaxExecutionTime: TimeSpan.FromMilliseconds(300))
        }, NullAgentEventSink.Instance, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(AgentRunStatus.BudgetExhausted, result.Status);
        Assert.IsTrue(client.Cancelled);
        Assert.Contains(message => message.Content.Contains("partial", StringComparison.Ordinal), result.Transcript);
        Assert.IsInRange(1, 300, client.Request!.Timeout.TotalMilliseconds);
        var checkpoint = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        Assert.AreEqual(WorkflowStatus.BudgetExhausted, checkpoint.Status);
        Assert.IsNull(checkpoint.LeaseOwner);
    }

    [TestMethod]
    public async Task TimeExhaustionDuringTool_RequiresReconciliationAndNeverRepeatsEffect()
    {
        await using var scope = await Scope.OpenAsync();
        var effect = new EffectTool(block: true);
        var runtime = scope.Runtime(new ScriptedClient([new ToolCallDelta("effect", "effect",
            JsonSerializer.SerializeToElement(new { })), new CompletedDelta()]), effect);
        var result = await runtime.RunAsync(scope.Request with { Budget = new(MaxExecutionTime: TimeSpan.FromMilliseconds(300)) },
            NullAgentEventSink.Instance, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(AgentRunStatus.BudgetExhausted, result.Status);
        var checkpoint = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        Assert.AreEqual(WorkflowStatus.NeedsReconciliation, checkpoint.Status);
        Assert.IsTrue(WorkflowIdentity.HasUnknownEffects(checkpoint));
        Assert.AreEqual(1, effect.Calls);
        Assert.AreEqual(AgentRunStatus.BudgetExhausted, (await runtime.ResumeAsync(result.RunId, scope.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None)).Status);
        Assert.AreEqual(1, effect.Calls);
    }

    [TestMethod]
    public async Task HumanWaitAndRestart_DoNotConsumeCleanlySettledExecutionTime()
    {
        var clock = new ManualClock();
        await using var scope = await Scope.OpenAsync(clock);
        var human = new HumanTool();
        var client = new AdvancingClient(clock, humanWait: true);
        var request = scope.Request with { Budget = new(MaxExecutionTime: TimeSpan.FromSeconds(3)) };
        var waiting = await scope.Runtime(client, human).RunAsync(request, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.AreEqual(AgentRunStatus.AwaitingInput, waiting.Status);
        var checkpoint = (await scope.Store.GetAsync(waiting.RunId, CancellationToken.None))!;
        Assert.AreEqual(TimeSpan.FromSeconds(1), (await ((IWorkflowStore)scope.Store).GetBudgetAsync(waiting.RunId, CancellationToken.None)).ExecutionTime);
        clock.Advance(TimeSpan.FromDays(2));
        await scope.ReopenAsync();
        var runtime = scope.Runtime(new AdvancingClient(clock, humanWait: false), human);
        await runtime.AnswerAsync(waiting.RunId, Assert.ContainsSingle(checkpoint.HumanRequests!).RequestId, "verified target",
            request.Caller, CancellationToken.None);
        var result = await runtime.ResumeAsync(waiting.RunId, request.Caller, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
        Assert.AreEqual(TimeSpan.FromSeconds(2), (await ((IWorkflowStore)scope.Store).GetBudgetAsync(waiting.RunId, CancellationToken.None)).ExecutionTime);
    }

    [TestMethod]
    public async Task ChildOvershoot_ChargesRootAndStopsParentBeforeAnotherProviderCall()
    {
        await using var scope = await Scope.OpenAsync();
        var client = new ScriptedClient(
            [new UsageDelta(100, 10, 0, 0), new ToolCallDelta("delegate", "invoke_agent",
                JsonSerializer.SerializeToElement(new { agent_id = "child", task = "child task" })), new CompletedDelta()],
            [new UsageDelta(9000, 10, 0, 0), new AssistantTextDelta("child partial"), new CompletedDelta()]);
        var registry = new InMemoryToolRegistry([]);
        var runtime = scope.Runtime(client, registry);
        var agents = new InMemoryAgentRegistry();
        agents.Register(new AgentDefinition("child", "Child", "Child task", "fixed", new ToolSelectionPolicy(ToolProfile.Full),
            MaxOutputTokens: 1024));
        registry.RegisterTools([new InvokeAgentTool(agents, runtime)]);
        var result = await runtime.RunAsync(scope.Request with { Budget = new(MaxTotalTokens: 8192) },
            NullAgentEventSink.Instance, CancellationToken.None);
        Assert.AreEqual(AgentRunStatus.BudgetExhausted, result.Status);
        Assert.AreEqual(2, client.Requests.Count);
        var checkpoints = await scope.Store.ListAsync(CancellationToken.None);
        Assert.AreEqual(2, checkpoints.Count);
        foreach (var checkpoint in checkpoints)
        {
            Assert.AreEqual(result.RunId.Value, checkpoint.RootRunId);
        }
        var budget = await ((IWorkflowStore)scope.Store).GetBudgetAsync(result.RunId, CancellationToken.None);
        Assert.AreEqual(9120, budget.ChargedTokens);
        Assert.AreEqual(0, budget.ReservedTokens);
        Assert.AreEqual(WorkflowToolState.Completed, Assert.ContainsSingle(Assert.ContainsSingle(checkpoint => checkpoint.ParentRunId is null, checkpoints).ToolExecutions!).State);
    }

    private static LlmDelta[] Final(long tokens) => [new UsageDelta(tokens, 0, 0, 0), new AssistantTextDelta("finished"), new CompletedDelta()];

    private sealed class Scope : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "IronPawBudgetTests", Guid.NewGuid().ToString("n"));
        private readonly List<SessionLaneScheduler> _schedulers = [];
        private TimeProvider _clock = TimeProvider.System;
        public CSharpDbWorkflowStore Store { get; private set; } = null!;
        public AgentRunRequest Request { get; } = new(new SessionKey("budget"), "perform task", "local", "test",
            new ToolSelectionPolicy(ToolProfile.Full), CallerIdentity.LocalOwner(), SystemPromptOverride: "fixed");
        public static async Task<Scope> OpenAsync(TimeProvider? clock = null)
        {
            var scope = new Scope();
            scope._clock = clock ?? TimeProvider.System;
            Directory.CreateDirectory(scope._path);
            scope.Store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(scope._path, "workflows.cdb"), timeProvider: scope._clock);
            return scope;
        }
        public async Task ReopenAsync()
        {
            await Store.DisposeAsync();
            Store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(_path, "workflows.cdb"), timeProvider: _clock);
        }
        public IResumableAgentOrchestrator Runtime(ILlmClient client, params ITool[] tools)
            => Runtime(client, new InMemoryToolRegistry(tools));
        public IResumableAgentOrchestrator Runtime(ILlmClient client, InMemoryToolRegistry registry)
        {
            var scheduler = new SessionLaneScheduler();
            _schedulers.Add(scheduler);
            return new AgentOrchestrator(scheduler,
                new FileSessionStore(new FileSessionStoreOptions(Path.Combine(_path, "sessions.json"), TimeSpan.Zero)),
                new JsonTranscriptStore(Path.Combine(_path, "transcripts")), registry,
                new ToolExecutor(), new ToolSchemaNormalizer(), client, new TranscriptSanitizer(),
                new ToolResultTruncator(), new DefaultSystemPromptBuilder(), workflowStore: Store);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var scheduler in _schedulers) scheduler.Dispose();
            await Store.DisposeAsync();
            Directory.Delete(_path, true);
        }
    }
    private sealed class ScriptedClient(params LlmDelta[][] responses) : ILlmClient
    {
        public List<LlmRequest> Requests { get; } = [];
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Requests.Add(request);
            foreach (var delta in responses[Requests.Count - 1]) { ct.ThrowIfCancellationRequested(); yield return delta; }
        }
    }
    private sealed class BlockingClient : ILlmClient
    {
        public bool Cancelled { get; private set; }
        public LlmRequest? Request { get; private set; }
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Request = request;
            yield return new AssistantTextDelta("partial");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { Cancelled = ct.IsCancellationRequested; }
        }
    }
    private sealed class EffectTool(bool block = false) : ITool
    {
        public int Calls { get; private set; }
        public ToolDescriptor Descriptor { get; } = new("effect", "effect", JsonSerializer.SerializeToElement(new { type = "object" }), "group:system");
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
        {
            Calls++;
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ToolResult.Success("receipt");
        }
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }
    private sealed class AdvancingClient(ManualClock clock, bool humanWait) : ILlmClient
    {
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            clock.Advance(TimeSpan.FromSeconds(1));
            if (humanWait) yield return new ToolCallDelta("question", "question", JsonSerializer.SerializeToElement(new { }));
            else yield return new AssistantTextDelta("finished");
            yield return new CompletedDelta();
        }
    }
    private sealed class HumanTool : IHumanInputTool
    {
        public ToolDescriptor Descriptor { get; } = new("question", "question", JsonSerializer.SerializeToElement(new { type = "object" }), "group:system");
        public string GetQuestion(ToolCall call) => "Which target?";
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct) =>
            throw new InvalidOperationException("A human wait cannot execute.");
    }
}
