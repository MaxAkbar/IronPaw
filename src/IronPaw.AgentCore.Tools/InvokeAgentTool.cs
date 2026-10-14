using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class InvokeAgentTool : IDurableDelegationTool
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

        var childRunId = context.ToolExecutionId is null ? (RunId?)null
            : WorkflowIdentity.ChildRunId(context.RunId, context.ToolExecutionId);
        if (childRunId is not null && _orchestrator is IResumableAgentOrchestrator resumable)
        {
            var acceptedChild = (await resumable.ListWorkflowsAsync(ct)).SingleOrDefault(child => child.RunId == childRunId.Value.Value);
            if (acceptedChild is not null)
            {
                if (acceptedChild.ParentRunId != context.RunId.Value || acceptedChild.RootRunId != context.ScopeRunId.Value ||
                    acceptedChild.Request.AgentId != agentId || acceptedChild.Request.UserInput != task)
                    throw new WorkflowConflictException("The recorded child invocation does not match the delegated operation.");
                return await RunChildAsync(() => resumable.ResumeAsync(childRunId.Value, context.Caller,
                    new AgentPrefixedEventSink(context.EventSink ?? NullAgentEventSink.Instance, agentId), ct), agentId, childRunId);
            }
        }

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

        if (context.ToolPolicy is null)
        {
            return ToolResult.Error("Delegation requires the parent tool policy.");
        }

        // Ephemeral session key for transcript isolation.
        var childSessionKey = new SessionKey($"{context.RunId.Value}::{agentId}::{childRunId?.Value ?? Guid.NewGuid().ToString("n")}");

        var childPolicy = agentDef.ToolPolicy with
        {
            SubagentDepth = childDepth,
            SenderIsOwner = context.Caller.IsOwner,
            InheritedPolicy = context.ToolPolicy
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
            ParentRunId: context.RunId,
            ParentSessionKey: context.ScopeSessionKey,
            RootRunId: context.RootRunId ?? context.ScopeRunId,
            Budget: new WorkflowBudget(MaxModelCalls: agentDef.MaxRounds + 2),
            RequestedRunId: childRunId);

        // Wrap the event sink to tag child events with the agent ID.
        var childSink = new AgentPrefixedEventSink(context.EventSink ?? NullAgentEventSink.Instance, agentDef.AgentId);

        return await RunChildAsync(() => _orchestrator.RunAsync(childRequest, childSink, ct), agentId, childRunId);
    }

    private static async Task<ToolResult> RunChildAsync(Func<Task<AgentRunResult>> run, string agentId, RunId? childRunId)
    {
        AgentRunResult result;
        try { result = await run(); }
        catch (WorkflowReconciliationRequiredException error) when (childRunId is not null)
        { throw new WorkflowSuspendedException(WorkflowStatus.NeedsReconciliation, error.Message, childRunId.Value.Value); }
        if (childRunId is not null && result.Status is (AgentRunStatus.AwaitingInput or AgentRunStatus.Paused))
            throw new WorkflowSuspendedException(result.Status == AgentRunStatus.AwaitingInput
                ? WorkflowStatus.AwaitingInput : WorkflowStatus.Paused, result.FinalAssistantMessage, result.RunId.Value);

        return new ToolResult(
            IsError: result.Status != AgentRunStatus.Succeeded,
            Content: result.FinalAssistantMessage,
            Trust: ContentTrust.Local,
            Details: new Dictionary<string, object?>
            {
                ["agent_id"] = agentId,
                ["child_run_id"] = result.RunId.Value,
                ["status"] = result.Status.ToString(),
                ["rounds"] = result.Metadata.GetValueOrDefault("rounds"),
                ["total_tokens"] = result.Metadata.GetValueOrDefault("totalTokens")
            });
    }
}
