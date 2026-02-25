using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class InvokeAgentTool : ITool
{
    private readonly IAgentRegistry _agentRegistry;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly int _maxDepth;

    public InvokeAgentTool(
        IAgentRegistry agentRegistry,
        IAgentOrchestrator orchestrator,
        int maxDepth = 3)
    {
        _agentRegistry = agentRegistry;
        _orchestrator = orchestrator;
        _maxDepth = maxDepth;

        Descriptor = new ToolDescriptor(
            Name: "invoke_agent",
            Description: "Delegates a task to a specialist agent. Use list_agents to discover available agents.",
            ParametersSchema: JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    agent_id = new
                    {
                        type = "string",
                        description = "The ID of the agent to invoke."
                    },
                    task = new
                    {
                        type = "string",
                        description = "A clear, self-contained description of the task to delegate."
                    }
                },
                required = new[] { "agent_id", "task" }
            }),
            Group: "group:agents");
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        if (!call.Arguments.TryGetProperty("agent_id", out var agentIdProp) ||
            agentIdProp.ValueKind != JsonValueKind.String)
        {
            return ToolResult.Error("Missing required parameter: agent_id");
        }

        if (!call.Arguments.TryGetProperty("task", out var taskProp) ||
            taskProp.ValueKind != JsonValueKind.String)
        {
            return ToolResult.Error("Missing required parameter: task");
        }

        var agentId = agentIdProp.GetString()!;
        var task = taskProp.GetString()!;

        var agentDef = _agentRegistry.Get(agentId);
        if (agentDef is null)
        {
            return ToolResult.Error($"Agent '{agentId}' not found. Use list_agents to see available agents.");
        }

        var childDepth = context.SubagentDepth + 1;
        if (childDepth > _maxDepth)
        {
            return ToolResult.Error($"Maximum agent delegation depth ({_maxDepth}) exceeded.");
        }

        // Ephemeral session key for transcript isolation.
        var childSessionKey = new SessionKey($"{context.RunId.Value}::{agentId}::{Guid.NewGuid():N}");

        var childPolicy = agentDef.ToolPolicy with
        {
            SubagentDepth = childDepth,
            SenderIsOwner = context.Caller.IsOwner
        };

        var childRequest = new AgentRunRequest(
            SessionKey: childSessionKey,
            UserInput: task,
            Provider: agentDef.Provider ?? context.Provider,
            Model: agentDef.Model ?? context.Model,
            ToolPolicy: childPolicy,
            Caller: context.Caller,
            SystemPromptOverride: agentDef.SystemPrompt,
            MaxOutputTokens: agentDef.MaxOutputTokens,
            MaxRounds: agentDef.MaxRounds,
            AgentId: agentDef.AgentId,
            ParentRunId: context.ScopeRunId,
            ParentSessionKey: context.ScopeSessionKey);

        // Wrap the event sink to tag child events with the agent ID.
        var childSink = new AgentPrefixedEventSink(NullAgentEventSink.Instance, agentDef.AgentId);

        var result = await _orchestrator.RunAsync(childRequest, childSink, ct);

        return ToolResult.Success(
            result.FinalAssistantMessage,
            ContentTrust.Trusted,
            new Dictionary<string, object?>
            {
                ["agent_id"] = agentId,
                ["child_run_id"] = result.RunId.Value,
                ["rounds"] = result.Metadata.GetValueOrDefault("rounds"),
                ["total_tokens"] = result.Metadata.GetValueOrDefault("totalTokens")
            });
    }
}
