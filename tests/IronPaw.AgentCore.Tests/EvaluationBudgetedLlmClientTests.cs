using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class EvaluationBudgetedLlmClientTests
{
    [TestMethod]
    public async Task ReservesBeforeDispatchAndPersistsFullResponseBeforeDelivery()
    {
        var settings = Settings();
        var store = new Store(settings);
        var inner = new Client([new AssistantTextDelta("answer"), new UsageDelta(20, 4, null, null), new CompletedDelta()]);
        inner.OnStart = () => Assert.AreEqual(LiveEvaluationRequestState.Started, Assert.ContainsSingle(store.Checkpoint.Requests).State);
        var original = Request();
        var wrapper = Wrapper(inner, store, settings);

        await foreach (var delta in wrapper.StreamAsync(original, default))
            Assert.AreEqual(LiveEvaluationRequestState.Completed, Assert.ContainsSingle(store.Checkpoint.Requests).State);

        var record = Assert.ContainsSingle(store.Checkpoint.Requests);
        Assert.AreEqual(0.0237m, record.Request.ReservedSpend);
        Assert.AreEqual(0.000033m, record.Receipt!.ActualSpend); // Inclusive OpenAI input conservatively uses maximum input rate.
        Assert.AreEqual(0, inner.LastRequest!.MaxAutoContinuationPasses);
        Assert.AreEqual(3, original.MaxAutoContinuationPasses);
        Assert.AreEqual(JsonSerializer.Serialize(original.Messages), JsonSerializer.Serialize(inner.LastRequest.Messages));
        Assert.AreEqual(JsonSerializer.Serialize(original.Tools), JsonSerializer.Serialize(inner.LastRequest.Tools));
        Assert.Contains("inclusive-input-at-maximum", record.Receipt.EvidenceJson);
    }

    [TestMethod]
    public async Task CompletedResponseReplaysAfterRestartWithoutPaidRedispatch()
    {
        var settings = Settings(); var store = new Store(settings);
        using var args = JsonDocument.Parse("{\"path\":\"x.txt\"}");
        var inner = new Client([new AssistantTextDelta("answer"), new ToolCallDelta("id", "read_file", args.RootElement),
            new UsageDelta(20, 4, null, null), new CompletedDelta()]);
        var first = await Read(Wrapper(inner, store, settings), Request());
        var second = await Read(Wrapper(inner, store, settings), Request());

        Assert.AreEqual(JsonSerializer.Serialize(first.Select(Describe)), JsonSerializer.Serialize(second.Select(Describe)));
        Assert.AreEqual(1, inner.Calls);
        Assert.ContainsSingle(store.Checkpoint.Requests);
    }

    [TestMethod]
    public async Task AnthropicChargesDisjointInputAndCacheCategories()
    {
        var settings = Settings() with { Provider = "anthropic" }; var store = new Store(settings);
        var inner = new Client([new UsageDelta(10, 4, 20, 30), new CompletedDelta()]);

        Assert.IsExactInstanceOfType<CompletedDelta>((await Read(Wrapper(inner, store, settings), Request("anthropic")))[^1]);

        Assert.AreEqual(0.0000575m, Assert.ContainsSingle(store.Checkpoint.Requests).Receipt!.ActualSpend);
    }

    [TestMethod]
    [DataRow("missing-input")]
    [DataRow("missing-output")]
    [DataRow("missing-completed")]
    [DataRow("negative")]
    [DataRow("overflow")]
    [DataRow("provider-error")]
    [DataRow("after-completed")]
    public async Task IncompleteOrInvalidUsageRetainsFullReservationAndNeverReplays(string defect)
    {
        LlmDelta[] deltas = defect switch
        {
            "missing-input" => [new AssistantTextDelta("partial"), new UsageDelta(null, 4, null, null), new CompletedDelta()],
            "missing-output" => [new UsageDelta(20, null, null, null), new CompletedDelta()],
            "missing-completed" => [new UsageDelta(20, 4, null, null)],
            "negative" => [new UsageDelta(-1, 4, null, null), new CompletedDelta()],
            "overflow" => [new UsageDelta(long.MaxValue, 1, null, null), new UsageDelta(1, 1, null, null), new CompletedDelta()],
            "provider-error" => [new UsageDelta(20, 4, null, null), new LlmErrorDelta("failure"), new CompletedDelta()],
            _ => [new UsageDelta(20, 4, null, null), new CompletedDelta(), new AssistantTextDelta("late")]
        };
        var settings = Settings(); var store = new Store(settings); var inner = new Client(deltas);
        var result = await Read(Wrapper(inner, store, settings), Request());
        var record = Assert.ContainsSingle(store.Checkpoint.Requests);

        Assert.IsExactInstanceOfType<LlmErrorDelta>(Assert.ContainsSingle(result));
        Assert.AreEqual(LiveEvaluationRequestState.NeedsReconciliation, record.State);
        Assert.AreEqual(Math.Max(record.Request.ReservedSpend, record.Receipt!.ActualSpend), record.ChargedSpend);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Read(Wrapper(inner, store, settings), Request()));
        Assert.AreEqual(1, inner.Calls);
    }

    [TestMethod]
    public async Task AnthropicMissingCacheUsageIsUnknown()
    {
        var settings = Settings() with { Provider = "anthropic" }; var store = new Store(settings);
        var inner = new Client([new UsageDelta(20, 4, null, 0), new CompletedDelta()]);
        Assert.IsExactInstanceOfType<LlmErrorDelta>(Assert.ContainsSingle(await Read(Wrapper(inner, store, settings), Request("anthropic"))));
        Assert.IsTrue(store.Checkpoint.NeedsReconciliation);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExceptionOrCancellationPersistsUnknownUsingIndependentToken(bool cancel)
    {
        var settings = Settings(); var store = new Store(settings); using var cts = new CancellationTokenSource();
        var inner = new Client([new AssistantTextDelta("uncommitted")])
        {
            ThrowAfter = cancel
            ? () => { cts.Cancel(); return new OperationCanceledException(cts.Token); }
            : () => new IOException("dropped")
        };
        var wrapper = Wrapper(inner, store, settings);

        await Assert.ThrowsAsync<Exception>(() => Read(wrapper, Request(), cts.Token));

        Assert.IsTrue(store.Checkpoint.NeedsReconciliation);
        Assert.IsFalse(store.CompletionTokenWasCancelled);
        Assert.IsNull(JsonDocument.Parse(Assert.ContainsSingle(store.Checkpoint.Requests).Receipt!.EvidenceJson).RootElement.GetProperty("response").Deserialize<object>());
    }

    [TestMethod]
    public async Task OversizedResponseDoesNotEscapeOrCauseAutomaticRedispatch()
    {
        var settings = Settings(); var store = new Store(settings);
        var inner = new Client([new AssistantTextDelta(new string('x', EvaluationBudgetedLlmClient.MaxResponseBytes + 1)), new CompletedDelta()]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Read(Wrapper(inner, store, settings), Request()));

        Assert.IsTrue(store.Checkpoint.NeedsReconciliation);
        Assert.AreEqual(1, inner.Calls);
    }

    [TestMethod]
    [DataRow("provider")]
    [DataRow("case")]
    [DataRow("model")]
    [DataRow("output")]
    [DataRow("input")]
    public async Task RejectsUnboundRequestsBeforeReservationOrProvider(string defect)
    {
        var settings = Settings(); var store = new Store(settings); var inner = new Client([]);
        var request = defect switch
        {
            "provider" => Request("anthropic"),
            "case" => Request("OPENAI"),
            "model" => Request() with { Model = "alias" },
            "output" => Request() with { MaxOutputTokens = 1001 },
            _ => Request() with { SystemPrompt = new string('x', 10001) }
        };

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Read(Wrapper(inner, store, settings), request));

        Assert.IsEmpty(store.Checkpoint.Requests); Assert.AreEqual(0, inner.Calls);
    }

    [TestMethod]
    public void ChangedPricesCannotReuseFrozenBinding()
    {
        var settings = Settings(); var store = new Store(settings); var changed = settings with
        {
            Pricing = settings.Pricing! with { InputPerMillion = 0.5m }
        };
        Assert.ThrowsExactly<ArgumentException>(() => new EvaluationBudgetedLlmClient(new Client([]), store, changed,
            "campaign", "trial", "direct", Owner, store.Checkpoint.Definition.BindingJson));
    }

    [TestMethod]
    public async Task ChangedCampaignBindingFailsBeforePaidAdmission()
    {
        var settings = Settings(); var store = new Store(settings); var inner = new Client([]);
        var wrapper = Wrapper(inner, store, settings);
        store.Checkpoint = store.Checkpoint with { Definition = store.Checkpoint.Definition with { BindingJson = "{}" } };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Read(wrapper, Request()));
        Assert.AreEqual(0, inner.Calls);
    }

    [TestMethod]
    [DataRow(10001, 4)]
    [DataRow(20, 101)]
    public async Task CompleteUsageBeyondDeclaredCeilingsIsRecordedAndBlocksLaterAdmissions(long input, long output)
    {
        var settings = Settings(); var store = new Store(settings);
        var inner = new Client([new UsageDelta(input, output, null, null), new CompletedDelta()]);
        await Read(Wrapper(inner, store, settings), Request());
        var receipt = Assert.ContainsSingle(store.Checkpoint.Requests).Receipt!;
        Assert.IsTrue(receipt.UsageComplete); Assert.IsTrue(receipt.ReservationAssumptionsExceeded);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Read(Wrapper(inner, store, settings), Request() with { RunId = RunId.New() }));
        Assert.AreEqual(1, inner.Calls);
    }

    [TestMethod]
    public async Task CorruptCompletedResponseDoesNotCausePaidRetry()
    {
        var settings = Settings(); var store = new Store(settings);
        var inner = new Client([new UsageDelta(20, 4, null, null), new CompletedDelta()]);
        await Read(Wrapper(inner, store, settings), Request());
        var record = Assert.ContainsSingle(store.Checkpoint.Requests);
        store.Checkpoint = store.Checkpoint with { Requests = [record with { Receipt = record.Receipt! with { EvidenceJson = "{}" } }] };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Read(Wrapper(inner, store, settings), Request()));
        Assert.AreEqual(1, inner.Calls);
    }

    private static readonly CallerIdentity Owner = new("owner", "Owner", true);
    private static LiveEvaluationSettings Settings() => new()
    {
        Enabled = true,
        Provider = "openai",
        Model = "exact",
        MaxTotalSpend = 1m,
        MaxInputTokensPerRequest = 10000,
        MaxOutputTokensPerRequest = 1000,
        InputFramingTokenAllowance = 1024,
        Pricing = new()
        {
            InputPerMillion = 1m,
            OutputPerMillion = 2m,
            CacheReadPerMillion = 0.1m,
            CacheWritePerMillion = 1.25m,
            Source = "supplied fixture prices",
            AsOfUtc = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero)
        }
    };
    private static LlmRequest Request(string provider = "openai") => new(new RunId("stable-run"), new SessionKey("test"),
        provider, "exact", "system", [new ChatMessage(ChatRole.User, "input")], [], TimeSpan.FromMinutes(1), 100, 3);
    private static EvaluationBudgetedLlmClient Wrapper(ILlmClient inner, Store store, LiveEvaluationSettings settings) =>
        new(inner, store, settings, "campaign", "trial", "direct", Owner, store.Checkpoint.Definition.BindingJson);
    private static async Task<List<LlmDelta>> Read(ILlmClient client, LlmRequest request, CancellationToken ct = default)
    {
        var result = new List<LlmDelta>();
        await foreach (var delta in client.StreamAsync(request, ct)) result.Add(delta);
        return result;
    }
    private static object Describe(LlmDelta delta) => new { kind = delta.GetType().Name, json = JsonSerializer.Serialize(delta, delta.GetType()) };

    private sealed class Client(LlmDelta[] deltas) : ILlmClient
    {
        public int Calls { get; private set; }
        public LlmRequest? LastRequest { get; private set; }
        public Action? OnStart { get; set; }
        public Func<Exception>? ThrowAfter { get; init; }
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++; LastRequest = request; OnStart?.Invoke();
            foreach (var delta in deltas) { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return delta; }
            if (ThrowAfter is not null) throw ThrowAfter();
        }
    }

    private sealed class Store : ILiveEvaluationStore
    {
        public LiveEvaluationCheckpoint Checkpoint { get; set; }
        public bool CompletionTokenWasCancelled { get; private set; }
        public Store(LiveEvaluationSettings settings)
        {
            var definition = new LiveEvaluationDefinition("campaign", settings.Provider!, settings.Model!, "USD", settings.MaxTotalSpend!.Value,
                TaskGraphDocumentJson.Serialize(new { settings }));
            Checkpoint = new(1, definition, LiveEvaluationIdentity.Fingerprint(definition), Owner, 0, WorkflowControl.None,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], []);
        }
        public Task<LiveEvaluationCheckpoint?> GetEvaluationAsync(string campaignId, CallerIdentity caller, CancellationToken ct) => Task.FromResult<LiveEvaluationCheckpoint?>(Checkpoint);
        public Task<LiveEvaluationAdmission> BeginEvaluationRequestAsync(string campaignId, LiveEvaluationRequest request, CallerIdentity caller, CancellationToken ct)
        {
            var existing = Checkpoint.Requests.SingleOrDefault(item => item.Request.RequestId == request.RequestId);
            if (existing is not null)
            {
                if (existing.State != LiveEvaluationRequestState.Completed || existing.Request != request) throw new InvalidOperationException("Unknown or mismatched");
                return Task.FromResult(new LiveEvaluationAdmission(false, existing));
            }
            if (Checkpoint.NeedsReconciliation || Checkpoint.HasReservationOverrun || Checkpoint.RemainingSpend < request.ReservedSpend)
                throw new InvalidOperationException("Budget blocked");
            var record = new LiveEvaluationRequestRecord(request, LiveEvaluationRequestState.Started, DateTimeOffset.UtcNow);
            Checkpoint = Checkpoint with { Requests = Checkpoint.Requests.Add(record) };
            return Task.FromResult(new LiveEvaluationAdmission(true, record));
        }
        public Task<LiveEvaluationCheckpoint> CompleteEvaluationRequestAsync(string campaignId, string requestId, LiveEvaluationReceipt receipt, CallerIdentity caller, CancellationToken ct)
        {
            CompletionTokenWasCancelled = ct.IsCancellationRequested;
            var index = Enumerable.Range(0, Checkpoint.Requests.Length).Single(index => Checkpoint.Requests[index].Request.RequestId == requestId);
            var record = Checkpoint.Requests[index] with
            {
                State = receipt.UsageComplete ? LiveEvaluationRequestState.Completed : LiveEvaluationRequestState.NeedsReconciliation,
                Receipt = receipt,
                CompletedAt = receipt.UsageComplete ? DateTimeOffset.UtcNow : null
            };
            Checkpoint = Checkpoint with { Requests = Checkpoint.Requests.SetItem(index, record) };
            return Task.FromResult(Checkpoint);
        }
        public Task<LiveEvaluationCheckpoint> CreateEvaluationAsync(LiveEvaluationDefinition definition, CallerIdentity caller, CancellationToken ct) => throw new NotSupportedException();
        public Task<LiveEvaluationCheckpoint> ReconcileEvaluationRequestAsync(string campaignId, string requestId, LiveEvaluationReceipt receipt, string note, CallerIdentity caller, CancellationToken ct) => throw new NotSupportedException();
        public Task<LiveEvaluationCheckpoint> RecordEvaluationTrialAsync(string campaignId, LiveEvaluationTrialResult trial, CallerIdentity caller, CancellationToken ct) => throw new NotSupportedException();
        public Task<LiveEvaluationCheckpoint> SetEvaluationControlAsync(string campaignId, WorkflowControl control, CallerIdentity caller, CancellationToken ct) => throw new NotSupportedException();
    }
}
