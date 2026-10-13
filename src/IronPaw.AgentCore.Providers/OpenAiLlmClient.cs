using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Providers;

public sealed class OpenAiLlmClient : ILlmClient
{
    private const string ContinuationPrompt =
        "Continue exactly where you left off. Do not repeat previous text. Keep the same format and tone.";

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyResolver;
    private readonly Uri _chatCompletionsEndpoint;
    private readonly OpenAiClientOptions _options;

    public OpenAiLlmClient(
        HttpClient httpClient,
        Func<string?> apiKeyResolver,
        string baseUrl = "https://api.openai.com",
        OpenAiClientOptions? options = null)
    {
        _httpClient = httpClient;
        _apiKeyResolver = apiKeyResolver;
        _chatCompletionsEndpoint = new Uri($"{baseUrl.TrimEnd('/')}/v1/chat/completions");
        _options = (options ?? new OpenAiClientOptions()).Normalize();
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

        var conversationMessages = request.Messages.ToList();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        ct = timeout.Token;
        var continuationPassesRemaining = _options.MaxAutoContinuationPasses;
        long consumedOutputTokensByUsage = 0;
        long consumedOutputTokensByEstimate = 0;

        while (true)
        {
            var consumedOutputTokens = Math.Max(consumedOutputTokensByUsage, consumedOutputTokensByEstimate);
            var payloadBuild = BuildPayload(request, conversationMessages, consumedOutputTokens);
            if (payloadBuild.Budget.MaxTokens <= 0)
            {
                // If we already produced output, exit cleanly when the remaining budget is exhausted.
                if (consumedOutputTokens > 0 || payloadBuild.Budget.RemainingRequestedOutputTokens <= 0)
                {
                    break;
                }

                yield return new LlmErrorDelta(
                    $"No output token budget available (estimated_input_tokens={payloadBuild.Budget.EstimatedInputTokens}, " +
                    $"context_window_tokens={_options.ContextWindowTokens}, safety_margin_tokens={_options.SafetyMarginTokens}). " +
                    "Reduce prompt size or increase the context window setting.");
                yield break;
            }

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsEndpoint);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            httpRequest.Content = JsonContent.Create(payloadBuild.Payload);

            using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                yield return new LlmErrorDelta(
                    $"OpenAI request failed ({(int)response.StatusCode}): {ExtractErrorMessage(errorBody)}");
                yield break;
            }

            var passState = new StreamPassState();
            await foreach (var delta in StreamPassAsync(response, passState, ct))
            {
                yield return delta;
            }
            if (passState.Failed) yield break;
            if (passState.LastFinishReason == "content_filter")
            {
                yield return new LlmErrorDelta("OpenAI response was interrupted by a content filter.");
                yield break;
            }

            consumedOutputTokensByEstimate += passState.EstimatedOutputTokens;
            if (passState.OutputTokensFromUsage.HasValue)
            {
                consumedOutputTokensByUsage += passState.OutputTokensFromUsage.Value;
            }

            consumedOutputTokens = Math.Max(consumedOutputTokensByUsage, consumedOutputTokensByEstimate);
            if (!ShouldAutoContinue(passState, continuationPassesRemaining, consumedOutputTokens, request.MaxOutputTokens))
            {
                if (passState.LastFinishReason == "length")
                {
                    yield return new LlmErrorDelta("OpenAI output limit reached before the response completed.");
                    yield break;
                }
                break;
            }

