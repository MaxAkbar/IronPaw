using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Providers;

public sealed class RoutedLlmClient : ILLMClient
{
    private readonly IReadOnlyDictionary<string, ILLMClient> _providers;
    private readonly ILLMClient _fallback;

    public RoutedLlmClient(IReadOnlyDictionary<string, ILLMClient> providers, ILLMClient fallback)
    {
        _providers = new Dictionary<string, ILLMClient>(providers, StringComparer.OrdinalIgnoreCase);
        _fallback = fallback;
    }

    public IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, CancellationToken ct)
    {
        if (_providers.TryGetValue(request.Provider, out var providerClient))
        {
            return providerClient.StreamAsync(request, ct);
        }

        return _fallback.StreamAsync(request, ct);
    }
}
