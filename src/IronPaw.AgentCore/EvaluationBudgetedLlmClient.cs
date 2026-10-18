using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

/// <summary>
/// Persists admission before a billable stream. Monetary amounts are estimates under the frozen
/// host price schedule and input ceiling; observed overruns are recorded, never hidden.
/// A bounded completed response is durably stored before delivery and can replay without another
/// provider call. Unknown or oversized responses require explicit reconciliation.
/// </summary>
public sealed class EvaluationBudgetedLlmClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly ILiveEvaluationStore _store;
    private readonly string _campaignId;
    private readonly CallerIdentity _caller;
    private readonly LiveEvaluationSettings _settings;
    private readonly string _trialId;
    private readonly string _strategy;
    private readonly string _bindingJson;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public const int MaxResponseBytes = 1024 * 1024;

    public EvaluationBudgetedLlmClient(ILlmClient inner, ILiveEvaluationStore store, LiveEvaluationSettings settings,
        string campaignId, string trialId, string strategy, CallerIdentity caller, string expectedBindingJson)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.IsOwner) throw new UnauthorizedAccessException("Live evaluation requires the original local owner.");
        _inner = inner;
        _store = store;
        _campaignId = Required(campaignId, nameof(campaignId));
        _caller = caller;
        _settings = settings.RequireEnabled();
        _trialId = Required(trialId, nameof(trialId));
        _strategy = Required(strategy, nameof(strategy));
        _bindingJson = Required(expectedBindingJson, nameof(expectedBindingJson));
        using var binding = JsonDocument.Parse(_bindingJson);
        if (!binding.RootElement.TryGetProperty("settings", out var bound) ||
            TaskGraphDocumentJson.Serialize(TaskGraphDocumentJson.Parse<LiveEvaluationSettings>(bound.GetRawText()).RequireEnabled())
                != TaskGraphDocumentJson.Serialize(_settings))
            throw new ArgumentException("The immutable campaign binding must contain these exact settings and prices.", nameof(expectedBindingJson));
    }

    public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Freeze caller-owned collections before hashing or forwarding; only paid continuation is changed.
        var frozen = ValidateAndFreeze(request);
        var fingerprint = LiveEvaluationIdentity.Digest(JsonSerializer.Serialize(frozen));
        var requestId = LiveEvaluationIdentity.Digest(JsonSerializer.Serialize(new { _trialId, _strategy, fingerprint }));
        var checkpoint = await _store.GetEvaluationAsync(_campaignId, _caller, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The live evaluation campaign does not exist.");
        ValidateCampaign(checkpoint);
        var previous = checkpoint.Requests.SingleOrDefault(item => item.Request.RequestId == requestId);
        var reservation = previous?.Request.ReservedSpend ?? Reservation(frozen.MaxOutputTokens);
        var admission = await _store.BeginEvaluationRequestAsync(_campaignId,
            new LiveEvaluationRequest(requestId, _trialId, _strategy, fingerprint, reservation), _caller, ct).ConfigureAwait(false);
        if (!admission.ShouldExecute)
        {
            foreach (var delta in Replay(admission.Invocation, fingerprint))
            {
                ct.ThrowIfCancellationRequested();
                yield return delta;
            }
            yield break;
        }

        var usage = new ObservedUsage(_settings.Provider!);
        var completed = false;
        var failed = false;
        var ended = false;
        var knownReceipt = false;
        var response = new List<StoredDelta>();
        long responseBytes = 2;
        try
        {
            await foreach (var delta in _inner.StreamAsync(frozen, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                if (completed)
                {
                    failed = true;
                    continue;
                }
                if (delta is CompletedDelta)
                {
                    completed = true;
                    continue;
                }
                if (delta is UsageDelta tokens) usage.Add(tokens);
                if (delta is LlmErrorDelta) failed = true;
                var stored = StoredDelta.From(delta);
                responseBytes = checked(responseBytes + Utf8.GetByteCount(JsonSerializer.Serialize(stored)) + 1);
                if (responseBytes > MaxResponseBytes)
                    throw new InvalidDataException("The paid response exceeds the bounded durable replay limit; reconciliation is required.");
                response.Add(stored);
            }
            ended = true;
        }
        finally
        {
            var known = ended && completed && !failed && usage.Complete;
            var amount = usage.Amount(_settings.Pricing!, out var arithmeticValid);
            known &= arithmeticValid;
            var storedResponse = known ? response.ToArray() : null;
            var serializedResponse = storedResponse is null ? null : JsonSerializer.Serialize(storedResponse);
            var receipt = new LiveEvaluationReceipt(known, amount, usage.Input, usage.Output,
                JsonSerializer.Serialize(new {
                    schemaVersion = 1, provider = _settings.Provider, model = _settings.Model,
                    priceSource = _settings.Pricing!.Source, priceAsOfUtc = _settings.Pricing.AsOfUtc,
                    accounting = _settings.Provider == "anthropic" ? "disjoint-input-cache-categories" : "inclusive-input-at-maximum-configured-input-rate",
                    inputTokens = usage.Input, outputTokens = usage.Output,
                    cacheReadTokens = usage.CacheRead, cacheWriteTokens = usage.CacheWrite,
                    usageValid = usage.Valid && arithmeticValid, completed, ended, failed,
                    estimatedSpend = true, reservationAssumptions = "declared-input-ceiling-and-price-schedule",
                    inputCeiling = _settings.MaxInputTokensPerRequest, outputCeiling = frozen.MaxOutputTokens,
                    observedInputCeilingOverrun = usage.TotalInput > _settings.MaxInputTokensPerRequest,
                    observedOutputCeilingOverrun = usage.Output > frozen.MaxOutputTokens,
                    requestFingerprint = fingerprint, response = storedResponse,
                    responseDigest = serializedResponse is null ? null : LiveEvaluationIdentity.Digest(serializedResponse)
                }), ReservationAssumptionsExceeded: usage.TotalInput > _settings.MaxInputTokensPerRequest || usage.Output > frozen.MaxOutputTokens);
            // Caller cancellation must not discard accounting for a request which may have reached the provider.
            using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _store.CompleteEvaluationRequestAsync(_campaignId, requestId, receipt, _caller, persist.Token).ConfigureAwait(false);
            knownReceipt = known;
        }
        if (knownReceipt)
        {
            foreach (var item in response)
            {
                ct.ThrowIfCancellationRequested();
                yield return item.ToDelta();
            }
            ct.ThrowIfCancellationRequested();
            yield return new CompletedDelta();
        }
        else
            yield return new LlmErrorDelta("Live evaluation usage is incomplete or invalid; its reservation remains charged and reconciliation is required.");
    }

    private static IReadOnlyList<LlmDelta> Replay(LiveEvaluationRequestRecord invocation, string fingerprint)
    {
        if (invocation.State != LiveEvaluationRequestState.Completed || invocation.Receipt?.UsageComplete != true)
            throw new InvalidOperationException("The prior paid request is uncertain and cannot be automatically redispatched.");
        using var document = JsonDocument.Parse(invocation.Receipt.EvidenceJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("requestFingerprint", out var identity) || identity.GetString() != fingerprint ||
            !root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("responseDigest", out var digest))
            throw new InvalidDataException("The completed paid request has no verified replay response; explicit reconciliation is required.");
        var stored = response.Deserialize<StoredDelta[]>() ?? throw new InvalidDataException("The completed response is missing.");
        var json = JsonSerializer.Serialize(stored);
        if (Utf8.GetByteCount(json) > MaxResponseBytes || digest.GetString() != LiveEvaluationIdentity.Digest(json))
            throw new InvalidDataException("The completed paid response failed its bounded replay integrity check.");
        return stored.Select(item => item.ToDelta()).Append(new CompletedDelta()).ToArray();
    }

    private LlmRequest ValidateAndFreeze(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Provider, _settings.Provider, StringComparison.Ordinal) ||
            !string.Equals(request.Model, _settings.Model, StringComparison.Ordinal))
            throw new ArgumentException("The request provider and model must exactly match the frozen live evaluation campaign.");
        if (request.MaxOutputTokens <= 0 || request.MaxOutputTokens > _settings.MaxOutputTokensPerRequest)
            throw new ArgumentException("The request output limit exceeds the configured live evaluation limit.");
        if (request.Timeout <= TimeSpan.Zero || request.Messages is null || request.Tools is null || request.SystemPrompt is null)
            throw new ArgumentException("The live evaluation request is incomplete.");
        var frozen = request with {
            Messages = request.Messages.Select(message => message with { }).ToArray(),
            Tools = request.Tools.Select(tool => tool with { ParametersSchema = tool.ParametersSchema.Clone() }).ToArray(),
            MaxAutoContinuationPasses = 0
        };
        var content = JsonSerializer.Serialize(new { frozen.SystemPrompt, frozen.Messages, frozen.Tools });
        // UTF-8 bytes plus reviewed framing is deliberately conservative for content. The hidden
        // provider framing/tokenizer assumption is explicit configuration, not a proved model cap.
        var guard = checked((long)Utf8.GetByteCount(content) + _settings.InputFramingTokenAllowance!.Value);
        if (guard > _settings.MaxInputTokensPerRequest)
            throw new ArgumentException("Request content and the configured framing allowance exceed the declared input ceiling.");
        return frozen;
    }

    private void ValidateCampaign(LiveEvaluationCheckpoint checkpoint)
    {
        var definition = checkpoint.Definition;
        if (definition.Provider != _settings.Provider || definition.Model != _settings.Model ||
            definition.Currency != _settings.Currency || definition.MaxTotalSpend != _settings.MaxTotalSpend ||
            definition.BindingJson != _bindingJson)
            throw new InvalidOperationException("The live evaluation settings or immutable campaign binding changed.");
    }

    private decimal Reservation(int maxOutputTokens)
    {
        var rates = _settings.Pricing!;
        return checked((_settings.MaxInputTokensPerRequest!.Value *
            (rates.InputPerMillion!.Value + rates.CacheReadPerMillion!.Value + rates.CacheWritePerMillion!.Value)
            + maxOutputTokens * rates.OutputPerMillion!.Value) / 1_000_000m);
    }

    private static string Required(string value, string name) => !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException("A nonempty value is required.", name);

    private sealed record StoredDelta(string Kind, string? Text = null, string? ToolCallId = null,
        string? ToolName = null, JsonElement? Arguments = null, long? Input = null, long? Output = null,
        long? CacheRead = null, long? CacheWrite = null)
    {
        public static StoredDelta From(LlmDelta delta) => delta switch {
            AssistantTextDelta text => new("text", Text: text.Text),
            ToolCallDelta tool => new("tool", ToolCallId: tool.ToolCallId, ToolName: tool.ToolName, Arguments: tool.Arguments.Clone()),
            UsageDelta usage => new("usage", Input: usage.InputTokens, Output: usage.OutputTokens,
                CacheRead: usage.CacheReadTokens, CacheWrite: usage.CacheWriteTokens),
            LlmErrorDelta error => new("error", Text: error.Error),
            _ => throw new InvalidDataException("The provider emitted an unsupported response delta.")
        };

        public LlmDelta ToDelta() => Kind switch {
            "text" when Text is not null => new AssistantTextDelta(Text),
            "tool" when ToolCallId is not null && ToolName is not null && Arguments.HasValue =>
                new ToolCallDelta(ToolCallId, ToolName, Arguments.Value.Clone()),
            "usage" => new UsageDelta(Input, Output, CacheRead, CacheWrite),
            _ => throw new InvalidDataException("The durable response contains an invalid replay delta.")
        };
    }

    private sealed class ObservedUsage(string provider)
    {
        public long? Input { get; private set; }
        public long? Output { get; private set; }
        public long? CacheRead { get; private set; }
        public long? CacheWrite { get; private set; }
        public bool Valid { get; private set; } = true;
        public bool Complete => Valid && Input.HasValue && Output.HasValue &&
            (provider != "anthropic" || (CacheRead.HasValue && CacheWrite.HasValue));
        public long? TotalInput
        {
            get
            {
                try { return provider == "anthropic" ? checked(Input + CacheRead + CacheWrite) : Input; }
                catch (OverflowException) { return long.MaxValue; }
            }
        }

        public void Add(UsageDelta delta)
        {
            if (delta.InputTokens < 0 || delta.OutputTokens < 0 || delta.CacheReadTokens < 0 || delta.CacheWriteTokens < 0)
            { Valid = false; return; }
            try
            {
                Input = Add(Input, delta.InputTokens);
                Output = Add(Output, delta.OutputTokens);
                CacheRead = Add(CacheRead, delta.CacheReadTokens);
                CacheWrite = Add(CacheWrite, delta.CacheWriteTokens);
            }
            catch (OverflowException) { Valid = false; }
        }

        public decimal Amount(LiveEvaluationPricing rates, out bool valid)
        {
            try
            {
                valid = true;
                var input = provider == "anthropic"
                    ? checked((Input ?? 0) * rates.InputPerMillion!.Value +
                        (CacheRead ?? 0) * rates.CacheReadPerMillion!.Value + (CacheWrite ?? 0) * rates.CacheWritePerMillion!.Value)
                    : checked((Input ?? 0) * Math.Max(rates.InputPerMillion!.Value,
                        Math.Max(rates.CacheReadPerMillion!.Value, rates.CacheWritePerMillion!.Value)));
                return checked((input + (Output ?? 0) * rates.OutputPerMillion!.Value) / 1_000_000m);
            }
            catch (OverflowException) { valid = false; return 0m; } // Unknown usage retains the whole reservation.
        }

        private static long? Add(long? total, long? delta) => delta.HasValue ? checked((total ?? 0) + delta.Value) : total;
    }
}