            conversationMessages.Add(new ChatMessage(ChatRole.Assistant, passState.AssistantText.ToString()));
            conversationMessages.Add(new ChatMessage(ChatRole.User, ContinuationPrompt));
            continuationPassesRemaining--;
        }

        yield return new CompletedDelta();
    }

    private async IAsyncEnumerable<LlmDelta> StreamPassAsync(
        HttpResponseMessage response,
        StreamPassState passState,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Accumulate tool-call argument fragments per index; emit complete calls at finish.
        var toolCallBuilders = new Dictionary<int, ToolCallBuilder>();
        var sawDone = false;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await foreach (var dataLine in ReadSseDataLinesAsync(reader, ct))
        {
            if (dataLine == "[DONE]") { sawDone = true; break; }

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(dataLine);
            }
            catch (JsonException) { }
            if (doc is null)
            {
                passState.Failed = true;
                yield return new LlmErrorDelta("OpenAI stream contained malformed JSON.");
                yield break;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out _))
                {
                    passState.Failed = true;
                    yield return new LlmErrorDelta($"OpenAI stream failed: {ExtractErrorMessage(dataLine)}");
                    yield break;
                }

                // Usage-only chunk emitted by stream_options: include_usage
                if (!root.TryGetProperty("choices", out var choicesEl) || choicesEl.GetArrayLength() == 0)
                {
                    if (root.TryGetProperty("usage", out var topUsageEl))
                    {
                        var promptTokens = TryReadLong(topUsageEl, "prompt_tokens");
                        var completionTokens = TryReadLong(topUsageEl, "completion_tokens");
                        if (completionTokens.HasValue)
                        {
                            passState.OutputTokensFromUsage = Math.Max(
                                passState.OutputTokensFromUsage ?? 0,
                                completionTokens.Value);
                        }

                        yield return new UsageDelta(
                            promptTokens,
                            completionTokens,
                            CacheReadTokens: null,
                            CacheWriteTokens: null);
                    }

                    continue;
                }

                var choice = choicesEl[0];
                if (!choice.TryGetProperty("delta", out var delta)) continue;

                // Text content — emit immediately for real-time streaming.
                if (delta.TryGetProperty("content", out var contentEl) &&
                    contentEl.ValueKind is JsonValueKind.String)
                {
                    var text = contentEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        passState.AssistantText.Append(text);
                        passState.EstimatedOutputCharacters += text.Length;
                        yield return new AssistantTextDelta(text);
                    }
                }

                // Tool-call fragments — accumulate until finish.
                if (delta.TryGetProperty("tool_calls", out var toolCallsEl) &&
                    toolCallsEl.ValueKind is JsonValueKind.Array)
                {
                    passState.HasToolCalls = true;

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
                                var fragment = argsEl.GetString() ?? string.Empty;
                                passState.EstimatedOutputCharacters += fragment.Length;
                                builder.Arguments.Append(fragment);
                            }
                        }
                    }
                }

                // On finish, emit any accumulated tool calls.
                if (choice.TryGetProperty("finish_reason", out var finishEl) &&
                    finishEl.ValueKind is JsonValueKind.String &&
                    finishEl.GetString() is { } finishReason)
                {
                    passState.LastFinishReason = finishReason;
                    if (finishReason is "tool_calls" or "stop")
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
        }

        if (!sawDone && passState.LastFinishReason is null)
        {
            passState.Failed = true;
            yield return new LlmErrorDelta("OpenAI stream ended before a completion marker.");
            yield break;
        }

        // Emit any remaining tool calls (some local models omit a finish_reason chunk).
        foreach (var builder in toolCallBuilders.Values.OrderBy(b => b.Index))
        {
            yield return new ToolCallDelta(
                builder.Id ?? Guid.NewGuid().ToString("n"),
                builder.Name ?? "unknown_tool",
                ParseJsonArguments(builder.Arguments.ToString()));
        }
    }

    private PayloadBuildResult BuildPayload(
        LlmRequest request,
        IReadOnlyList<ChatMessage> conversationMessages,
        long consumedOutputTokens)
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
            })
            .Cast<object>()
            .ToArray();
        var messages = BuildMessages(request.SystemPrompt, conversationMessages);
        var budget = OpenAiOutputTokenBudgetPlanner.Plan(
            _options,
            messages,
            tools,
            request.MaxOutputTokens,
            consumedOutputTokens);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["tools"] = tools,
            ["tool_choice"] = "auto",
            ["max_tokens"] = budget.MaxTokens,
            ["stream"] = true,
            ["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true }
        };

        return new PayloadBuildResult(payload, budget);
    }

    private static IReadOnlyList<object> BuildMessages(string systemPrompt, IReadOnlyList<ChatMessage> transcript)
    {
        var messages = new List<object>
        {
            new Dictionary<string, object?> { ["role"] = "system", ["content"] = systemPrompt }
        };
        for (var i = 0; i < transcript.Count; i++)
        {
            var message = transcript[i];
            if (message.Role == ChatRole.Assistant)
            {
                var text = new List<string>();
                var calls = new List<object>();
                do
                {
                    message = transcript[i];
                    if (!string.IsNullOrWhiteSpace(message.ToolCallId) && !string.IsNullOrWhiteSpace(message.ToolName))
                    {
                        calls.Add(new Dictionary<string, object?>
                        {
                            ["id"] = message.ToolCallId,
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object?>
                            {
                                ["name"] = message.ToolName,
                                ["arguments"] = CoerceJsonString(message.Content)
                            }
                        });
                    }
                    else if (!string.IsNullOrWhiteSpace(message.Content))
                    {
                        text.Add(message.Content);
                    }
                    if (i + 1 >= transcript.Count || transcript[i + 1].Role != ChatRole.Assistant) break;
                    i++;
                } while (true);
                var turn = new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = text.Count == 0 ? null : string.Join("\n", text)
                };
                if (calls.Count > 0) turn["tool_calls"] = calls;
                messages.Add(turn);
            }
            else if (message.Role == ChatRole.Tool)
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool", ["tool_call_id"] = message.ToolCallId, ["content"] = message.Content
                });
            }
            else if (message.Role == ChatRole.User)
            {
                messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = message.Content });
            }
        }
        return messages;
    }

    private static bool ShouldAutoContinue(
        StreamPassState passState,
        int continuationPassesRemaining,
        long consumedOutputTokens,
        int requestedMaxOutputTokens)
    {
        if (continuationPassesRemaining <= 0)
        {
            return false;
        }

        if (!string.Equals(passState.LastFinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (passState.HasToolCalls)
        {
            return false;
        }

        if (passState.AssistantText.Length == 0)
        {
            return false;
        }

        return consumedOutputTokens < requestedMaxOutputTokens;
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

    private sealed class StreamPassState
    {
        public StringBuilder AssistantText { get; } = new();
        public string? LastFinishReason { get; set; }
        public bool HasToolCalls { get; set; }
        public bool Failed { get; set; }
        public long? OutputTokensFromUsage { get; set; }
        public int EstimatedOutputCharacters { get; set; }
        public int EstimatedOutputTokens => OpenAiOutputTokenBudgetPlanner.EstimateTokensFromCharacters(EstimatedOutputCharacters);
    }

    private sealed record PayloadBuildResult(
        IReadOnlyDictionary<string, object?> Payload,
        OpenAiOutputTokenBudgetPlan Budget);
}
