using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class InMemoryToolRegistry : IToolRegistry
{
    private readonly IReadOnlyList<ITool> _tools;

    public InMemoryToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = tools.ToArray();
    }

    public IReadOnlyList<ITool> ResolveTools(ToolSelectionPolicy policy)
    {
        return ToolPolicyEvaluator.Apply(_tools, policy);
    }
}
