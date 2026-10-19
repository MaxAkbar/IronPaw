using System.Text.Json;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class LiveEvaluationSettingsTests
{
    [TestMethod]
    public void DisabledDefaultsRemainUnconfiguredWithoutInventingProviderOrSpend()
    {
        var settings = new LiveEvaluationSettings();

        var result = settings.Validate();

        Assert.IsTrue(result.IsValid);
        Assert.IsFalse(result.IsReady);
        Assert.IsEmpty(result.Errors);
        Assert.IsNull(settings.Provider);
        Assert.IsNull(settings.Model);
        Assert.IsNull(settings.MaxTotalSpend);
        Assert.ThrowsExactly<InvalidOperationException>(() => settings.RequireEnabled());
    }

    [TestMethod]
    [DataRow("openai")]
    [DataRow("ANTHROPIC")]
    [DataRow("ollama")]
    [DataRow("lmstudio")]
    public void EnabledSettingsFreezeExplicitProviderModelAndDecimalWholeComparisonCap(string provider)
    {
        var settings = Complete() with { Provider = provider };

        var result = settings.Validate();
        var accepted = settings.RequireEnabled();

        Assert.IsTrue(result.IsValid);
        Assert.IsTrue(result.IsReady);
        Assert.AreEqual(provider.ToLowerInvariant(), accepted.Provider);
        Assert.AreEqual("exact-model-id", accepted.Model);
        Assert.AreEqual(12.34m, accepted.MaxTotalSpend);
        Assert.AreEqual("USD", accepted.Currency);
        Assert.AreEqual(provider, settings.Provider);
    }

    [TestMethod]
    [DataRow("provider")]
    [DataRow("model")]
    [DataRow("spend")]
    public void EnabledSettingsCannotOmitRequiredLiveValues(string missing)
    {
        var settings = missing switch
        {
            "provider" => Complete() with { Provider = null },
            "model" => Complete() with { Model = null },
            _ => Complete() with { MaxTotalSpend = null }
        };

        Assert.IsFalse(settings.Validate().IsValid);
        Assert.IsFalse(settings.Validate().IsReady);
        Assert.ThrowsExactly<ArgumentException>(() => settings.RequireEnabled());
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("unknown-provider")]
    [DataRow("rule-based-provider")]
    [DataRow("padded-provider")]
    [DataRow("blank-model")]
    [DataRow("padded-model")]
    [DataRow("control-model")]
    [DataRow("zero-cap")]
    [DataRow("negative-cap")]
    [DataRow("null-currency")]
    [DataRow("lowercase-currency")]
    [DataRow("currency-length")]
    [DataRow("currency-characters")]
    public void MalformedProvidedValuesFailEvenWhenLiveExecutionIsDisabled(string defect)
    {
        var settings = defect switch
        {
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

        Assert.IsFalse(result.IsValid);
        Assert.IsFalse(result.IsReady);
        Assert.IsNotEmpty(result.Errors);
    }

    [TestMethod]
    [DataRow("{\"enabled\":false,\"apiKey\":\"secret\"}")]
    [DataRow("{\"provider\":\"openai\",\"PROVIDER\":\"anthropic\"}")]
    [DataRow("{\"maxTotalSpend\":\"5\"}")]
    [DataRow("{\"schemaVersion\":\"1\"}")]
    [DataRow("null")]
    public void StrictConfigurationRejectsSecretFieldsAndAmbiguousDocuments(string json) =>
        Assert.Throws<JsonException>(() => TaskGraphDocumentJson.Parse<LiveEvaluationSettings>(json));

    [TestMethod]
    public void ConfigurationRoundTripPreservesDecimalCapWithoutEnablingDisabledSettings()
    {
        var settings = Complete() with { Enabled = false, MaxTotalSpend = 0.0001m };

        var parsed = TaskGraphDocumentJson.Parse<LiveEvaluationSettings>(TaskGraphDocumentJson.Serialize(settings));

        Assert.AreEqual(settings, parsed);
        Assert.IsTrue(parsed.Validate().IsValid);
        Assert.IsFalse(parsed.Validate().IsReady);
    }

    [TestMethod]
    [DataRow("pricing")]
    [DataRow("input-ceiling")]
    [DataRow("output-ceiling")]
    [DataRow("framing")]
    public void EnabledExecutionRequiresDeclaredRatesAndRequestCeilings(string missing)
    {
        var settings = missing switch
        {
            "pricing" => Complete() with { Pricing = null },
            "input-ceiling" => Complete() with { MaxInputTokensPerRequest = null },
            "output-ceiling" => Complete() with { MaxOutputTokensPerRequest = null },
            _ => Complete() with { InputFramingTokenAllowance = null }
        };
        Assert.IsFalse(settings.Validate().IsValid);
        Assert.ThrowsExactly<ArgumentException>(() => settings.RequireEnabled());
        Assert.IsTrue((settings with { Enabled = false }).Validate().IsValid);
    }

    [TestMethod]
    [DataRow("input-negative")]
    [DataRow("output-negative")]
    [DataRow("read-negative")]
    [DataRow("write-negative")]
    [DataRow("input-missing")]
    [DataRow("output-missing")]
    [DataRow("read-missing")]
    [DataRow("write-missing")]
    [DataRow("unbounded-rate")]
    [DataRow("all-zero")]
    [DataRow("source")]
    [DataRow("source-control")]
    [DataRow("source-padding")]
    [DataRow("asof-missing")]
    [DataRow("asof-not-utc")]
    public void SuppliedMalformedPriceSchedulesFailWithoutInventedFallbacks(string defect)
    {
        var pricing = Complete().Pricing!;
        pricing = defect switch
        {
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

        Assert.IsFalse((Complete() with { Pricing = pricing, Enabled = false }).Validate().IsValid);
    }

    [TestMethod]
    [DataRow("input")]
    [DataRow("output")]
    [DataRow("negative-framing")]
    [DataRow("framing-exhausts-input")]
    [DataRow("zero-trials")]
    [DataRow("too-many-trials")]
    public void InvalidRequestAndTrialBoundsFailEvenWhenDisabled(string defect)
    {
        var settings = defect switch
        {
            "input" => Complete() with { MaxInputTokensPerRequest = 0 },
            "output" => Complete() with { MaxOutputTokensPerRequest = 0 },
            "negative-framing" => Complete() with { InputFramingTokenAllowance = -1 },
            "framing-exhausts-input" => Complete() with { InputFramingTokenAllowance = 10000 },
            "zero-trials" => Complete() with { PairedTrials = 0 },
            _ => Complete() with { PairedTrials = 1001 }
        };
        Assert.IsFalse((settings with { Enabled = false }).Validate().IsValid);
    }

    [TestMethod]
    public void EnabledExecutionDoesNotSilentlyConvertCurrency()
    {
        Assert.IsFalse((Complete() with { Currency = "EUR" }).Validate().IsValid);
        Assert.IsTrue((Complete() with { Currency = "EUR", Enabled = false }).Validate().IsValid);
    }

    private static LiveEvaluationSettings Complete() => new()
    {
        Enabled = true,
        Provider = "openai",
        Model = "exact-model-id",
        MaxTotalSpend = 12.34m,
        Currency = "USD",
        MaxInputTokensPerRequest = 10000,
        MaxOutputTokensPerRequest = 1000,
        InputFramingTokenAllowance = 1024,
        Pricing = new()
        {
            InputPerMillion = 1m,
            OutputPerMillion = 2m,
            CacheReadPerMillion = 0.1m,
            CacheWritePerMillion = 1.25m,
            Source = "fixture supplied prices",
            AsOfUtc = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero)
        }
    };
}
