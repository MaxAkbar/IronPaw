using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Providers;

public sealed class RoutedLlmClient : ILlmClient
{
    private readonly IReadOnlyDictionary<string, ILlmClient> _providers;
    private readonly ILlmClient _fallback;

    public RoutedLlmClient(IReadOnlyDictionary<string, ILlmClient> providers, ILlmClient fallback)
    {
        _providers = new Dictionary<string, ILlmClient>(providers, StringComparer.OrdinalIgnoreCase);
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
