using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class ListAgentsTool : ITool
{
    private readonly IAgentRegistry _registry;

    public ListAgentsTool(IAgentRegistry registry)
    {
        _registry = registry;

        Descriptor = new ToolDescriptor(
            Name: "list_agents",
            Description: "Lists all registered agents that can be invoked via invoke_agent.",
            ParametersSchema: JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }),
            Group: "group:agents");
    }

    public ToolDescriptor Descriptor { get; }

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        var agents = _registry.GetAll();

        if (agents.Count == 0)
        {
            return Task.FromResult(ToolResult.Success("No agents registered."));
        }

        var entries = agents.Select(a => new
        {
            agent_id = a.AgentId,
            display_name = a.DisplayName,
            description = a.Description
        });

        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        return Task.FromResult(ToolResult.Success(json));
    }
}
