namespace IronPaw.AgentCore.Providers;

public static class ProviderDefaults
{
    public static readonly IReadOnlyList<string> ProviderNames =
    [
        "local",
        "openai",
        "ollama",
        "lmstudio",
        "anthropic"
    ];

    public static readonly IReadOnlyDictionary<string, string> DefaultModels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["local"] = "rule-based-v1",
            ["openai"] = "gpt-4o-mini",
            ["ollama"] = "llama3.2",
            ["lmstudio"] = "local-model",
            ["anthropic"] = "claude-3-5-haiku-latest"
        };
}
