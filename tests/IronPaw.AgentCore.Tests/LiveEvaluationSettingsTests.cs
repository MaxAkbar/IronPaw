using System.Text.Json;

namespace IronPaw.AgentCore.Tests;

public sealed class LiveEvaluationSettingsTests
{
    [Fact]
    public void DisabledDefaultsRemainUnconfiguredWithoutInventingProviderOrSpend()
    {
        var settings = new LiveEvaluationSettings();

        var result = settings.Validate();

        Assert.True(result.IsValid);
        Assert.False(result.IsReady);
        Assert.Empty(result.Errors);
        Assert.Null(settings.Provider);
        Assert.Null(settings.Model);
        Assert.Null(settings.MaxTotalSpend);
        Assert.Throws<InvalidOperationException>(() => settings.RequireEnabled());
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("ANTHROPIC")]
    [InlineData("ollama")]
    [InlineData("lmstudio")]
    public void EnabledSettingsFreezeExplicitProviderModelAndDecimalWholeComparisonCap(string provider)
    {
        var settings = Complete() with { Provider = provider };

        var result = settings.Validate();
        var accepted = settings.RequireEnabled();

        Assert.True(result.IsValid);
        Assert.True(result.IsReady);
        Assert.Equal(provider.ToLowerInvariant(), accepted.Provider);
        Assert.Equal("exact-model-id", accepted.Model);
        Assert.Equal(12.34m, accepted.MaxTotalSpend);
        Assert.Equal("USD", accepted.Currency);
        Assert.Equal(provider, settings.Provider);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("spend")]
    public void EnabledSettingsCannotOmitRequiredLiveValues(string missing)
    {
        var settings = missing switch {
            "provider" => Complete() with { Provider = null },
            "model" => Complete() with { Model = null },
            _ => Complete() with { MaxTotalSpend = null }
        };

        Assert.False(settings.Validate().IsValid);
        Assert.False(settings.Validate().IsReady);
        Assert.Throws<ArgumentException>(() => settings.RequireEnabled());
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("unknown-provider")]
    [InlineData("rule-based-provider")]
    [InlineData("padded-provider")]
    [InlineData("blank-model")]
    [InlineData("padded-model")]
    [InlineData("control-model")]
    [InlineData("zero-cap")]
    [InlineData("negative-cap")]
    [InlineData("null-currency")]
    [InlineData("lowercase-currency")]
    [InlineData("currency-length")]
    [InlineData("currency-characters")]
    public void MalformedProvidedValuesFailEvenWhenLiveExecutionIsDisabled(string defect)
    {
        var settings = defect switch {
            "schema" => Complete() with { SchemaVersion = 2 },
            "unknown-provider" => Complete() with { Provider = "opneai" },
            "rule-based-provider" => Complete() with { Provider = "local" },
            "padded-provider" => Complete() with { Provider = " openai " },
            "blank-model" => Complete() with { Model = "" },
            "padded-model" => Complete() with { Model = " exact-model " },
            "control-model" => Complete() with { Model = "model\nother" },
            "zero-cap" => Complete() with { MaxTotalSpend = 0 },
            "negative-cap" => Complete() with { MaxTotalSpend = -1 },
            "null-currency" => Complete() with { Currency = null! },
            "lowercase-currency" => Complete() with { Currency = "usd" },
            "currency-length" => Complete() with { Currency = "US" },
            _ => Complete() with { Currency = "U$D" }
        };

        var result = (settings with { Enabled = false }).Validate();

        Assert.False(result.IsValid);
        Assert.False(result.IsReady);
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("{\"enabled\":false,\"apiKey\":\"secret\"}")]
    [InlineData("{\"provider\":\"openai\",\"PROVIDER\":\"anthropic\"}")]
    [InlineData("{\"maxTotalSpend\":\"5\"}")]
    [InlineData("{\"schemaVersion\":\"1\"}")]
    [InlineData("null")]
    public void StrictConfigurationRejectsSecretFieldsAndAmbiguousDocuments(string json) =>
        Assert.ThrowsAny<JsonException>(() => TaskGraphDocumentJson.Parse<LiveEvaluationSettings>(json));

    [Fact]
    public void ConfigurationRoundTripPreservesDecimalCapWithoutEnablingDisabledSettings()
    {
        var settings = Complete() with { Enabled = false, MaxTotalSpend = 0.0001m };

        var parsed = TaskGraphDocumentJson.Parse<LiveEvaluationSettings>(TaskGraphDocumentJson.Serialize(settings));

        Assert.Equal(settings, parsed);
        Assert.True(parsed.Validate().IsValid);
        Assert.False(parsed.Validate().IsReady);
    }

