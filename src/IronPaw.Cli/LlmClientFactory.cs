using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Providers;

namespace IronPaw.Cli;

internal static class LlmClientFactory
{
    public static ILlmClient Build()
    {
        var sharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        var local = new RuleBasedLlmClient();
        var openAi = new OpenAiLlmClient(
            sharedHttpClient,
            () => Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        var ollama = new OpenAiLlmClient(
            sharedHttpClient,
            () => ResolveApiKey("OLLAMA_API_KEY", "ollama"),
            ResolveOpenAiCompatibleBaseUrl(
                Environment.GetEnvironmentVariable("OLLAMA_BASE_URL"),
                "http://localhost:11434"));
        var lmStudio = new OpenAiLlmClient(
            sharedHttpClient,
            () => ResolveApiKey("LMSTUDIO_API_KEY", "lm-studio"),
            ResolveOpenAiCompatibleBaseUrl(
                Environment.GetEnvironmentVariable("LMSTUDIO_BASE_URL"),
                "http://localhost:1234"));
        var anthropic = new AnthropicLlmClient(
            sharedHttpClient,
            () => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

        var providers = new Dictionary<string, ILlmClient>(StringComparer.OrdinalIgnoreCase)
        {
            ["local"] = local,
            ["openai"] = openAi,
            ["ollama"] = ollama,
            ["lmstudio"] = lmStudio,
            ["anthropic"] = anthropic
        };

        return new RoutedLlmClient(providers, local);
    }

    internal static string ResolveApiKey(string environmentVariableName, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariableName);
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
    }

    internal static string ResolveOpenAiCompatibleBaseUrl(string? configured, string fallbackBaseUrl)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configured)
            ? fallbackBaseUrl
            : configured.Trim();

        baseUrl = baseUrl.TrimEnd('/');
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^3];
        }

        return baseUrl;
    }
}
