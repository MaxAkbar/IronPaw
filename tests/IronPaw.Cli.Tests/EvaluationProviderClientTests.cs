using System.Runtime.CompilerServices;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.Cli;

namespace IronPaw.Cli.Tests;

[TestClass]
public sealed class EvaluationProviderClientTests
{
    [TestMethod]
    public void DisabledOrIncompleteSettingsRejectBeforeFactory()
    {
        var calls = 0;
        Assert.ThrowsExactly<InvalidOperationException>(() => new EvaluationProviderClient(new(), () => { calls++; throw new Exception(); }));
        Assert.ThrowsExactly<ArgumentException>(() => new EvaluationProviderClient(new() { Enabled = true }, () => { calls++; throw new Exception(); }));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow("local", "exact-model")]
    [DataRow("anthropic", "exact-model")]
    [DataRow("openai", "changed-model")]
    [DataRow("openai", "EXACT-MODEL")]
    public void ChangedBindingRejectsBeforeFactory(string provider, string model)
    {
        var calls = 0;
        using var client = new EvaluationProviderClient(Settings(), () => { calls++; throw new Exception(); });
        Assert.ThrowsExactly<InvalidOperationException>(() => client.StreamAsync(Request(provider, model), CancellationToken.None));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1001)]
    public void InvalidOutputCeilingRejectsBeforeFactory(int outputTokens)
    {
        var calls = 0;
        using var client = new EvaluationProviderClient(Settings(), () => { calls++; throw new Exception(); });
        Assert.ThrowsExactly<InvalidOperationException>(() => client.StreamAsync(Request() with { MaxOutputTokens = outputTokens }, CancellationToken.None));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task ExactBindingDisablesHiddenContinuationAndReusesOneLazyClient()
    {
        var calls = 0;
        var observed = new RecordingClient();
        using var client = new EvaluationProviderClient(Settings(), () => { calls++; return observed; });
        Assert.AreEqual(0, calls);
        await foreach (var _ in client.StreamAsync(Request("OPENAI") with { MaxAutoContinuationPasses = 8 }, CancellationToken.None)) { }
        await foreach (var _ in client.StreamAsync(Request(), CancellationToken.None)) { }
        Assert.AreEqual(1, calls);
        Assert.AreEqual(2, observed.Requests.Count);
        foreach (var request in observed.Requests)
        {
            Assert.AreEqual(0, request.MaxAutoContinuationPasses);
        }
    }

    private static LlmRequest Request(string provider = "openai", string model = "exact-model") =>
        new(RunId.New(), new SessionKey("evaluation-fixture"), provider, model, "fixture", [], [], TimeSpan.FromSeconds(5), 1000);

    internal static LiveEvaluationSettings Settings() => new()
    {
        Enabled = true,
        Provider = "openai",
        Model = "exact-model",
        MaxTotalSpend = 1,
        MaxInputTokensPerRequest = 10000,
        MaxOutputTokensPerRequest = 1000,
        InputFramingTokenAllowance = 1024,
        Pricing = new()
        {
            InputPerMillion = 1,
            OutputPerMillion = 2,
            CacheReadPerMillion = 0.1m,
            CacheWritePerMillion = 1.25m,
            Source = "controlled fixture prices",
            AsOfUtc = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero)
        }
    };

    private sealed class RecordingClient : ILlmClient
    {
        public List<LlmRequest> Requests { get; } = [];
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield break;
        }
    }
}
