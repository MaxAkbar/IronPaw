using IronPaw.AgentCore.Abstractions;
using IronPaw.Cli;

namespace IronPaw.Cli.Tests;

public sealed class LazyLlmClientTests
{
    [Theory]
    [InlineData("future-provider")]
    [InlineData("")]
    [InlineData(" local ")]
    public void UnknownProvider_IsRejectedBeforeLoadingProviderConfiguration(string provider)
    {
        var calls = 0;
        var client = new LazyLlmClient(() => { calls++; throw new InvalidOperationException("Factory must stay deferred"); });
        var request = Request(provider);
        Assert.Throws<NotSupportedException>(() => client.StreamAsync(request, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("LOCAL")]
    public void SupportedProvider_IsCaseInsensitiveAndDefersFactoryUntilStream(string provider)
    {
        var calls = 0;
        var client = new LazyLlmClient(() => { calls++; throw new InvalidOperationException("Configured factory was requested"); });
        Assert.Equal(0, calls);
        Assert.Throws<InvalidOperationException>(() => client.StreamAsync(Request(provider), CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Throws<NotSupportedException>(() => client.StreamAsync(Request("future-provider"), CancellationToken.None));
        Assert.Equal(1, calls);
    }

    private static LlmRequest Request(string provider) => new(RunId.New(), new SessionKey("test"), provider,
        "local", "test", [], [], TimeSpan.FromSeconds(5));
}
