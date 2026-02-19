using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class InMemoryToolRegistry : IToolRegistry, IToolRegistrar
{
    private readonly object _lock = new();
    private readonly List<ITool> _tools;

    public InMemoryToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = tools.ToList();
    }

    public IReadOnlyList<ITool> ResolveTools(ToolSelectionPolicy policy)
    {
        List<ITool> snapshot;
        lock (_lock)
        {
            snapshot = _tools.ToList();
        }

        return ToolPolicyEvaluator.Apply(snapshot, policy);
    }

    public void RegisterTools(IEnumerable<ITool> tools)
    {
        lock (_lock)
        {
            _tools.AddRange(tools);
        }
    }
}
