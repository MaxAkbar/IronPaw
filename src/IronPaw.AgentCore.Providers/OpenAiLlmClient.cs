using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Providers;

public sealed class OpenAiLlmClient : ILlmClient
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
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            yield return new LlmErrorDelta(
                $"OpenAI request failed ({(int)response.StatusCode}): {ExtractErrorMessage(errorBody)}");
            yield break;
        }

        // Accumulate tool-call argument fragments per index; emit complete calls at finish.
        var toolCallBuilders = new Dictionary<int, ToolCallBuilder>();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await foreach (var dataLine in ReadSseDataLinesAsync(reader, ct))
        {
            if (dataLine == "[DONE]") break;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(dataLine);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;

                // Usage-only chunk emitted by stream_options: include_usage
                if (!root.TryGetProperty("choices", out var choicesEl) || choicesEl.GetArrayLength() == 0)
                {
                    if (root.TryGetProperty("usage", out var topUsageEl))
                    {
                        yield return new UsageDelta(
                            TryReadLong(topUsageEl, "prompt_tokens"),
                            TryReadLong(topUsageEl, "completion_tokens"),
                            CacheReadTokens: null,
                            CacheWriteTokens: null);
                    }

                    continue;
                }

                var choice = choicesEl[0];
                if (!choice.TryGetProperty("delta", out var delta)) continue;

                // Text content — emit immediately for real-time streaming
                if (delta.TryGetProperty("content", out var contentEl) &&
                    contentEl.ValueKind is JsonValueKind.String)
                {
                    var text = contentEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return new AssistantTextDelta(text);
                    }
                }

                // Tool-call fragments — accumulate until finish
                if (delta.TryGetProperty("tool_calls", out var toolCallsEl) &&
                    toolCallsEl.ValueKind is JsonValueKind.Array)
                {
                    foreach (var tcEl in toolCallsEl.EnumerateArray())
                    {
                        if (!tcEl.TryGetProperty("index", out var indexEl)) continue;
                        var index = indexEl.GetInt32();

                        if (!toolCallBuilders.TryGetValue(index, out var builder))
                        {
                            builder = new ToolCallBuilder(index);
                            toolCallBuilders[index] = builder;
                        }

                        if (tcEl.TryGetProperty("id", out var idEl))
                        {
                            builder.Id = idEl.GetString() ?? builder.Id;
                        }

                        if (tcEl.TryGetProperty("function", out var funcEl))
                        {
                            if (funcEl.TryGetProperty("name", out var nameEl))
                            {
                                builder.Name = nameEl.GetString() ?? builder.Name;
                            }

                            if (funcEl.TryGetProperty("arguments", out var argsEl) &&
                                argsEl.ValueKind is JsonValueKind.String)
                            {
                                builder.Arguments.Append(argsEl.GetString());
                            }
                        }
                    }
                }

                // On finish, emit any accumulated tool calls
                if (choice.TryGetProperty("finish_reason", out var finishEl) &&
                    finishEl.ValueKind is JsonValueKind.String &&
                    finishEl.GetString() is { } finishReason &&
                    finishReason is "tool_calls" or "stop")
                {
                    foreach (var builder in toolCallBuilders.Values.OrderBy(b => b.Index))
                    {
                        yield return new ToolCallDelta(
                            builder.Id ?? Guid.NewGuid().ToString("n"),
                            builder.Name ?? "unknown_tool",
                            ParseJsonArguments(builder.Arguments.ToString()));
                    }

                    toolCallBuilders.Clear();
                }
            }
        }

        // Emit any remaining tool calls (some local models omit a finish_reason chunk)
        foreach (var builder in toolCallBuilders.Values.OrderBy(b => b.Index))
        {
            yield return new ToolCallDelta(
                builder.Id ?? Guid.NewGuid().ToString("n"),
                builder.Name ?? "unknown_tool",
                ParseJsonArguments(builder.Arguments.ToString()));
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
            ["tool_choice"] = "auto",
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = true,
            ["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true }
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

    private static async IAsyncEnumerable<string> ReadSseDataLinesAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                yield return line["data:".Length..].Trim();
            }
        }
    }

    private static JsonElement ParseJsonArguments(string rawArguments)
    {
        if (string.IsNullOrWhiteSpace(rawArguments))
        {
            return JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        }

        try
        {
            using var document = JsonDocument.Parse(rawArguments);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["raw"] = rawArguments });
        }
    }

    private static string CoerceJsonString(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "{}";

        try
        {
            using var _ = JsonDocument.Parse(content);
            return content;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object?> { ["raw"] = content });
        }
    }

    private static object? ToUntypedObject(JsonElement element) =>
        JsonSerializer.Deserialize<object>(element.GetRawText());

    private static long? TryReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind is JsonValueKind.Number ? value.GetInt64() : null;
    }

    private static string ExtractErrorMessage(string rawBody)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            if (document.RootElement.TryGetProperty("error", out var errorElement) &&
                errorElement.TryGetProperty("message", out var messageElement))
            {
                return messageElement.GetString() ?? "Unknown OpenAI error.";
            }
        }
        catch (JsonException) { }

        return rawBody.Length > 400 ? rawBody[..400] : rawBody;
    }

    private sealed class ToolCallBuilder(int index)
    {
        public int Index { get; } = index;
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
