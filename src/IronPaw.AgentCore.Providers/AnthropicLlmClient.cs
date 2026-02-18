using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Providers;

public sealed class AnthropicLlmClient : ILLMClient
{
    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyResolver;
    private readonly Uri _messagesEndpoint;

    public AnthropicLlmClient(
        HttpClient httpClient,
        Func<string?> apiKeyResolver,
        string baseUrl = "https://api.anthropic.com")
    {
        _httpClient = httpClient;
        _apiKeyResolver = apiKeyResolver;
        _messagesEndpoint = new Uri($"{baseUrl.TrimEnd('/')}/v1/messages");
    }

    public async IAsyncEnumerable<LlmDelta> StreamAsync(
        LlmRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var apiKey = _apiKeyResolver();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield return new LlmErrorDelta("ANTHROPIC_API_KEY is not configured.");
            yield break;
        }

        var payload = BuildPayload(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _messagesEndpoint);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Headers.Add("x-api-key", apiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        var rawBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            yield return new LlmErrorDelta(
                $"Anthropic request failed ({(int)response.StatusCode}): {ExtractErrorMessage(rawBody)}");
            yield break;
        }

        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;

        if (root.TryGetProperty("content", out var contentElement) &&
            contentElement.ValueKind is JsonValueKind.Array)
        {
            foreach (var block in contentElement.EnumerateArray())
            {
                foreach (var delta in ParseContentBlock(block))
                {
                    yield return delta;
                }
            }
        }

        if (root.TryGetProperty("usage", out var usage))
        {
            var inputTokens = TryReadLong(usage, "input_tokens");
            var outputTokens = TryReadLong(usage, "output_tokens");
            yield return new UsageDelta(inputTokens, outputTokens, CacheReadTokens: null, CacheWriteTokens: null);
        }

        yield return new CompletedDelta();
    }

    private static object BuildPayload(LlmRequest request)
    {
        var tools = request.Tools.Select(
            tool => new Dictionary<string, object?>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = ToUntypedObject(tool.ParametersSchema)
            }).ToArray();

        return new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["system"] = request.SystemPrompt,
            ["max_tokens"] = 2_048,
            ["messages"] = BuildMessages(request.Messages),
            ["tools"] = tools
        };
    }

    private static IReadOnlyList<object> BuildMessages(IReadOnlyList<ChatMessage> transcript)
    {
        var messages = new List<object>(transcript.Count);
        foreach (var message in transcript)
        {
            switch (message.Role)
            {
                case ChatRole.User:
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = message.Content
                    });
                    break;

                case ChatRole.Assistant when !string.IsNullOrWhiteSpace(message.ToolCallId) &&
                                             !string.IsNullOrWhiteSpace(message.ToolName):
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "tool_use",
                                ["id"] = message.ToolCallId,
                                ["name"] = message.ToolName,
                                ["input"] = ParseArgumentsAsObject(message.Content)
                            }
                        }
                    });
                    break;

                case ChatRole.Assistant:
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = message.Content
                    });
                    break;

                case ChatRole.Tool:
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = message.ToolCallId,
                                ["content"] = message.Content
                            }
                        }
                    });
                    break;
            }
        }

        return messages;
    }

    private static IEnumerable<LlmDelta> ParseContentBlock(JsonElement block)
    {
        if (!block.TryGetProperty("type", out var typeElement) || typeElement.ValueKind is not JsonValueKind.String)
        {
            yield break;
        }

        var type = typeElement.GetString();
        if (type == "text" && block.TryGetProperty("text", out var textElement))
        {
            var text = textElement.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return new AssistantTextDelta(text);
            }

            yield break;
        }

        if (type == "tool_use")
        {
            var id = block.TryGetProperty("id", out var idElement)
                ? idElement.GetString() ?? Guid.NewGuid().ToString("n")
                : Guid.NewGuid().ToString("n");
            var name = block.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? "unknown_tool"
                : "unknown_tool";
            var input = block.TryGetProperty("input", out var inputElement)
                ? inputElement.Clone()
                : JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
            yield return new ToolCallDelta(id, name, input);
        }
    }

    private static object ParseArgumentsAsObject(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return JsonSerializer.Deserialize<object>(document.RootElement.GetRawText()) ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>
            {
                ["raw"] = content
            };
        }
    }

    private static object? ToUntypedObject(JsonElement element)
    {
        return JsonSerializer.Deserialize<object>(element.GetRawText());
    }

    private static long? TryReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64(),
            _ => null
        };
    }

    private static string ExtractErrorMessage(string rawBody)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            if (document.RootElement.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.TryGetProperty("message", out var messageElement))
                {
                    return messageElement.GetString() ?? "Unknown Anthropic error.";
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to raw body output.
        }

        return rawBody.Length > 400 ? rawBody[..400] : rawBody;
    }
}
