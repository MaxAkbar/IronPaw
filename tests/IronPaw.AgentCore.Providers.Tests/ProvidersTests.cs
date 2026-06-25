using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Providers;

namespace IronPaw.AgentCore.Providers.Tests;

public sealed class ProvidersTests
{
    [Fact]
    public void LlmClientFactory_ResolveOpenAiCompatibleOptions_ReturnsProviderSpecificDefaults()
    {
        using var _ = BeginLlmBudgetEnvironmentScope();

        var openAi = LlmClientFactory.ResolveOpenAiCompatibleOptions("openai");
        var ollama = LlmClientFactory.ResolveOpenAiCompatibleOptions("ollama");
        var lmStudio = LlmClientFactory.ResolveOpenAiCompatibleOptions("lmstudio");
        var nonCompatible = LlmClientFactory.ResolveOpenAiCompatibleOptions("anthropic");

        Assert.NotNull(openAi);
        Assert.Equal(128_000, openAi!.ContextWindowTokens);
        Assert.Equal(2_048, openAi.SafetyMarginTokens);
        Assert.Equal(256, openAi.MinimumOutputTokensPerRequest);
        Assert.Equal(2, openAi.MaxAutoContinuationPasses);

        Assert.NotNull(ollama);
        Assert.Equal(8_192, ollama!.ContextWindowTokens);
        Assert.Equal(768, ollama.SafetyMarginTokens);
        Assert.Equal(128, ollama.MinimumOutputTokensPerRequest);
        Assert.Equal(1, ollama.MaxAutoContinuationPasses);

        Assert.NotNull(lmStudio);
        Assert.Equal(8_192, lmStudio!.ContextWindowTokens);
        Assert.Null(nonCompatible);
    }

    [Fact]
    public void LlmClientFactory_ResolveOpenAiCompatibleOptions_RuntimeOverridesBeatEnvironment()
    {
        using var _ = BeginLlmBudgetEnvironmentScope(
            contextWindow: "4096",
            safetyMargin: "400",
            minOutput: "64",
            maxAutoContinuationPasses: "1");

        var fromEnvironmentOnly = LlmClientFactory.ResolveOpenAiCompatibleOptions("openai");
        var fromRuntimeOverride = LlmClientFactory.ResolveOpenAiCompatibleOptions(
            "openai",
            new OpenAiClientOptionOverrides(
                ContextWindowTokens: 32768,
                SafetyMarginTokens: 1024,
                MinimumOutputTokensPerRequest: 512,
                MaxAutoContinuationPasses: 3));

        Assert.NotNull(fromEnvironmentOnly);
        Assert.Equal(4096, fromEnvironmentOnly!.ContextWindowTokens);
        Assert.Equal(400, fromEnvironmentOnly.SafetyMarginTokens);
        Assert.Equal(64, fromEnvironmentOnly.MinimumOutputTokensPerRequest);
        Assert.Equal(1, fromEnvironmentOnly.MaxAutoContinuationPasses);

        Assert.NotNull(fromRuntimeOverride);
        Assert.Equal(32768, fromRuntimeOverride!.ContextWindowTokens);
        Assert.Equal(1024, fromRuntimeOverride.SafetyMarginTokens);
        Assert.Equal(512, fromRuntimeOverride.MinimumOutputTokensPerRequest);
        Assert.Equal(3, fromRuntimeOverride.MaxAutoContinuationPasses);
    }

