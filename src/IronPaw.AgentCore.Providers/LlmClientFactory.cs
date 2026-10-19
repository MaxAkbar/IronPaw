using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Providers;

public sealed record OpenAiClientOptionOverrides(
    int? ContextWindowTokens = null,
    int? SafetyMarginTokens = null,
    int? MinimumOutputTokensPerRequest = null,
    int? MaxAutoContinuationPasses = null);

public static class LlmClientFactory
{
    private const string OpenAiProvider = "openai";
    private const string OllamaProvider = "ollama";
    private const string LmStudioProvider = "lmstudio";

    public static ILlmClient Build(OpenAiClientOptionOverrides? runtimeOverrides = null) =>
        Build(runtimeOverrides, readEnvironmentOverrides: true);

    public static ILlmClient Build(OpenAiClientOptionOverrides? runtimeOverrides, bool readEnvironmentOverrides)
    {
        var environmentOverrides = readEnvironmentOverrides ? LoadOpenAiClientOptionOverridesFromEnvironment() : new OpenAiClientOptionOverrides();
        var effectiveOverrides = MergeOverrides(environmentOverrides, runtimeOverrides);

        var sharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        var openAiOptions = ResolveOpenAiCompatibleOptionsCore(OpenAiProvider, effectiveOverrides)
            ?? GetDefaultOpenAiOptions();
        var localOpenAiCompatibleOptions = ResolveOpenAiCompatibleOptionsCore(OllamaProvider, effectiveOverrides)
            ?? GetDefaultLocalOpenAiCompatibleOptions();

        var local = new RuleBasedLlmClient();
        var openAi = new OpenAiLlmClient(
            sharedHttpClient,
            () => Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            options: openAiOptions);
        var ollama = new OpenAiLlmClient(
            sharedHttpClient,
            () => ResolveApiKey("OLLAMA_API_KEY", "ollama"),
            ResolveOpenAiCompatibleBaseUrl(
                Environment.GetEnvironmentVariable("OLLAMA_BASE_URL"),
                "http://localhost:11434"),
            options: localOpenAiCompatibleOptions);
        var lmStudio = new OpenAiLlmClient(
            sharedHttpClient,
            () => ResolveApiKey("LMSTUDIO_API_KEY", "lm-studio"),
            ResolveOpenAiCompatibleBaseUrl(
                Environment.GetEnvironmentVariable("LMSTUDIO_BASE_URL"),
                "http://localhost:1234"),
            options: localOpenAiCompatibleOptions);
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

    public static OpenAiClientOptions? ResolveOpenAiCompatibleOptions(
        string provider,
        OpenAiClientOptionOverrides? runtimeOverrides = null) =>
        ResolveOpenAiCompatibleOptions(provider, runtimeOverrides, readEnvironmentOverrides: true);

    public static OpenAiClientOptions? ResolveOpenAiCompatibleOptions(
        string provider, OpenAiClientOptionOverrides? runtimeOverrides, bool readEnvironmentOverrides)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        var environmentOverrides = readEnvironmentOverrides ? LoadOpenAiClientOptionOverridesFromEnvironment() : new OpenAiClientOptionOverrides();
        var effectiveOverrides = MergeOverrides(environmentOverrides, runtimeOverrides);
        return ResolveOpenAiCompatibleOptionsCore(provider, effectiveOverrides);
    }

    private static OpenAiClientOptionOverrides LoadOpenAiClientOptionOverridesFromEnvironment()
    {
        return new OpenAiClientOptionOverrides(
            ContextWindowTokens: ParseOptionalIntEnvironmentVariable(
                "IRONPAW_LLM_CONTEXT_WINDOW_TOKENS",
                minInclusive: 1),
            SafetyMarginTokens: ParseOptionalIntEnvironmentVariable(
                "IRONPAW_LLM_SAFETY_MARGIN_TOKENS",
                minInclusive: 0),
            MinimumOutputTokensPerRequest: ParseOptionalIntEnvironmentVariable(
                "IRONPAW_LLM_MIN_OUTPUT_TOKENS_PER_REQUEST",
                minInclusive: 1),
            MaxAutoContinuationPasses: ParseOptionalIntEnvironmentVariable(
                "IRONPAW_LLM_MAX_AUTO_CONTINUATION_PASSES",
                minInclusive: 0));
    }

