using System.Collections.Immutable;
using System.Text.Json.Serialization;
using IronPaw.AgentCore.Providers;

namespace IronPaw.AgentCore;

/// <summary>
/// Explicit live-comparison configuration. The spend limit covers both strategies and all trials
/// together. Prices and input ceilings are declared host assumptions, not a provider billing guarantee.
/// </summary>
public sealed record LiveEvaluationSettings
{
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int SchemaVersion { get; init; } = 1;
    public bool Enabled { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public decimal? MaxTotalSpend { get; init; }
    public string Currency { get; init; } = "USD";
    public LiveEvaluationPricing? Pricing { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int? MaxInputTokensPerRequest { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int? MaxOutputTokensPerRequest { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int? InputFramingTokenAllowance { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int PairedTrials { get; init; } = 20;
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int Seed { get; init; } = 1729;

    public LiveEvaluationSettingsValidation Validate()
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        if (SchemaVersion != 1) errors.Add("Unsupported live evaluation configuration schema; expected 1.");
        if (Provider is not null && (string.IsNullOrWhiteSpace(Provider) || Provider != Provider.Trim() ||
            Provider.Equals("local", StringComparison.OrdinalIgnoreCase) ||
            !ProviderDefaults.ProviderNames.Contains(Provider, StringComparer.OrdinalIgnoreCase)))
            errors.Add("Provider must identify a supported model provider; the rule-based local replay provider cannot qualify a live comparison.");
        if (Model is not null && (string.IsNullOrWhiteSpace(Model) || Model != Model.Trim() || Model.Any(char.IsControl)))
            errors.Add("Model must be an explicit, unpadded model ID with no control characters.");
        if (MaxTotalSpend is <= 0)
            errors.Add("MaxTotalSpend must be a positive decimal amount for the whole comparison, including both strategies and every trial.");
        if (Currency is null || Currency.Length != 3 || Currency.Any(character => character is < 'A' or > 'Z'))
            errors.Add("Currency must be a three-letter uppercase currency label, such as USD.");
        if (MaxInputTokensPerRequest is <= 0) errors.Add("MaxInputTokensPerRequest must be positive.");
        if (MaxOutputTokensPerRequest is <= 0) errors.Add("MaxOutputTokensPerRequest must be positive.");
        if (InputFramingTokenAllowance is < 0) errors.Add("InputFramingTokenAllowance cannot be negative.");
        if (InputFramingTokenAllowance.HasValue && MaxInputTokensPerRequest.HasValue &&
            InputFramingTokenAllowance.Value >= MaxInputTokensPerRequest.Value)
            errors.Add("InputFramingTokenAllowance must leave room for request content within MaxInputTokensPerRequest.");
        if (Pricing is not null) errors.AddRange(Pricing.Validate());
        if (PairedTrials is < 1 or > 1000) errors.Add("PairedTrials must be between 1 and 1000.");
        if (Enabled)
        {
            if (Provider is null) errors.Add("Enabled live evaluation requires an explicit Provider.");
            if (Model is null) errors.Add("Enabled live evaluation requires an exact Model ID.");
            if (MaxTotalSpend is null) errors.Add("Enabled live evaluation requires an explicit MaxTotalSpend.");
            if (Currency != "USD") errors.Add("Enabled live evaluation supports USD only; no currency conversion is performed.");
            if (Pricing is null) errors.Add("Enabled live evaluation requires an explicit price schedule and provenance.");
            if (MaxInputTokensPerRequest is null) errors.Add("Enabled live evaluation requires MaxInputTokensPerRequest.");
            if (MaxOutputTokensPerRequest is null) errors.Add("Enabled live evaluation requires MaxOutputTokensPerRequest.");
            if (InputFramingTokenAllowance is null) errors.Add("Enabled live evaluation requires an explicit InputFramingTokenAllowance.");
        }
        return new(errors.Count == 0, Enabled && errors.Count == 0, errors.ToImmutable());
    }

    /// <summary>Returns complete enabled settings without initiating execution or granting authority.</summary>
    public LiveEvaluationSettings RequireEnabled()
    {
        var validation = Validate();
        if (!validation.IsValid) throw new ArgumentException(string.Join(Environment.NewLine, validation.Errors));
        if (!Enabled) throw new InvalidOperationException("Live evaluation is disabled in this configuration.");
        return this with { Provider = Provider!.ToLowerInvariant() };
    }
}

/// <param name="IsReady">Complete enabled configuration only; does not authorize billable execution.</param>
public sealed record LiveEvaluationSettingsValidation(bool IsValid, bool IsReady, ImmutableArray<string> Errors);

/// <summary>USD per million tokens, explicitly supplied for the exact selected model and endpoint.</summary>
public sealed record LiveEvaluationPricing
{
    [JsonNumberHandling(JsonNumberHandling.Strict)] public decimal? InputPerMillion { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)] public decimal? OutputPerMillion { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)] public decimal? CacheReadPerMillion { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)] public decimal? CacheWritePerMillion { get; init; }
    public string? Source { get; init; }
    public DateTimeOffset? AsOfUtc { get; init; }

    internal ImmutableArray<string> Validate()
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        foreach (var (name, rate) in new[] {
            (nameof(InputPerMillion), InputPerMillion), (nameof(OutputPerMillion), OutputPerMillion),
            (nameof(CacheReadPerMillion), CacheReadPerMillion), (nameof(CacheWritePerMillion), CacheWritePerMillion) })
            if (!rate.HasValue || rate < 0 || rate > 1_000_000_000_000m)
                errors.Add($"Pricing.{name} must be an explicit nonnegative USD rate per million tokens, at most 1000000000000.");
        if (InputPerMillion == 0 && OutputPerMillion == 0 && CacheReadPerMillion == 0 && CacheWritePerMillion == 0)
            errors.Add("The price schedule must contain at least one positive rate.");
        if (string.IsNullOrWhiteSpace(Source) || Source != Source.Trim() || Source.Any(char.IsControl) || Source.Length > 2048)
            errors.Add("Pricing.Source must identify the reviewed price source without padding or control characters.");
        if (!AsOfUtc.HasValue || AsOfUtc.Value == default || AsOfUtc.Value.Offset != TimeSpan.Zero)
            errors.Add("Pricing.AsOfUtc must be an explicit UTC price review timestamp.");
        return errors.ToImmutable();
    }
}
