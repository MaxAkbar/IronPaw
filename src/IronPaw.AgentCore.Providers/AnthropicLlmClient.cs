using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Providers;

public sealed class AnthropicLlmClient : ILlmClient
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
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Headers.Add("x-api-key", apiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            yield return new LlmErrorDelta(
                $"Anthropic request failed ({(int)response.StatusCode}): {ExtractErrorMessage(errorBody)}");
            yield break;
        }

        // Track content blocks by their index; tool_use blocks accumulate partial JSON.
        var contentBlocks = new Dictionary<int, ContentBlockState>();
        long inputTokens = 0;
        long outputTokens = 0;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await foreach (var dataLine in ReadSseDataLinesAsync(reader, ct))
        {
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
                if (!root.TryGetProperty("type", out var typeEl)) continue;
                var eventType = typeEl.GetString();

                switch (eventType)
                {
                    case "message_start":
                        if (root.TryGetProperty("message", out var msgEl) &&
                            msgEl.TryGetProperty("usage", out var startUsageEl))
                        {
                            inputTokens = TryReadLong(startUsageEl, "input_tokens") ?? 0;
                        }

                        break;

                    case "content_block_start":
                    {
                        var index = root.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : -1;
                        if (index < 0) break;

                        if (!root.TryGetProperty("content_block", out var blockEl)) break;
                        var blockType = blockEl.TryGetProperty("type", out var btEl) ? btEl.GetString() : null;

                        var state = new ContentBlockState { Type = blockType ?? string.Empty };
                        if (blockType == "tool_use")
                        {
                            state.ToolId = blockEl.TryGetProperty("id", out var tidEl) ? tidEl.GetString() : null;
                            state.ToolName = blockEl.TryGetProperty("name", out var tnEl) ? tnEl.GetString() : null;
                        }

                        contentBlocks[index] = state;
                        break;
                    }

                    case "content_block_delta":
                    {
                        var index = root.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : -1;
                        if (index < 0 || !contentBlocks.TryGetValue(index, out var state)) break;
                        if (!root.TryGetProperty("delta", out var deltaEl)) break;

                        var deltaType = deltaEl.TryGetProperty("type", out var dtEl) ? dtEl.GetString() : null;

                        if (deltaType == "text_delta" &&
                            deltaEl.TryGetProperty("text", out var textEl))
                        {
                            var text = textEl.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                yield return new AssistantTextDelta(text);
                            }
                        }
                        else if (deltaType == "input_json_delta" &&
                                 deltaEl.TryGetProperty("partial_json", out var jsonEl))
                        {
                            state.AccumulatedJson.Append(jsonEl.GetString());
                        }

                        break;
                    }

                    case "content_block_stop":
                    {
                        var index = root.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : -1;
                        if (index >= 0 && contentBlocks.TryGetValue(index, out var state) &&
                            state.Type == "tool_use")
                        {
                            var id = state.ToolId ?? Guid.NewGuid().ToString("n");
                            var name = state.ToolName ?? "unknown_tool";
                            var args = ParseJsonArguments(state.AccumulatedJson.ToString());
                            yield return new ToolCallDelta(id, name, args);
                        }

                        break;
                    }

                    case "message_delta":
                        if (root.TryGetProperty("usage", out var deltaUsageEl))
                        {
                            outputTokens = TryReadLong(deltaUsageEl, "output_tokens") ?? 0;
                        }

                        yield return new UsageDelta(inputTokens, outputTokens, CacheReadTokens: null, CacheWriteTokens: null);
                        break;

                    case "message_stop":
                        yield return new CompletedDelta();
                        yield break;
                }
            }
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
            ["max_tokens"] = request.MaxOutputTokens,
            ["messages"] = BuildMessages(request.Messages),
            ["tools"] = tools,
            ["stream"] = true
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

    private static object ParseArgumentsAsObject(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return JsonSerializer.Deserialize<object>(document.RootElement.GetRawText()) ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?> { ["raw"] = content };
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
                return messageElement.GetString() ?? "Unknown Anthropic error.";
            }
        }
        catch (JsonException) { }

        return rawBody.Length > 400 ? rawBody[..400] : rawBody;
    }

    private sealed class ContentBlockState
    {
        public string Type { get; init; } = string.Empty;
        public string? ToolId { get; set; }
        public string? ToolName { get; set; }
        public StringBuilder AccumulatedJson { get; } = new();
    }
}
