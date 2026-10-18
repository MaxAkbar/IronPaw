using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Providers;

namespace IronPaw.Cli;

/// <summary>One exact provider/model, no routing fallback, no hidden continuation or installed add-ins.</summary>
internal sealed class EvaluationProviderClient : ILlmClient, IDisposable
{
    private readonly LiveEvaluationSettings _settings;
    private readonly Lazy<ILlmClient> _client;
    private HttpClient? _http;

    public EvaluationProviderClient(LiveEvaluationSettings settings, Func<ILlmClient>? create = null)
    {
        _settings = settings.RequireEnabled();
        _client = new Lazy<ILlmClient>(create ?? Create);
    }

    public IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Provider, _settings.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.Model, _settings.Model, StringComparison.Ordinal))
            throw new InvalidOperationException("A live comparison cannot change its frozen provider or model.");
        if (request.MaxOutputTokens <= 0 || request.MaxOutputTokens > _settings.MaxOutputTokensPerRequest)
            throw new InvalidOperationException("The request exceeds the campaign output-token ceiling.");
        return _client.Value.StreamAsync(request with { MaxAutoContinuationPasses = 0 }, ct);
    }

    private ILlmClient Create()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // Endpoints are fixed so environment changes cannot silently redirect a resumed priced campaign.
        var options = new OpenAiClientOptions(MaxAutoContinuationPasses: 0);
        return _settings.Provider switch
        {
            "openai" => new OpenAiLlmClient(_http, () => Environment.GetEnvironmentVariable("OPENAI_API_KEY"), options: options),
            "anthropic" => new AnthropicLlmClient(_http, () => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
            "ollama" => new OpenAiLlmClient(_http, () => Key("OLLAMA_API_KEY", "ollama"), "http://localhost:11434", options),
            "lmstudio" => new OpenAiLlmClient(_http, () => Key("LMSTUDIO_API_KEY", "lm-studio"), "http://localhost:1234", options),
            _ => throw new NotSupportedException("Unsupported live comparison provider.")
        };
    }

    private static string Key(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    public void Dispose() => _http?.Dispose();
}
