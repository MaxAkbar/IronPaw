using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public interface IBoundWebBrowserService : IWebBrowserService
{
    string? BrowserBinding { get; }
}

public interface IBrowserBoundTool : ITool
{
    string? BrowserBinding { get; }
}

public interface IWebBrowserRunScope
{
    void BindSession(string sessionId, RunId rootRunId);
}