    [Fact]
    public async Task RuleBasedLlmClient_NoMessages_ReturnsNoMessagesDelta()
    {
        var client = new RuleBasedLlmClient();
        var request = CreateRequest(messages: []);

        var deltas = await CollectAsync(client.StreamAsync(request, CancellationToken.None));

        var text = Assert.IsType<AssistantTextDelta>(deltas[0]);
        Assert.Contains("No messages were provided", text.Text, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<CompletedDelta>(deltas[^1]);
    }

    [Fact]
    public async Task RuleBasedLlmClient_PlanPrompt_EmitsWriteFileToolCall()
    {
        var client = new RuleBasedLlmClient();
        var request = CreateRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "Create a plan and write it to Strategy.md")
            ],
            tools:
            [
                new LlmToolDefinition(
                    "write_file",
                    "writes a file",
                    JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }))
            ]);

        var deltas = await CollectAsync(client.StreamAsync(request, CancellationToken.None));
        var toolCall = Assert.IsType<ToolCallDelta>(deltas.Single(delta => delta is ToolCallDelta));

        Assert.Equal("write_file", toolCall.ToolName);
        Assert.Equal("Strategy.md", toolCall.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task RoutedLlmClient_UsesFallbackForUnknownProvider()
    {
        var primary = new StubLlmClient(new AssistantTextDelta("primary"));
        var fallback = new StubLlmClient(new AssistantTextDelta("fallback"));
        var routed = new RoutedLlmClient(new Dictionary<string, ILlmClient>
        {
            ["known"] = primary
        }, fallback);

        var request = CreateRequest(provider: "unknown", messages: [new ChatMessage(ChatRole.User, "x")]);
        var deltas = await CollectAsync(routed.StreamAsync(request, CancellationToken.None));

        Assert.Equal(0, primary.Calls);
        Assert.Equal(1, fallback.Calls);
        Assert.Equal("fallback", Assert.IsType<AssistantTextDelta>(deltas[0]).Text);
    }

    [Fact]
    public async Task OpenAiLlmClient_NoApiKey_ReturnsErrorDelta()
    {
        var client = new OpenAiLlmClient(
            new HttpClient(new ThrowingHandler()),
            apiKeyResolver: () => null);

        var request = CreateRequest(provider: "openai", model: "gpt-4o-mini", messages: [new ChatMessage(ChatRole.User, "hi")]);
        var deltas = await CollectAsync(client.StreamAsync(request, CancellationToken.None));

        var error = Assert.IsType<LlmErrorDelta>(Assert.Single(deltas));
        Assert.Contains("OPENAI_API_KEY", error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiLlmClient_ParsesAssistantTextToolCallAndUsage()
    {
        // SSE format: each line is "data: <json>" and terminated with "data: [DONE]"
        const string responseBody =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello from openai\"},\"finish_reason\":null}]}\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"write_file\",\"arguments\":\"\"}}]},\"finish_reason\":null}]}\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"path\\\":\\\"Plan.md\\\",\\\"content\\\":\\\"demo\\\"}\"}}]},\"finish_reason\":null}]}\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n" +
            "data: {\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":7}}\n" +
            "data: [DONE]\n";

        var client = new OpenAiLlmClient(
            new HttpClient(new StaticJsonHandler(HttpStatusCode.OK, responseBody)),
            apiKeyResolver: () => "test-key");

        var request = CreateRequest(provider: "openai", model: "gpt-4o-mini", messages: [new ChatMessage(ChatRole.User, "hi")]);
        var deltas = await CollectAsync(client.StreamAsync(request, CancellationToken.None));

        Assert.Contains(deltas, delta => delta is AssistantTextDelta text && text.Text.Contains("openai", StringComparison.OrdinalIgnoreCase));
        var toolCall = Assert.IsType<ToolCallDelta>(deltas.Single(delta => delta is ToolCallDelta));
        Assert.Equal("write_file", toolCall.ToolName);
        Assert.Equal("Plan.md", toolCall.Arguments.GetProperty("path").GetString());

        var usage = Assert.IsType<UsageDelta>(deltas.Single(delta => delta is UsageDelta));
        Assert.Equal(12, usage.InputTokens);
        Assert.Equal(7, usage.OutputTokens);
        Assert.IsType<CompletedDelta>(deltas[^1]);
    }

    [Fact]
    public async Task OpenAiLlmClient_LengthFinishReason_AutoContinuesStreamingText()
    {
        const string firstResponseBody =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Part one \"},\"finish_reason\":null}]}\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"length\"}]}\n" +
            "data: {\"usage\":{\"prompt_tokens\":21,\"completion_tokens\":6}}\n" +
            "data: [DONE]\n";
        const string secondResponseBody =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Part two.\"},\"finish_reason\":null}]}\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n" +
            "data: {\"usage\":{\"prompt_tokens\":28,\"completion_tokens\":5}}\n" +
            "data: [DONE]\n";

        var handler = new RecordingSequenceHandler(
            (HttpStatusCode.OK, firstResponseBody),
            (HttpStatusCode.OK, secondResponseBody));
        var client = new OpenAiLlmClient(
            new HttpClient(handler),
            apiKeyResolver: () => "test-key",
            options: new OpenAiClientOptions(
                ContextWindowTokens: 8_192,
                SafetyMarginTokens: 512,
                MinimumOutputTokensPerRequest: 64,
                MaxAutoContinuationPasses: 2));

        var request = CreateRequest(
            provider: "openai",
            model: "gpt-4o-mini",
            messages: [new ChatMessage(ChatRole.User, "Tell me a long answer.")],
            maxOutputTokens: 256);
        var deltas = await CollectAsync(client.StreamAsync(request, CancellationToken.None));

        var combinedText = string.Concat(deltas.OfType<AssistantTextDelta>().Select(delta => delta.Text));
        Assert.Equal("Part one Part two.", combinedText);
        Assert.Equal(2, handler.RequestBodies.Count);

        using var secondRequestDocument = JsonDocument.Parse(handler.RequestBodies[1]);
        var messages = secondRequestDocument.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("assistant", messages[^2].GetProperty("role").GetString());
        Assert.Equal("Part one ", messages[^2].GetProperty("content").GetString());
        Assert.Equal("user", messages[^1].GetProperty("role").GetString());
        Assert.Contains(
            "Continue exactly where you left off",
            messages[^1].GetProperty("content").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiLlmClient_NoOutputBudget_ReturnsErrorWithoutCallingApi()
    {
        var handler = new RecordingSequenceHandler();
        var client = new OpenAiLlmClient(
            new HttpClient(handler),
            apiKeyResolver: () => "test-key",
            options: new OpenAiClientOptions(
                ContextWindowTokens: 1_024,
                SafetyMarginTokens: 256,
                MinimumOutputTokensPerRequest: 64,
                MaxAutoContinuationPasses: 0));

        var oversizedPrompt = new string('x', 10_000);
        var request = CreateRequest(
            provider: "openai",
            model: "gpt-4o-mini",
            messages: [new ChatMessage(ChatRole.User, oversizedPrompt)],
            maxOutputTokens: 512);
        var deltas = await CollectAsync(client.StreamAsync(request, CancellationToken.None));

        var error = Assert.IsType<LlmErrorDelta>(Assert.Single(deltas));
        Assert.Contains("No output token budget available", error.Error, StringComparison.Ordinal);
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task AnthropicLlmClient_ParsesTextAndToolUse()
    {
        // SSE format matching Anthropic's event stream protocol
        const string responseBody =
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10,\"output_tokens\":0}}}\n" +
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\"}}\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hello from anthropic\"}}\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":0}\n" +
            "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"slugify\"}}\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"text\\\":\\\"Hello Test\\\"}\"}}\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":1}\n" +
            "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":3}}\n" +
            "data: {\"type\":\"message_stop\"}\n";

        var client = new AnthropicLlmClient(
            new HttpClient(new StaticJsonHandler(HttpStatusCode.OK, responseBody)),
            apiKeyResolver: () => "anthropic-key");

        var request = CreateRequest(provider: "anthropic", model: "claude-3-5-haiku-latest", messages: [new ChatMessage(ChatRole.User, "hi")]);
        var deltas = await CollectAsync(client.StreamAsync(request, CancellationToken.None));

        Assert.Contains(deltas, delta => delta is AssistantTextDelta text && text.Text.Contains("anthropic", StringComparison.OrdinalIgnoreCase));
        var toolCall = Assert.IsType<ToolCallDelta>(deltas.Single(delta => delta is ToolCallDelta));
        Assert.Equal("slugify", toolCall.ToolName);
        Assert.Equal("Hello Test", toolCall.Arguments.GetProperty("text").GetString());

        var usage = Assert.IsType<UsageDelta>(deltas.Single(delta => delta is UsageDelta));
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(3, usage.OutputTokens);
        Assert.IsType<CompletedDelta>(deltas[^1]);
    }

    [Fact]
    public async Task AnthropicLlmClient_NoApiKey_ReturnsErrorDelta()
    {
        var client = new AnthropicLlmClient(
            new HttpClient(new ThrowingHandler()),
            apiKeyResolver: () => string.Empty);

        var request = CreateRequest(provider: "anthropic", model: "claude-3-5-haiku-latest", messages: [new ChatMessage(ChatRole.User, "hi")]);
        var deltas = await CollectAsync(client.StreamAsync(request, CancellationToken.None));

        var error = Assert.IsType<LlmErrorDelta>(Assert.Single(deltas));
        Assert.Contains("ANTHROPIC_API_KEY", error.Error, StringComparison.Ordinal);
    }

    private static LlmRequest CreateRequest(
        string provider = "local",
        string model = "rule-based-v1",
        IReadOnlyList<ChatMessage>? messages = null,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        int maxOutputTokens = 4096)
    {
        return new LlmRequest(
            RunId: new RunId("run-1"),
            SessionKey: new SessionKey("session-1"),
            Provider: provider,
            Model: model,
            SystemPrompt: "system",
            Messages: messages ?? [new ChatMessage(ChatRole.User, "hello")],
            Tools: tools ?? [],
            Timeout: TimeSpan.FromSeconds(10),
            MaxOutputTokens: maxOutputTokens);
    }

    private static async Task<List<LlmDelta>> CollectAsync(IAsyncEnumerable<LlmDelta> stream)
    {
        var output = new List<LlmDelta>();
        await foreach (var delta in stream)
        {
            output.Add(delta);
        }

        return output;
    }

    private sealed class StubLlmClient : ILlmClient
    {
        private readonly IReadOnlyList<LlmDelta> _deltas;

        public StubLlmClient(params LlmDelta[] deltas)
        {
            _deltas = deltas;
        }

        public int Calls { get; private set; }

        public async IAsyncEnumerable<LlmDelta> StreamAsync(
            LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            foreach (var delta in _deltas)
            {
                yield return delta;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class StaticJsonHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _json;

        public StaticJsonHandler(HttpStatusCode statusCode, string json)
        {
            _statusCode = statusCode;
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }

    private sealed class RecordingSequenceHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode StatusCode, string Body)> _responses;

        public RecordingSequenceHandler(params (HttpStatusCode StatusCode, string Body)[] responses)
        {
            _responses = new Queue<(HttpStatusCode StatusCode, string Body)>(responses);
        }

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No queued HTTP responses were configured.");
            }

            var (statusCode, body) = _responses.Dequeue();
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Handler should not be invoked.");
        }
    }

    private static IDisposable BeginLlmBudgetEnvironmentScope(
        string? contextWindow = null,
        string? safetyMargin = null,
        string? minOutput = null,
        string? maxAutoContinuationPasses = null)
    {
        return new EnvironmentVariableScope(
            ("IRONPAW_LLM_CONTEXT_WINDOW_TOKENS", contextWindow),
            ("IRONPAW_LLM_SAFETY_MARGIN_TOKENS", safetyMargin),
            ("IRONPAW_LLM_MIN_OUTPUT_TOKENS_PER_REQUEST", minOutput),
            ("IRONPAW_LLM_MAX_AUTO_CONTINUATION_PASSES", maxAutoContinuationPasses));
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string?> _previousValues;

        public EnvironmentVariableScope(params (string Name, string? Value)[] updates)
        {
            var previousValues = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (name, value) in updates)
            {
                previousValues[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }

            _previousValues = previousValues;
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previousValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
