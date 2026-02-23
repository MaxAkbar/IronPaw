using System.Text;
using System.Text.Json;
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

        // Pre-load memories and enrich the request (skip for sub-agents — they start fresh).
        var effectiveRequest = request;
        if (_memoryStore is not null && request.ParentRunId is null)
        {
            var globalMemories = await _memoryStore.LoadGlobalAsync(ct);
            var sessionMemories = await _memoryStore.LoadSessionAsync(request.SessionKey, ct);
            var activeMemories = globalMemories.Concat(sessionMemories).ToList();
            effectiveRequest = request with { ActiveMemories = activeMemories };
        }

        // Derive SenderIsOwner from CallerIdentity so it cannot be spoofed by the host.
        effectiveRequest = effectiveRequest with
        {
            ToolPolicy = effectiveRequest.ToolPolicy with { SenderIsOwner = effectiveRequest.Caller.IsOwner }
        };

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
                    ("model", effectiveRequest.Model)),
                AgentId: effectiveRequest.AgentId),
            ct);

        var loadedTranscript = await _transcriptStore.LoadAsync(effectiveRequest.SessionKey, ct);
        var repairedTranscript = _transcriptSanitizer.Repair(loadedTranscript);
        var transcript = repairedTranscript.ToList();

        transcript.Add(new ChatMessage(ChatRole.User, effectiveRequest.UserInput));

        if (effectiveRequest.AutoCreateSkills)
        {
            await TryAutoCreateSkillsAsync(effectiveRequest, transcript, sink, runId, toolsUsed, ct);
        }

        // Initial resolution for the diagnostic event only.
        var initialTools = _toolRegistry.ResolveTools(effectiveRequest.ToolPolicy);
        var routeResult = await TryRouteWithDelegatesAsync(
            effectiveRequest,
            transcript,
            initialTools,
            sink,
            runId,
            toolsUsed,
            ct);
        aggregatedInput += routeResult.InputTokens;
        aggregatedOutput += routeResult.OutputTokens;
        lastCacheRead += routeResult.CacheReadTokens;
        lastCacheWrite += routeResult.CacheWriteTokens;

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
                    ("toolCount", initialTools.Count)),
                AgentId: effectiveRequest.AgentId),
            ct);

        var maxRounds = routeResult.HaltAfterClarification ? 0 : effectiveRequest.MaxRounds;
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
                    Data: EventData.Of(("round", rounds)),
                    AgentId: effectiveRequest.AgentId),
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
                                Data: EventData.Of(("text", text.Text)),
                                AgentId: effectiveRequest.AgentId),
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
                                    ("toolCallId", toolCall.ToolCallId)),
                                AgentId: effectiveRequest.AgentId),
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
                    var execContext = new ToolExecutionContext(runId, effectiveRequest.SessionKey, effectiveRequest.Provider, effectiveRequest.Model, effectiveRequest.Caller, SubagentDepth: effectiveRequest.ToolPolicy.SubagentDepth);
                    result = await _toolExecutor.ExecuteAsync(tool, toolCall, execContext, ct);
                }

                var fencedContent = ContentFencer.Fence(result, toolCall.ToolName);
                transcript.Add(new ChatMessage(ChatRole.Tool, fencedContent, toolCall.ToolCallId, toolCall.ToolName));
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
                        Severity: result.IsError ? AgentEventSeverity.Warning : AgentEventSeverity.Info,
                        AgentId: effectiveRequest.AgentId),
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
                    ("cacheWriteTokens", lastCacheWrite)),
                AgentId: effectiveRequest.AgentId),
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

    private async Task<RoutePlanExecutionResult> TryRouteWithDelegatesAsync(
        AgentRunRequest request,
        List<ChatMessage> transcript,
        IReadOnlyList<ITool> tools,
        IAgentEventSink sink,
        RunId runId,
        HashSet<string> toolsUsed,
        CancellationToken ct)
    {
        if (request.ParentRunId is not null)
        {
            return RoutePlanExecutionResult.None;
        }

        var invokeAgentTool = tools.FirstOrDefault(tool =>
            tool.Descriptor.Name.Equals("invoke_agent", StringComparison.OrdinalIgnoreCase));
        var listAgentsTool = tools.FirstOrDefault(tool =>
            tool.Descriptor.Name.Equals("list_agents", StringComparison.OrdinalIgnoreCase));
        if (invokeAgentTool is null || listAgentsTool is null)
        {
            return RoutePlanExecutionResult.None;
        }

        var emptyArgs = JsonSerializer.SerializeToElement(new { });
        var listCall = new ToolCall(
            ToolCallId: $"plan_list_agents_{Guid.NewGuid():N}",
            ToolName: listAgentsTool.Descriptor.Name,
            Arguments: emptyArgs);
        var execContext = new ToolExecutionContext(
            runId,
            request.SessionKey,
            request.Provider,
            request.Model,
            request.Caller,
            SubagentDepth: request.ToolPolicy.SubagentDepth);

        var listResult = await _toolExecutor.ExecuteAsync(listAgentsTool, listCall, execContext, ct);
        if (listResult.IsError)
        {
            return RoutePlanExecutionResult.None;
        }

        var availableAgents = ParseAgentCatalog(listResult.Content);
        if (availableAgents.Count == 0)
        {
            return RoutePlanExecutionResult.None;
        }

        await PublishAsync(
            sink,
            new AgentEvent(
                runId,
                request.SessionKey,
                AgentEventStream.Diagnostics,
                Type: "route_planning_started",
                Timestamp: DateTimeOffset.UtcNow,
                Data: EventData.Of(("agentCount", availableAgents.Count)),
                AgentId: request.AgentId),
            ct);

        var plannerPrompt = BuildRoutePlanPrompt(request, availableAgents);
        var plannerRequest = new LlmRequest(
            runId,
            request.SessionKey,
            request.Provider,
            request.Model,
            plannerPrompt,
            transcript,
            Array.Empty<LlmToolDefinition>(),
            Timeout: TimeSpan.FromMinutes(1),
            MaxOutputTokens: 1024);

        var plannerBuffer = new StringBuilder();
        long inputTokens = 0;
        long outputTokens = 0;
        long cacheReadTokens = 0;
        long cacheWriteTokens = 0;

        await foreach (var delta in _llmClient.StreamAsync(plannerRequest, ct))
        {
            switch (delta)
            {
                case AssistantTextDelta text:
                    plannerBuffer.Append(text.Text);
                    break;

                case UsageDelta usage:
                    inputTokens += usage.InputTokens ?? 0;
                    outputTokens += usage.OutputTokens ?? 0;
                    if (usage.CacheReadTokens.HasValue)
                    {
                        cacheReadTokens += usage.CacheReadTokens.Value;
                    }

                    if (usage.CacheWriteTokens.HasValue)
                    {
                        cacheWriteTokens += usage.CacheWriteTokens.Value;
                    }

                    break;

                case LlmErrorDelta error:
                    throw new InvalidOperationException($"LLM returned an error: {error.Error}");
            }
        }

        var plan = ParseRoutePlan(plannerBuffer.ToString(), availableAgents);
        if (plan is null)
        {
            return new RoutePlanExecutionResult(
                HaltAfterClarification: false,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                CacheReadTokens: cacheReadTokens,
                CacheWriteTokens: cacheWriteTokens);
        }

        await PublishAsync(
            sink,
            new AgentEvent(
                runId,
                request.SessionKey,
                AgentEventStream.Diagnostics,
                Type: "route_planning_completed",
                Timestamp: DateTimeOffset.UtcNow,
                Data: EventData.Of(
                    ("needsClarification", plan.NeedsClarification),
                    ("delegationCount", plan.Delegations.Count)),
                AgentId: request.AgentId),
            ct);

        if (plan.NeedsClarification)
        {
            transcript.Add(new ChatMessage(ChatRole.Assistant, BuildClarificationMessage(plan.ClarificationQuestions)));
            return new RoutePlanExecutionResult(
                HaltAfterClarification: true,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                CacheReadTokens: cacheReadTokens,
                CacheWriteTokens: cacheWriteTokens);
        }

        foreach (var delegation in plan.Delegations)
        {
            var args = JsonSerializer.SerializeToElement(new
            {
                agent_id = delegation.AgentId,
                task = delegation.Task
            });
            var toolCallId = $"plan_delegate_{Guid.NewGuid():N}";
            var toolCall = new ToolCall(toolCallId, invokeAgentTool.Descriptor.Name, args);

            transcript.Add(new ChatMessage(ChatRole.Assistant, args.GetRawText(), toolCall.ToolCallId, toolCall.ToolName));
            toolsUsed.Add(toolCall.ToolName);

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
                        ("toolCallId", toolCall.ToolCallId)),
                    AgentId: request.AgentId),
                ct);

            var result = await _toolExecutor.ExecuteAsync(invokeAgentTool, toolCall, execContext, ct);
            var fencedContent = ContentFencer.Fence(result, toolCall.ToolName);
            transcript.Add(new ChatMessage(ChatRole.Tool, fencedContent, toolCall.ToolCallId, toolCall.ToolName));

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
                    Severity: result.IsError ? AgentEventSeverity.Warning : AgentEventSeverity.Info,
                    AgentId: request.AgentId),
                ct);
        }

        if (plan.Delegations.Count > 0)
        {
            var truncated = _toolResultTruncator.Truncate(transcript, request.ContextWindowChars);
            var repaired = _transcriptSanitizer.Repair(truncated);
            transcript.Clear();
            transcript.AddRange(repaired);
        }

        return new RoutePlanExecutionResult(
            HaltAfterClarification: false,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CacheReadTokens: cacheReadTokens,
            CacheWriteTokens: cacheWriteTokens);
    }

    private async Task<bool> TryAutoCreateSkillsAsync(
        AgentRunRequest request,
        List<ChatMessage> transcript,
        IAgentEventSink sink,
        RunId runId,
        HashSet<string> toolsUsed,
        CancellationToken ct)
    {
        var tools = _toolRegistry.ResolveTools(request.ToolPolicy);
        var createSkillTool = tools.FirstOrDefault(tool =>
            tool.Descriptor.Name.Equals("create_skill", StringComparison.OrdinalIgnoreCase));

        if (createSkillTool is null)
        {
            return false;
        }

        var systemPrompt = BuildAutoSkillPrompt(request, tools);
        var toolDefinitions = new[] { _schemaNormalizer.ToDefinition(createSkillTool, request.Provider) };
        var llmRequest = new LlmRequest(
            runId,
            request.SessionKey,
            request.Provider,
            request.Model,
            systemPrompt,
            transcript,
            toolDefinitions,
            Timeout: TimeSpan.FromMinutes(1),
            MaxOutputTokens: 1024);

        var pendingToolCalls = new List<ToolCall>();

        await foreach (var delta in _llmClient.StreamAsync(llmRequest, ct))
        {
            switch (delta)
            {
                case ToolCallDelta toolCall when toolCall.ToolName.Equals("create_skill", StringComparison.OrdinalIgnoreCase):
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
                                ("toolCallId", toolCall.ToolCallId)),
                            AgentId: request.AgentId),
                        ct);
                    break;

                case LlmErrorDelta error:
                    throw new InvalidOperationException($"LLM returned an error: {error.Error}");
            }
        }

        if (pendingToolCalls.Count == 0)
        {
            return false;
        }

        foreach (var toolCall in pendingToolCalls)
        {
            toolsUsed.Add(toolCall.ToolName);
            transcript.Add(new ChatMessage(ChatRole.Assistant, toolCall.Arguments.GetRawText(), toolCall.ToolCallId, toolCall.ToolName));

            var execContext = new ToolExecutionContext(runId, request.SessionKey, request.Provider, request.Model, request.Caller, SubagentDepth: request.ToolPolicy.SubagentDepth);
            var result = await _toolExecutor.ExecuteAsync(createSkillTool, toolCall, execContext, ct);

            var fencedContent = ContentFencer.Fence(result, toolCall.ToolName);
            transcript.Add(new ChatMessage(ChatRole.Tool, fencedContent, toolCall.ToolCallId, toolCall.ToolName));
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
                    Severity: result.IsError ? AgentEventSeverity.Warning : AgentEventSeverity.Info,
                    AgentId: request.AgentId),
                ct);
        }

        return true;
    }

    private static string BuildAutoSkillPrompt(AgentRunRequest request, IReadOnlyList<ITool> tools)
    {
        var lines = new List<string>
        {
            "You are a tool planner.",
            "Decide whether a new runtime skill is required to satisfy the user request.",
            "If existing tools can satisfy the request, respond with exactly: NO_SKILL_NEEDED",
            "Otherwise, call create_skill with complete C# source code implementing ISkillAddin.",
            "The create_skill arguments MUST be: { \"source_code\": \"<complete C# file>\" }.",
            "Do not answer the user directly."
        };

        lines.Add("");
        lines.Add("User request:");
        lines.Add(request.UserInput);
        lines.Add("");
        lines.Add("Available tools:");

        var availableTools = tools
            .Where(tool => !tool.Descriptor.Name.Equals("create_skill", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (availableTools.Count == 0)
        {
            lines.Add("- none");
        }
        else
        {
            foreach (var tool in availableTools)
            {
                lines.Add($"- {tool.Descriptor.Name}: {tool.Descriptor.Description}");
            }
        }

        lines.Add("");
        lines.Add("Use this template for source_code:");
        lines.Add("using System;");
        lines.Add("using System.Collections.Generic;");
        lines.Add("using System.Text.Json;");
        lines.Add("using System.Threading;");
        lines.Add("using System.Threading.Tasks;");
        lines.Add("using IronPaw.AgentCore.Abstractions;");
        lines.Add("");
        lines.Add("public sealed class MyAddin : ISkillAddin");
        lines.Add("{");
        lines.Add("    public string AddinId => \"my.addin\";");
        lines.Add("    public string Description => \"Short description.\";");
        lines.Add("    public IReadOnlyList<ISkill> CreateSkills() => [new MySkill()];");
        lines.Add("}");
        lines.Add("");
        lines.Add("internal sealed class MySkill : ISkill");
        lines.Add("{");
        lines.Add("    public SkillDescriptor Descriptor { get; } = new(");
        lines.Add("        Name: \"my_skill\",");
        lines.Add("        Description: \"What this skill does.\",");
        lines.Add("        InputSchema: JsonSerializer.SerializeToElement(new");
        lines.Add("        {");
        lines.Add("            type = \"object\",");
        lines.Add("            properties = new { },");
        lines.Add("            required = Array.Empty<string>()");
        lines.Add("        }));");
        lines.Add("");
        lines.Add("    public Task<SkillResult> ExecuteAsync(SkillCall call, SkillExecutionContext ctx, CancellationToken ct)");
        lines.Add("    {");
        lines.Add("        return Task.FromResult(SkillResult.Success(\"ok\"));");
        lines.Add("    }");
        lines.Add("}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildRoutePlanPrompt(AgentRunRequest request, IReadOnlyList<AgentCatalogEntry> agents)
    {
        var lines = new List<string>
        {
            "You are a routing planner for a coding assistant.",
            "Decide whether the request needs clarification before execution.",
            "If clarification is needed, ask up to 3 concise questions and do not delegate.",
            "If clarification is not needed, break work into minimal delegated tasks for available specialist agents.",
            "Return JSON only. No prose, no markdown."
        };

        lines.Add("");
        lines.Add("Available agents:");
        foreach (var agent in agents)
        {
            lines.Add($"- {agent.AgentId}: {agent.DisplayName} — {agent.Description}");
        }

        lines.Add("");
        lines.Add("Output schema:");
        lines.Add("{");
        lines.Add("  \"needs_clarification\": boolean,");
        lines.Add("  \"clarification_questions\": string[],");
        lines.Add("  \"delegations\": [{ \"agent_id\": string, \"task\": string }]");
        lines.Add("}");
        lines.Add("");
        lines.Add("Rules:");
        lines.Add("- If needs_clarification=true, delegations must be an empty array.");
        lines.Add("- If needs_clarification=false, clarification_questions must be an empty array.");
        lines.Add("- Use only listed agent_id values.");
        lines.Add("- Keep delegation count minimal and avoid overlap.");
        lines.Add("");
        lines.Add("User request:");
        lines.Add(request.UserInput);

        return string.Join(Environment.NewLine, lines);
    }

    private static RoutePlan? ParseRoutePlan(string raw, IReadOnlyList<AgentCatalogEntry> agents)
    {
        var json = TryExtractJsonObject(raw);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            var knownAgents = new HashSet<string>(agents.Select(agent => agent.AgentId), StringComparer.OrdinalIgnoreCase);

            var needsClarification = root.TryGetProperty("needs_clarification", out var needsNode) &&
                                     needsNode.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                     needsNode.GetBoolean();

            var questions = new List<string>();
            if (root.TryGetProperty("clarification_questions", out var questionsNode) &&
                questionsNode.ValueKind is JsonValueKind.Array)
            {
                foreach (var item in questionsNode.EnumerateArray())
                {
                    if (item.ValueKind is not JsonValueKind.String)
                    {
                        continue;
                    }

                    var question = item.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(question))
                    {
                        questions.Add(question);
                    }
                }
            }

            var delegations = new List<DelegationPlanEntry>();
            if (root.TryGetProperty("delegations", out var delegationsNode) &&
                delegationsNode.ValueKind is JsonValueKind.Array)
            {
                foreach (var item in delegationsNode.EnumerateArray())
                {
                    if (item.ValueKind is not JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!item.TryGetProperty("agent_id", out var agentNode) ||
                        agentNode.ValueKind is not JsonValueKind.String)
                    {
                        continue;
                    }

                    if (!item.TryGetProperty("task", out var taskNode) ||
                        taskNode.ValueKind is not JsonValueKind.String)
                    {
                        continue;
                    }

                    var agentId = agentNode.GetString()?.Trim();
                    var task = taskNode.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(agentId) ||
                        string.IsNullOrWhiteSpace(task) ||
                        !knownAgents.Contains(agentId))
                    {
                        continue;
                    }

                    delegations.Add(new DelegationPlanEntry(agentId, task));
                }
            }

            if (needsClarification)
            {
                delegations.Clear();
                questions = questions.Take(3).ToList();
                if (questions.Count == 0)
                {
                    questions.Add("Could you clarify the expected outcome and constraints?");
                }
            }
            else
            {
                questions.Clear();
                delegations = delegations
                    .DistinctBy(d => $"{d.AgentId}:{d.Task}", StringComparer.OrdinalIgnoreCase)
                    .Take(4)
                    .ToList();
            }

            return new RoutePlan(needsClarification, questions, delegations);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return raw[start..(end + 1)];
    }

    private static IReadOnlyList<AgentCatalogEntry> ParseAgentCatalog(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<AgentCatalogEntry>();
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind is not JsonValueKind.Array)
            {
                return Array.Empty<AgentCatalogEntry>();
            }

            var agents = new List<AgentCatalogEntry>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind is not JsonValueKind.Object)
                {
                    continue;
                }

                var agentId = item.TryGetProperty("agent_id", out var idNode) && idNode.ValueKind is JsonValueKind.String
                    ? idNode.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(agentId))
                {
                    continue;
                }

                var displayName = item.TryGetProperty("display_name", out var displayNode) && displayNode.ValueKind is JsonValueKind.String
                    ? displayNode.GetString()?.Trim()
                    : agentId;
                var description = item.TryGetProperty("description", out var descriptionNode) && descriptionNode.ValueKind is JsonValueKind.String
                    ? descriptionNode.GetString()?.Trim()
                    : "No description provided.";

                agents.Add(new AgentCatalogEntry(agentId, displayName ?? agentId, description ?? "No description provided."));
            }

            return agents;
        }
        catch (JsonException)
        {
            return Array.Empty<AgentCatalogEntry>();
        }
    }

    private static string BuildClarificationMessage(IReadOnlyList<string> questions)
    {
        var lines = new List<string>
        {
            "I need a bit more detail before I proceed:"
        };

        for (var i = 0; i < questions.Count; i++)
        {
            lines.Add($"{i + 1}. {questions[i]}");
        }

        lines.Add("Reply with answers and I will continue.");
        return string.Join(Environment.NewLine, lines);
    }

    private sealed record AgentCatalogEntry(string AgentId, string DisplayName, string Description);

    private sealed record DelegationPlanEntry(string AgentId, string Task);

    private sealed record RoutePlan(
        bool NeedsClarification,
        IReadOnlyList<string> ClarificationQuestions,
        IReadOnlyList<DelegationPlanEntry> Delegations);

    private sealed record RoutePlanExecutionResult(
        bool HaltAfterClarification,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens)
    {
        public static RoutePlanExecutionResult None { get; } = new(
            HaltAfterClarification: false,
            InputTokens: 0,
            OutputTokens: 0,
            CacheReadTokens: 0,
            CacheWriteTokens: 0);
    }
}
