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
    private readonly IMemoryStore? _memoryStore;
    private readonly IRunHistoryStore? _runHistoryStore;

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
        ISystemPromptBuilder promptBuilder,
        IMemoryStore? memoryStore = null,
        IRunHistoryStore? runHistoryStore = null)
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
        _memoryStore = memoryStore;
        _runHistoryStore = runHistoryStore;
    }

    public Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentEventSink sink, CancellationToken ct)
    {
        var effectiveSink = sink ?? NullAgentEventSink.Instance;
        return _laneScheduler.EnqueueAsync(request.SessionKey, laneCt => RunInLaneAsync(request, effectiveSink, laneCt), ct);
    }

    private async Task<AgentRunResult> RunInLaneAsync(AgentRunRequest request, IAgentEventSink sink, CancellationToken ct)
    {
        var runId = RunId.New();
        var startedAt = DateTimeOffset.UtcNow;
        var rounds = 0;
        long aggregatedInput = 0;
        long aggregatedOutput = 0;
        long lastCacheRead = 0;
        long lastCacheWrite = 0;
        var finalAssistantMessage = string.Empty;
        var toolsUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pre-load memories and enrich the request.
        var effectiveRequest = request;
        if (_memoryStore is not null)
        {
            var globalMemories = await _memoryStore.LoadGlobalAsync(ct);
            var sessionMemories = await _memoryStore.LoadSessionAsync(request.SessionKey, ct);
            var activeMemories = globalMemories.Concat(sessionMemories).ToList();
            effectiveRequest = request with { ActiveMemories = activeMemories };
        }

        await PublishAsync(
            sink,
            new AgentEvent(
                runId,
                effectiveRequest.SessionKey,
                AgentEventStream.Lifecycle,
                Type: "run_started",
                Timestamp: DateTimeOffset.UtcNow,
                Data: EventData.Of(
                    ("provider", effectiveRequest.Provider),
                    ("model", effectiveRequest.Model))),
            ct);

        var loadedTranscript = await _transcriptStore.LoadAsync(effectiveRequest.SessionKey, ct);
        var repairedTranscript = _transcriptSanitizer.Repair(loadedTranscript);
        var transcript = repairedTranscript.ToList();

        transcript.Add(new ChatMessage(ChatRole.User, effectiveRequest.UserInput));

        // Initial resolution for the diagnostic event only.
        var initialTools = _toolRegistry.ResolveTools(effectiveRequest.ToolPolicy);
        var initialPrompt = _promptBuilder.BuildPrompt(effectiveRequest, initialTools);

        await PublishAsync(
            sink,
            new AgentEvent(
                runId,
                effectiveRequest.SessionKey,
                AgentEventStream.Diagnostics,
                Type: "prompt_built",
                Timestamp: DateTimeOffset.UtcNow,
                Data: EventData.Of(
                    ("systemPromptChars", initialPrompt.Length),
                    ("toolCount", initialTools.Count))),
            ct);

        var maxRounds = effectiveRequest.MaxRounds;
        var savedInLastIteration = false;
        var lastToolCount = initialTools.Count;
        var lastSystemPromptLength = initialPrompt.Length;

        while (rounds < maxRounds)
        {
            ct.ThrowIfCancellationRequested();
            rounds++;
            savedInLastIteration = false;

            // Re-resolve tools every round so dynamically registered tools become visible.
            var tools = _toolRegistry.ResolveTools(effectiveRequest.ToolPolicy);
            var toolMap = tools.ToDictionary(tool => tool.Descriptor.Name, StringComparer.OrdinalIgnoreCase);
            var systemPrompt = _promptBuilder.BuildPrompt(effectiveRequest, tools);

            lastToolCount = tools.Count;
            lastSystemPromptLength = systemPrompt.Length;

            var toolDefinitions = tools.Select(tool => _schemaNormalizer.ToDefinition(tool, effectiveRequest.Provider)).ToArray();
            var llmRequest = new LlmRequest(
                runId,
                effectiveRequest.SessionKey,
                effectiveRequest.Provider,
                effectiveRequest.Model,
                systemPrompt,
                transcript,
                toolDefinitions,
                Timeout: TimeSpan.FromMinutes(2),
                MaxOutputTokens: effectiveRequest.MaxOutputTokens);

            var assistantBuffer = new StringBuilder();
            var pendingToolCalls = new List<ToolCall>();

            await PublishAsync(
                sink,
                new AgentEvent(
                    runId,
                    effectiveRequest.SessionKey,
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
                                effectiveRequest.SessionKey,
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
                                effectiveRequest.SessionKey,
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
                toolsUsed.Add(toolCall.ToolName);

                ToolResult result;
                if (!toolMap.TryGetValue(toolCall.ToolName, out var tool))
                {
                    result = ToolResult.Error($"Tool '{toolCall.ToolName}' is not available.");
                }
                else
                {
                    var execContext = new ToolExecutionContext(runId, effectiveRequest.SessionKey, effectiveRequest.Provider, effectiveRequest.Model);
                    result = await _toolExecutor.ExecuteAsync(tool, toolCall, execContext, ct);
                }

                transcript.Add(new ChatMessage(ChatRole.Tool, result.Content, toolCall.ToolCallId, toolCall.ToolName));
                await PublishAsync(
                    sink,
                    new AgentEvent(
                        runId,
                        effectiveRequest.SessionKey,
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

            transcript = _toolResultTruncator.Truncate(transcript, effectiveRequest.ContextWindowChars).ToList();
            transcript = _transcriptSanitizer.Repair(transcript).ToList();
            await _transcriptStore.ReplaceAsync(effectiveRequest.SessionKey, transcript, ct);
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
            await _transcriptStore.ReplaceAsync(effectiveRequest.SessionKey, transcript, ct);
        }

        var totalTokens = aggregatedInput + aggregatedOutput + lastCacheRead + lastCacheWrite;
        await _sessionStore.UpsertAsync(
            effectiveRequest.SessionKey,
            existing =>
            {
                var previous = existing ?? new SessionEntry(
                    SessionId: effectiveRequest.SessionKey.Value,
                    UpdatedAt: DateTimeOffset.UtcNow);

                return previous with
                {
                    SessionId = effectiveRequest.SessionKey.Value,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    ProviderOverride = effectiveRequest.Provider,
                    ModelOverride = effectiveRequest.Model,
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

        // Record run history.
        if (_runHistoryStore is not null)
        {
            var summary = new RunSummary(
                RunId: runId.Value,
                SessionId: effectiveRequest.SessionKey.Value,
                StartedAt: startedAt,
                CompletedAt: DateTimeOffset.UtcNow,
                UserPrompt: effectiveRequest.UserInput,
                FinalAssistantMessage: finalAssistantMessage,
                Rounds: rounds,
                TotalTokens: totalTokens,
                ToolsUsed: toolsUsed.ToList());

            await _runHistoryStore.RecordAsync(summary, ct);
        }

        await PublishAsync(
            sink,
            new AgentEvent(
                runId,
                effectiveRequest.SessionKey,
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
            effectiveRequest.SessionKey,
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
