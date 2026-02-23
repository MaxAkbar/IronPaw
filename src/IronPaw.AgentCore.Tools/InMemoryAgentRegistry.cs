using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class InMemoryAgentRegistry : IAgentRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, AgentDefinition> _agents = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AgentDefinition> GetAll()
    {
        lock (_lock)
        {
            return _agents.Values.ToList();
        }
    }

    public AgentDefinition? Get(string agentId)
    {
        lock (_lock)
        {
            return _agents.GetValueOrDefault(agentId);
        }
    }

    public void Register(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        lock (_lock)
        {
            _agents[definition.AgentId] = definition;
        }
    }
}
