namespace IronPaw.Cli;

internal static class CliDefaults
{
    public const string QuitOption = "quit";

    public static readonly IReadOnlyList<string> ProviderNames =
    [
        "local",
        "openai",
        "ollama",
        "lmstudio",
        "anthropic"
    ];

    public static readonly IReadOnlyList<string> ProviderChoicesWithQuit =
    [
        "local",
        "openai",
        "ollama",
        "lmstudio",
        "anthropic",
        QuitOption
    ];

    public static readonly IReadOnlyDictionary<string, string> ProviderDefaultModels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["local"] = "rule-based-v1",
            ["openai"] = "gpt-4o-mini",
            ["ollama"] = "llama3.2",
            ["lmstudio"] = "local-model",
            ["anthropic"] = "claude-3-5-haiku-latest"
        };
}
