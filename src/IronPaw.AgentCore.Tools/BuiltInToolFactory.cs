using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public static class BuiltInToolFactory
{
    public static IReadOnlyList<ITool> CreateDefault(string workspaceRoot, IEnumerable<ITool>? additionalTools = null)
    {
        var tools = new List<ITool>
        {
            new CurrentTimeTool(),
            new ListFilesTool(workspaceRoot),
            new ReadFileTool(workspaceRoot),
            new WriteFileTool(workspaceRoot)
        };

        if (additionalTools is not null)
        {
            tools.AddRange(additionalTools);
        }

        return tools;
    }
}
