using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public static class BuiltInToolFactory
{
    public static IReadOnlyList<ITool> CreateDefault(
        string workspaceRoot,
        IEnumerable<ITool>? additionalTools = null,
        IMemoryStore? memoryStore = null,
        IRunHistoryStore? runHistoryStore = null)
    {
        var webBrowser = new WebBrowserService();
        var tools = new List<ITool>
        {
            new CurrentTimeTool(),
            new ListFilesTool(workspaceRoot),
            new ReadFileTool(workspaceRoot),
            new WriteFileTool(workspaceRoot),
            new WebNavigateTool(webBrowser),
            new WebExtractStructuredTool(webBrowser)
        };

        if (memoryStore is not null)
        {
            tools.Add(new SaveMemoryTool(memoryStore));
            tools.Add(new RecallMemoryTool(memoryStore));
            tools.Add(new ForgetMemoryTool(memoryStore));
        }

        if (runHistoryStore is not null)
        {
            tools.Add(new ReviewRunsTool(runHistoryStore));
            tools.Add(new ReviewRunDetailTool(runHistoryStore));
        }

        if (additionalTools is not null)
        {
            tools.AddRange(additionalTools);
        }

        return tools;
    }
}
