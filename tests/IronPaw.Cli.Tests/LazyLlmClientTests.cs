using IronPaw.AgentCore.Abstractions;
using IronPaw.Cli;

namespace IronPaw.Cli.Tests;

[TestClass]
public sealed class LazyLlmClientTests
{
    [TestMethod]
    [DataRow("future-provider")]
    [DataRow("")]
    [DataRow(" local ")]
    public void UnknownProvider_IsRejectedBeforeLoadingProviderConfiguration(string provider)
    {
        var calls = 0;
        var client = new LazyLlmClient(() => { calls++; throw new InvalidOperationException("Factory must stay deferred"); });
        var request = Request(provider);
        Assert.ThrowsExactly<NotSupportedException>(() => client.StreamAsync(request, CancellationToken.None));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow("local")]
    [DataRow("LOCAL")]
    public void SupportedProvider_IsCaseInsensitiveAndDefersFactoryUntilStream(string provider)
    {
        var calls = 0;
        var client = new LazyLlmClient(() => { calls++; throw new InvalidOperationException("Configured factory was requested"); });
        Assert.AreEqual(0, calls);
        Assert.ThrowsExactly<InvalidOperationException>(() => client.StreamAsync(Request(provider), CancellationToken.None));
        Assert.AreEqual(1, calls);
        Assert.ThrowsExactly<NotSupportedException>(() => client.StreamAsync(Request("future-provider"), CancellationToken.None));
        Assert.AreEqual(1, calls);
    }

    private static LlmRequest Request(string provider) => new(RunId.New(), new SessionKey("test"), provider,
        "local", "test", [], [], TimeSpan.FromSeconds(5));
}
