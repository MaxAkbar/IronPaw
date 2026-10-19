namespace IronPaw.AgentCore.Providers.Tests;

public sealed class LlmClientFactoryCompatibilityTests
{
    [Fact]
    public void ExistingCompiledCallersRetainTheirFactoryEntryPoints()
    {
        Assert.NotNull(typeof(LlmClientFactory).GetMethod(nameof(LlmClientFactory.Build), [typeof(OpenAiClientOptionOverrides)]));
        Assert.NotNull(typeof(LlmClientFactory).GetMethod(nameof(LlmClientFactory.ResolveOpenAiCompatibleOptions),
            [typeof(string), typeof(OpenAiClientOptionOverrides)]));
    }

    [Fact]
    public void FrozenSettingsCanResolveWithoutRereadingEnvironment()
    {
        var options = LlmClientFactory.ResolveOpenAiCompatibleOptions("openai",
            new(ContextWindowTokens: 64000, SafetyMarginTokens: 1000, MinimumOutputTokensPerRequest: 200, MaxAutoContinuationPasses: 0),
            readEnvironmentOverrides: false);
        Assert.Equal(64000, options!.ContextWindowTokens);
        Assert.Equal(1000, options.SafetyMarginTokens);
        Assert.Equal(0, options.MaxAutoContinuationPasses);
    }
}
