namespace IronPaw.AgentCore.Providers.Tests;

[TestClass]
public sealed class LlmClientFactoryCompatibilityTests
{
    [TestMethod]
    public void ExistingCompiledCallersRetainTheirFactoryEntryPoints()
    {
        Assert.IsNotNull(typeof(LlmClientFactory).GetMethod(nameof(LlmClientFactory.Build), [typeof(OpenAiClientOptionOverrides)]));
        Assert.IsNotNull(typeof(LlmClientFactory).GetMethod(nameof(LlmClientFactory.ResolveOpenAiCompatibleOptions),
            [typeof(string), typeof(OpenAiClientOptionOverrides)]));
    }

    [TestMethod]
    public void FrozenSettingsCanResolveWithoutRereadingEnvironment()
    {
        var options = LlmClientFactory.ResolveOpenAiCompatibleOptions("openai",
            new(ContextWindowTokens: 64000, SafetyMarginTokens: 1000, MinimumOutputTokensPerRequest: 200, MaxAutoContinuationPasses: 0),
            readEnvironmentOverrides: false);
        Assert.AreEqual(64000, options!.ContextWindowTokens);
        Assert.AreEqual(1000, options.SafetyMarginTokens);
        Assert.AreEqual(0, options.MaxAutoContinuationPasses);
    }
}
