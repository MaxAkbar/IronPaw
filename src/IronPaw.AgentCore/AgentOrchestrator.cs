using System.Text;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private readonly ISessionLaneScheduler _laneScheduler;
    private readonly ISessionStore _sessionStore;
    private readonly ITranscriptStore _transcriptStore;
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolExecutor _toolExecutor;
    private readonly IToolSchemaNormalizer _schemaNormalizer;
    private readonly ILlmClient _llmClient;
    private readonly ITranscriptSanitizer _transcriptSanitizer;
    private readonly IToolResultTruncator _toolResultTruncator;
    private readonly ISystemPromptBuilder _promptBuilder;

    public AgentOrchestrator(
        ISessionLaneScheduler laneScheduler,
        ISessionStore sessionStore,
        ITranscriptStore transcriptStore,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        IToolSchemaNormalizer schemaNormalizer,
        ILlmClient llmClient,
        ITranscriptSanitizer transcriptSanitizer,
        IToolResultTruncator toolResultTruncator,
        ISystemPromptBuilder promptBuilder)
    {
        _laneScheduler = laneScheduler;
        _sessionStore = sessionStore;
        _transcriptStore = transcriptStore;
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _schemaNormalizer = schemaNormalizer;
        _llmClient = llmClient;
        _transcriptSanitizer = transcriptSanitizer;
        _toolResultTruncator = toolResultTruncator;
        _promptBuilder = promptBuilder;
    }

    public Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentEventSink sink, CancellationToken ct)
    {
        var effectiveSink = sink ?? NullAgentEventSink.Instance;
        return _laneScheduler.EnqueueAsync(request.SessionKey, laneCt => RunInLaneAsync(request, effectiveSink, laneCt), ct);
    }

    private async Task<AgentRunResult> RunInLaneAsync(AgentRunRequest request, IAgentEventSink sink, CancellationToken ct)
    {
        var runId = RunId.New();
        var rounds = 0;
        long aggregatedInput = 0;
        long aggregatedOutput = 0;
        long lastCacheRead = 0;
        long lastCacheWrite = 0;
        var finalAssistantMessage = string.Empty;

        await PublishAsync(
            sink,
            new AgentEvent(
                runId,
                request.SessionKey,
                AgentEventStream.Lifecycle,
                Type: "run_started",
                Timestamp: DateTimeOffset.UtcNow,
                Data: EventData.Of(
                    ("provider", request.Provider),
                    ("model", request.Model))),
            ct);

        var loadedTranscript = await _transcriptStore.LoadAsync(request.SessionKey, ct);
        var repairedTranscript = _transcriptSanitizer.Repair(loadedTranscript);
        var transcript = repairedTranscript.ToList();

        transcript.Add(new ChatMessage(ChatRole.User, request.UserInput));

        // Initial resolution for the diagnostic event only.
        var initialTools = _toolRegistry.ResolveTools(request.ToolPolicy);
        var initialPrompt = _promptBuilder.BuildPrompt(request, initialTools);

        await PublishAsync(
            sink,
            new AgentEvent(
                runId,
                request.SessionKey,
                AgentEventStream.Diagnostics,
                Type: "prompt_built",
                Timestamp: DateTimeOffset.UtcNow,
                Data: EventData.Of(
                    ("systemPromptChars", initialPrompt.Length),
                    ("toolCount", initialTools.Count))),
            ct);

        var maxRounds = request.MaxRounds;
        var savedInLastIteration = false;
        var lastToolCount = initialTools.Count;
        var lastSystemPromptLength = initialPrompt.Length;

        while (rounds < maxRounds)
        {
            ct.ThrowIfCancellationRequested();
            rounds++;
            savedInLastIteration = false;

            // Re-resolve tools every round so dynamically registered tools become visible.
            var tools = _toolRegistry.ResolveTools(request.ToolPolicy);
            var toolMap = tools.ToDictionary(tool => tool.Descriptor.Name, StringComparer.OrdinalIgnoreCase);
            var systemPrompt = _promptBuilder.BuildPrompt(request, tools);

            lastToolCount = tools.Count;
            lastSystemPromptLength = systemPrompt.Length;

            var toolDefinitions = tools.Select(tool => _schemaNormalizer.ToDefinition(tool, request.Provider)).ToArray();
            var llmRequest = new LlmRequest(
                runId,
                request.SessionKey,
                request.Provider,
                request.Model,
                systemPrompt,
                transcript,
                toolDefinitions,
                Timeout: TimeSpan.FromMinutes(2),
                MaxOutputTokens: request.MaxOutputTokens);

            var assistantBuffer = new StringBuilder();
            var pendingToolCalls = new List<ToolCall>();

            await PublishAsync(
                sink,
                new AgentEvent(
                    runId,
                    request.SessionKey,
                    AgentEventStream.Lifecycle,
                    Type: "llm_round_started",
                    Timestamp: DateTimeOffset.UtcNow,
                    Data: EventData.Of(("round", rounds))),
                ct);

            await foreach (var delta in _llmClient.StreamAsync(llmRequest, ct))
            {
                switch (delta)
                {
                    case AssistantTextDelta text:
                        assistantBuffer.Append(text.Text);
                        await PublishAsync(
                            sink,
                            new AgentEvent(
                                runId,
                                request.SessionKey,
                                AgentEventStream.Assistant,
                                Type: "assistant_delta",
                                Timestamp: DateTimeOffset.UtcNow,
                                Data: EventData.Of(("text", text.Text))),
                            ct);
                        break;

                    case ToolCallDelta toolCall:
                        // Collect tool calls; do NOT add to transcript here — text must come first.
                        pendingToolCalls.Add(new ToolCall(toolCall.ToolCallId, toolCall.ToolName, toolCall.Arguments.Clone()));
                        await PublishAsync(
                            sink,
                            new AgentEvent(
                                runId,
                                request.SessionKey,
                                AgentEventStream.Tool,
                                Type: "tool_call",
                                Timestamp: DateTimeOffset.UtcNow,
                                Data: EventData.Of(
                                    ("toolName", toolCall.ToolName),
                                    ("toolCallId", toolCall.ToolCallId))),
                            ct);
                        break;

                    case UsageDelta usage:
                        aggregatedInput += usage.InputTokens ?? 0;
                        aggregatedOutput += usage.OutputTokens ?? 0;
                        if (usage.CacheReadTokens.HasValue)
                        {
                            lastCacheRead += usage.CacheReadTokens.Value;
                        }

                        if (usage.CacheWriteTokens.HasValue)
                        {
                            lastCacheWrite += usage.CacheWriteTokens.Value;
                        }

                        break;

                    case LlmErrorDelta error:
                        throw new InvalidOperationException($"LLM returned an error: {error.Error}");
                }
            }

            // Add text first, then tool-call messages, so the transcript never has two
            // consecutive assistant messages from the same response (text before tool calls).
            var assistantText = assistantBuffer.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(assistantText))
            {
                transcript.Add(new ChatMessage(ChatRole.Assistant, assistantText));
            }

            foreach (var toolCall in pendingToolCalls)
            {
                transcript.Add(new ChatMessage(ChatRole.Assistant, toolCall.Arguments.GetRawText(), toolCall.ToolCallId, toolCall.ToolName));
            }

            if (pendingToolCalls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(assistantText))
                {
                    finalAssistantMessage = assistantText;
                }

                break;
            }

            foreach (var toolCall in pendingToolCalls)
            {
                ToolResult result;
                if (!toolMap.TryGetValue(toolCall.ToolName, out var tool))
                {
                    result = ToolResult.Error($"Tool '{toolCall.ToolName}' is not available.");
                }
                else
                {
                    var execContext = new ToolExecutionContext(runId, request.SessionKey, request.Provider, request.Model);
                    result = await _toolExecutor.ExecuteAsync(tool, toolCall, execContext, ct);
                }

                transcript.Add(new ChatMessage(ChatRole.Tool, result.Content, toolCall.ToolCallId, toolCall.ToolName));
                await PublishAsync(
                    sink,
                    new AgentEvent(
                        runId,
                        request.SessionKey,
                        AgentEventStream.Tool,
                        Type: "tool_result",
                        Timestamp: DateTimeOffset.UtcNow,
                        Data: EventData.Of(
                            ("toolName", toolCall.ToolName),
                            ("toolCallId", toolCall.ToolCallId),
                            ("isError", result.IsError),
                            ("contentChars", result.Content.Length)),
                        Severity: result.IsError ? AgentEventSeverity.Warning : AgentEventSeverity.Info),
                    ct);
            }

            transcript = _toolResultTruncator.Truncate(transcript, request.ContextWindowChars).ToList();
            transcript = _transcriptSanitizer.Repair(transcript).ToList();
            await _transcriptStore.ReplaceAsync(request.SessionKey, transcript, ct);
            savedInLastIteration = true;
        }

        if (string.IsNullOrWhiteSpace(finalAssistantMessage))
        {
            finalAssistantMessage = transcript.LastOrDefault(
                message => message.Role is ChatRole.Assistant && string.IsNullOrWhiteSpace(message.ToolCallId))
                ?.Content ?? "Run completed with no assistant output.";
        }

        // Only save when the loop exited cleanly (no tool calls in last round).
        // When tool calls occurred in the last round, the transcript was already saved inside the loop.
        if (!savedInLastIteration)
        {
            await _transcriptStore.ReplaceAsync(request.SessionKey, transcript, ct);
        }

        var totalTokens = aggregatedInput + aggregatedOutput + lastCacheRead + lastCacheWrite;
        await _sessionStore.UpsertAsync(
            request.SessionKey,
            existing =>
            {
                var previous = existing ?? new SessionEntry(
                    SessionId: request.SessionKey.Value,
                    UpdatedAt: DateTimeOffset.UtcNow);

                return previous with
                {
                    SessionId = request.SessionKey.Value,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    ProviderOverride = request.Provider,
                    ModelOverride = request.Model,
                    InputTokens = previous.InputTokens + aggregatedInput,
                    OutputTokens = previous.OutputTokens + aggregatedOutput,
                    TotalTokens = previous.TotalTokens + totalTokens,
                    TotalTokensFresh = true,
                    Diagnostics = new Dictionary<string, object?>
                    {
                        ["toolCount"] = lastToolCount,
                        ["systemPromptChars"] = lastSystemPromptLength,
                        ["rounds"] = rounds
                    }
                };
            },
            ct);

        await PublishAsync(
            sink,
            new AgentEvent(
                runId,
                request.SessionKey,
                AgentEventStream.Lifecycle,
                Type: "run_completed",
                Timestamp: DateTimeOffset.UtcNow,
                Data: EventData.Of(
                    ("rounds", rounds),
                    ("inputTokens", aggregatedInput),
                    ("outputTokens", aggregatedOutput),
                    ("cacheReadTokens", lastCacheRead),
                    ("cacheWriteTokens", lastCacheWrite))),
            ct);

        return new AgentRunResult(
            runId,
            request.SessionKey,
            finalAssistantMessage,
            transcript,
            new Dictionary<string, object?>
            {
                ["rounds"] = rounds,
                ["toolCount"] = lastToolCount,
                ["totalTokens"] = totalTokens
            });
    }

    private static ValueTask PublishAsync(IAgentEventSink sink, AgentEvent evt, CancellationToken ct)
    {
        return sink.PublishAsync(evt, ct);
    }
}
