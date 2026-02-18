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
        var routed = new RoutedLlmClient(new Dictionary<string, ILLMClient>
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
        const string responseBody = """
{
  "choices": [
    {
      "message": {
        "content": "hello from openai",
        "tool_calls": [
          {
            "id": "call_1",
            "type": "function",
            "function": {
              "name": "write_file",
              "arguments": "{\"path\":\"Plan.md\",\"content\":\"demo\"}"
            }
          }
        ]
      }
    }
  ],
  "usage": {
    "prompt_tokens": 12,
    "completion_tokens": 7
  }
}
""";

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
    public async Task AnthropicLlmClient_ParsesTextAndToolUse()
    {
        const string responseBody = """
{
  "content": [
    { "type": "text", "text": "hello from anthropic" },
    { "type": "tool_use", "id": "toolu_1", "name": "slugify", "input": { "text": "Hello Test" } }
  ],
  "usage": {
    "input_tokens": 10,
    "output_tokens": 3
  }
}
""";

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
        IReadOnlyList<LlmToolDefinition>? tools = null)
    {
        return new LlmRequest(
            RunId: new RunId("run-1"),
            SessionKey: new SessionKey("session-1"),
            Provider: provider,
            Model: model,
            SystemPrompt: "system",
            Messages: messages ?? [new ChatMessage(ChatRole.User, "hello")],
            Tools: tools ?? [],
            Timeout: TimeSpan.FromSeconds(10));
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

    private sealed class StubLlmClient : ILLMClient
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

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Handler should not be invoked.");
        }
    }
}