    [Theory]
    [InlineData("pricing")]
    [InlineData("input-ceiling")]
    [InlineData("output-ceiling")]
    [InlineData("framing")]
    public void EnabledExecutionRequiresDeclaredRatesAndRequestCeilings(string missing)
    {
        var settings = missing switch {
            "pricing" => Complete() with { Pricing = null },
            "input-ceiling" => Complete() with { MaxInputTokensPerRequest = null },
            "output-ceiling" => Complete() with { MaxOutputTokensPerRequest = null },
            _ => Complete() with { InputFramingTokenAllowance = null }
        };
        Assert.False(settings.Validate().IsValid);
        Assert.Throws<ArgumentException>(() => settings.RequireEnabled());
        Assert.True((settings with { Enabled = false }).Validate().IsValid);
    }

    [Theory]
    [InlineData("input-negative")]
    [InlineData("output-negative")]
    [InlineData("read-negative")]
    [InlineData("write-negative")]
    [InlineData("input-missing")]
    [InlineData("output-missing")]
    [InlineData("read-missing")]
    [InlineData("write-missing")]
    [InlineData("unbounded-rate")]
    [InlineData("all-zero")]
    [InlineData("source")]
    [InlineData("source-control")]
    [InlineData("source-padding")]
    [InlineData("asof-missing")]
    [InlineData("asof-not-utc")]
    public void SuppliedMalformedPriceSchedulesFailWithoutInventedFallbacks(string defect)
    {
        var pricing = Complete().Pricing!;
        pricing = defect switch {
            "input-negative" => pricing with { InputPerMillion = -1 },
            "output-negative" => pricing with { OutputPerMillion = -1 },
            "read-negative" => pricing with { CacheReadPerMillion = -1 },
            "write-negative" => pricing with { CacheWritePerMillion = -1 },
            "input-missing" => pricing with { InputPerMillion = null },
            "output-missing" => pricing with { OutputPerMillion = null },
            "read-missing" => pricing with { CacheReadPerMillion = null },
            "write-missing" => pricing with { CacheWritePerMillion = null },
            "unbounded-rate" => pricing with { InputPerMillion = decimal.MaxValue },
            "all-zero" => pricing with { InputPerMillion = 0, OutputPerMillion = 0, CacheReadPerMillion = 0, CacheWritePerMillion = 0 },
            "source" => pricing with { Source = null },
            "source-control" => pricing with { Source = "source\nsecret" },
            "source-padding" => pricing with { Source = " source " },
            "asof-missing" => pricing with { AsOfUtc = null },
            _ => pricing with { AsOfUtc = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.FromHours(1)) }
        };

        Assert.False((Complete() with { Pricing = pricing, Enabled = false }).Validate().IsValid);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("negative-framing")]
    [InlineData("framing-exhausts-input")]
    [InlineData("zero-trials")]
    [InlineData("too-many-trials")]
    public void InvalidRequestAndTrialBoundsFailEvenWhenDisabled(string defect)
    {
        var settings = defect switch {
            "input" => Complete() with { MaxInputTokensPerRequest = 0 },
            "output" => Complete() with { MaxOutputTokensPerRequest = 0 },
            "negative-framing" => Complete() with { InputFramingTokenAllowance = -1 },
            "framing-exhausts-input" => Complete() with { InputFramingTokenAllowance = 10000 },
            "zero-trials" => Complete() with { PairedTrials = 0 },
            _ => Complete() with { PairedTrials = 1001 }
        };
        Assert.False((settings with { Enabled = false }).Validate().IsValid);
    }

    [Fact]
    public void EnabledExecutionDoesNotSilentlyConvertCurrency()
    {
        Assert.False((Complete() with { Currency = "EUR" }).Validate().IsValid);
        Assert.True((Complete() with { Currency = "EUR", Enabled = false }).Validate().IsValid);
    }

    private static LiveEvaluationSettings Complete() => new() {
        Enabled = true, Provider = "openai", Model = "exact-model-id", MaxTotalSpend = 12.34m, Currency = "USD",
        MaxInputTokensPerRequest = 10000, MaxOutputTokensPerRequest = 1000, InputFramingTokenAllowance = 1024,
        Pricing = new() {
            InputPerMillion = 1m, OutputPerMillion = 2m, CacheReadPerMillion = 0.1m, CacheWritePerMillion = 1.25m,
            Source = "fixture supplied prices", AsOfUtc = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero)
        }
    };
}
