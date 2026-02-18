using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Providers;

public sealed class OpenAiLlmClient : ILLMClient
{
    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyResolver;
    private readonly Uri _chatCompletionsEndpoint;

    public OpenAiLlmClient(HttpClient httpClient, Func<string?> apiKeyResolver, string baseUrl = "https://api.openai.com")
    {
        _httpClient = httpClient;
        _apiKeyResolver = apiKeyResolver;
        _chatCompletionsEndpoint = new Uri($"{baseUrl.TrimEnd('/')}/v1/chat/completions");
    }

    public async IAsyncEnumerable<LlmDelta> StreamAsync(
        LlmRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var apiKey = _apiKeyResolver();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield return new LlmErrorDelta("OPENAI_API_KEY is not configured.");
            yield break;
        }

        var payload = BuildPayload(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        var rawBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            yield return new LlmErrorDelta(
                $"OpenAI request failed ({(int)response.StatusCode}): {ExtractErrorMessage(rawBody)}");
            yield break;
        }

        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choicesElement) || choicesElement.GetArrayLength() == 0)
        {
            yield return new LlmErrorDelta("OpenAI response did not include choices.");
            yield break;
        }

        var message = choicesElement[0].GetProperty("message");
        foreach (var delta in ParseMessage(message))
        {
            yield return delta;
        }

        if (root.TryGetProperty("usage", out var usage))
        {
            var inputTokens = TryReadLong(usage, "prompt_tokens");
            var outputTokens = TryReadLong(usage, "completion_tokens");
            yield return new UsageDelta(inputTokens, outputTokens, CacheReadTokens: null, CacheWriteTokens: null);
        }

        yield return new CompletedDelta();
    }

    private static object BuildPayload(LlmRequest request)
    {
        var tools = request.Tools.Select(
            tool => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = ToUntypedObject(tool.ParametersSchema)
                }
            }).ToArray();

        return new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessages(request),
            ["tools"] = tools,
            ["tool_choice"] = "auto"
        };
    }

    private static IReadOnlyList<object> BuildMessages(LlmRequest request)
    {
        var messages = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt
            }
        };

        foreach (var message in request.Messages)
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
                        ["content"] = null,
                        ["tool_calls"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["id"] = message.ToolCallId,
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?>
                                {
                                    ["name"] = message.ToolName,
                                    ["arguments"] = CoerceJsonString(message.Content)
                                }
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
                        ["role"] = "tool",
                        ["tool_call_id"] = message.ToolCallId,
                        ["content"] = message.Content
                    });
                    break;
            }
        }

        return messages;
    }

    private static IEnumerable<LlmDelta> ParseMessage(JsonElement message)
    {
        if (message.TryGetProperty("content", out var contentElement))
        {
            if (contentElement.ValueKind is JsonValueKind.String)
            {
                var text = contentElement.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return new AssistantTextDelta(text);
                }
            }
            else if (contentElement.ValueKind is JsonValueKind.Array)
            {
                foreach (var item in contentElement.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.Object &&
                        item.TryGetProperty("type", out var typeElement) &&
                        typeElement.ValueKind is JsonValueKind.String &&
                        typeElement.GetString() == "text" &&
                        item.TryGetProperty("text", out var textElement))
                    {
                        var text = textElement.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            yield return new AssistantTextDelta(text);
                        }
                    }
                }
            }
        }

        if (message.TryGetProperty("tool_calls", out var toolCallsElement) &&
            toolCallsElement.ValueKind is JsonValueKind.Array)
        {
            foreach (var toolCallElement in toolCallsElement.EnumerateArray())
            {
                var toolCallId = toolCallElement.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("n");
                var function = toolCallElement.GetProperty("function");
                var toolName = function.GetProperty("name").GetString() ?? "unknown_tool";
                var rawArguments = function.TryGetProperty("arguments", out var argsElement)
                    ? argsElement.GetString() ?? "{}"
                    : "{}";
                var arguments = ParseJsonArguments(rawArguments);

                yield return new ToolCallDelta(toolCallId, toolName, arguments);
            }
        }
    }

    private static JsonElement ParseJsonArguments(string rawArguments)
    {
        try
        {
            using var document = JsonDocument.Parse(rawArguments);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>
                {
                    ["raw"] = rawArguments
                });
        }
    }

    private static string CoerceJsonString(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "{}";
        }

        try
        {
            using var _ = JsonDocument.Parse(content);
            return content;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["raw"] = content
                });
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
                    return messageElement.GetString() ?? "Unknown OpenAI error.";
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to truncated raw body.
        }

        return rawBody.Length > 400 ? rawBody[..400] : rawBody;
    }
}
