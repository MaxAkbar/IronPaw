using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Providers;

namespace IronPaw.Cli;

/// <summary>Read-only graph commands do not load provider environment configuration.</summary>
internal sealed class LazyLlmClient(Func<ILlmClient> create) : ILlmClient
{
    private readonly Lazy<ILlmClient> _client = new(create);

    public IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ProviderDefaults.ProviderNames.Contains(request.Provider, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unsupported provider '{request.Provider}'.");
        return _client.Value.StreamAsync(request, ct);
    }
}