    private static OpenAiClientOptionOverrides MergeOverrides(
        OpenAiClientOptionOverrides baseOverrides,
        OpenAiClientOptionOverrides? runtimeOverrides)
    {
        if (runtimeOverrides is null)
        {
            return baseOverrides;
        }

        return new OpenAiClientOptionOverrides(
            ContextWindowTokens: runtimeOverrides.ContextWindowTokens ?? baseOverrides.ContextWindowTokens,
            SafetyMarginTokens: runtimeOverrides.SafetyMarginTokens ?? baseOverrides.SafetyMarginTokens,
            MinimumOutputTokensPerRequest: runtimeOverrides.MinimumOutputTokensPerRequest ?? baseOverrides.MinimumOutputTokensPerRequest,
            MaxAutoContinuationPasses: runtimeOverrides.MaxAutoContinuationPasses ?? baseOverrides.MaxAutoContinuationPasses);
    }

    private static OpenAiClientOptions? ResolveOpenAiCompatibleOptionsCore(
        string provider,
        OpenAiClientOptionOverrides effectiveOverrides)
    {
        if (provider.Equals(OpenAiProvider, StringComparison.OrdinalIgnoreCase))
        {
            return ApplyOverrides(GetDefaultOpenAiOptions(), effectiveOverrides);
        }

        if (provider.Equals(OllamaProvider, StringComparison.OrdinalIgnoreCase) ||
            provider.Equals(LmStudioProvider, StringComparison.OrdinalIgnoreCase))
        {
            return ApplyOverrides(GetDefaultLocalOpenAiCompatibleOptions(), effectiveOverrides);
        }

        return null;
    }

    private static OpenAiClientOptions GetDefaultOpenAiOptions()
    {
        return new OpenAiClientOptions(
            ContextWindowTokens: 128_000,
            SafetyMarginTokens: 2_048,
            MinimumOutputTokensPerRequest: 256,
            MaxAutoContinuationPasses: 2);
    }

    private static OpenAiClientOptions GetDefaultLocalOpenAiCompatibleOptions()
    {
        return new OpenAiClientOptions(
            ContextWindowTokens: 8_192,
            SafetyMarginTokens: 768,
            MinimumOutputTokensPerRequest: 128,
            MaxAutoContinuationPasses: 1);
    }

    private static OpenAiClientOptions ApplyOverrides(
        OpenAiClientOptions defaults,
        OpenAiClientOptionOverrides overrides)
    {
        return (defaults with
        {
            ContextWindowTokens = overrides.ContextWindowTokens ?? defaults.ContextWindowTokens,
            SafetyMarginTokens = overrides.SafetyMarginTokens ?? defaults.SafetyMarginTokens,
            MinimumOutputTokensPerRequest = overrides.MinimumOutputTokensPerRequest ?? defaults.MinimumOutputTokensPerRequest,
            MaxAutoContinuationPasses = overrides.MaxAutoContinuationPasses ?? defaults.MaxAutoContinuationPasses
        }).Normalize();
    }

    private static int? ParseOptionalIntEnvironmentVariable(string name, int minInclusive)
    {
        var rawValue = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        if (!int.TryParse(rawValue.Trim(), out var parsedValue) || parsedValue < minInclusive)
        {
            throw new ArgumentException(
                $"Invalid {name} value '{rawValue}'. Expected an integer >= {minInclusive}.");
        }

        return parsedValue;
    }

    public static string ResolveApiKey(string environmentVariableName, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariableName);
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
    }

    public static string ResolveOpenAiCompatibleBaseUrl(string? configured, string fallbackBaseUrl)
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
