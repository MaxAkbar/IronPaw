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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        ct = timeout.Token;
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
        long? inputTokens = null;
        long? outputTokens = null;
        long reportedInputTokens = 0;
        long reportedOutputTokens = 0;
        long? cacheReadTokens = null;
        long? cacheWriteTokens = null;
        long reportedCacheReadTokens = 0;
        long reportedCacheWriteTokens = 0;
        string? stopReason = null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await foreach (var dataLine in ReadSseDataLinesAsync(reader, ct))
        {
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(dataLine);
            }
            catch (JsonException) { }
            if (doc is null)
            {
                yield return new LlmErrorDelta("Anthropic stream contained malformed JSON.");
                yield break;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) continue;
                var eventType = typeEl.GetString();

                switch (eventType)
                {
                    case "error":
                        yield return new LlmErrorDelta($"Anthropic stream failed: {ExtractErrorMessage(dataLine)}");
                        yield break;
                    case "message_start":
                        if (root.TryGetProperty("message", out var msgEl) &&
                            msgEl.TryGetProperty("usage", out var startUsageEl))
                        {
                            inputTokens = TryReadLong(startUsageEl, "input_tokens");
                            cacheReadTokens = TryReadLong(startUsageEl, "cache_read_input_tokens");
                            cacheWriteTokens = TryReadLong(startUsageEl, "cache_creation_input_tokens");
                            if (inputTokens is < 0 || cacheReadTokens is < 0 || cacheWriteTokens is < 0)
                            {
                                yield return new LlmErrorDelta("Anthropic stream reported negative token usage.");
                                yield break;
                            }
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
                        if (root.TryGetProperty("delta", out var messageDelta) &&
                            messageDelta.TryGetProperty("stop_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                        {
                            stopReason = reason.GetString();
                        }
                        if (root.TryGetProperty("usage", out var deltaUsageEl))
                        {
                            outputTokens = TryReadLong(deltaUsageEl, "output_tokens");
                            if (outputTokens is < 0)
                            {
                                yield return new LlmErrorDelta("Anthropic stream reported negative token usage.");
                                yield break;
                            }
                        }

                        yield return new UsageDelta(
                            inputTokens.HasValue ? Math.Max(0, inputTokens.Value - reportedInputTokens) : null,
                            outputTokens.HasValue ? Math.Max(0, outputTokens.Value - reportedOutputTokens) : null,
                            CacheReadTokens: cacheReadTokens.HasValue ? Math.Max(0, cacheReadTokens.Value - reportedCacheReadTokens) : null,
                            CacheWriteTokens: cacheWriteTokens.HasValue ? Math.Max(0, cacheWriteTokens.Value - reportedCacheWriteTokens) : null);
                        reportedInputTokens = Math.Max(reportedInputTokens, inputTokens ?? 0);
                        reportedOutputTokens = Math.Max(reportedOutputTokens, outputTokens ?? 0);
                        reportedCacheReadTokens = Math.Max(reportedCacheReadTokens, cacheReadTokens ?? 0);
                        reportedCacheWriteTokens = Math.Max(reportedCacheWriteTokens, cacheWriteTokens ?? 0);
                        break;

                    case "message_stop":
                        if (stopReason == "max_tokens")
                        {
                            yield return new LlmErrorDelta("Anthropic output limit reached before the response completed.");
                            yield break;
                        }
                        yield return new CompletedDelta();
                        yield break;
                }
            }
        }

        yield return new LlmErrorDelta("Anthropic stream ended before message_stop.");
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
        var messages = new List<object>();
        for (var i = 0; i < transcript.Count; i++)
        {
            var message = transcript[i];
            if (message.Role is ChatRole.Assistant or ChatRole.Tool)
            {
                var role = message.Role;
                var blocks = new List<object>();
                do
                {
                    message = transcript[i];
                    if (role == ChatRole.Tool)
                    {
                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "tool_result", ["tool_use_id"] = message.ToolCallId, ["content"] = message.Content
                        });
                    }
                    else if (!string.IsNullOrWhiteSpace(message.ToolCallId) && !string.IsNullOrWhiteSpace(message.ToolName))
                    {
                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "tool_use", ["id"] = message.ToolCallId,
                            ["name"] = message.ToolName, ["input"] = ParseArgumentsAsObject(message.Content)
                        });
                    }
                    else
                    {
                        blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = message.Content });
                    }
                    if (i + 1 >= transcript.Count || transcript[i + 1].Role != role) break;
                    i++;
                } while (true);
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = role == ChatRole.Assistant ? "assistant" : "user", ["content"] = blocks
                });
            }
            else if (message.Role == ChatRole.User)
            {
                messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = message.Content });
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
