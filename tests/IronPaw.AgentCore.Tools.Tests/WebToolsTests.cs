using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class WebToolsTests
{
    [Fact]
    public async Task WebNavigateTool_InvalidAction_ReturnsError()
    {
        var browser = new StubWebBrowserService();
        var tool = new WebNavigateTool(browser);
        var call = new ToolCall(
            Guid.NewGuid().ToString("n"),
            "web_navigate",
            JsonSerializer.SerializeToElement(new
            {
                action = "bad_action",
                url = "https://example.com"
            }));

        var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Invalid action", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Null(browser.LastNavigationRequest);
    }

    [Fact]
    public async Task WebNavigateTool_Success_UsesGeneratedSessionAndUntrustedTrust()
    {
        var browser = new StubWebBrowserService();
        var tool = new WebNavigateTool(browser);
        var call = new ToolCall(
            Guid.NewGuid().ToString("n"),
            "web_navigate",
            JsonSerializer.SerializeToElement(new
            {
                action = "open",
                url = "https://example.com"
            }));

        var context = CreateContext();
        var result = await tool.ExecuteAsync(call, context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(ContentTrust.Untrusted, result.Trust);
        Assert.NotNull(browser.LastNavigationRequest);
        Assert.Equal(WebNavigationAction.Open, browser.LastNavigationRequest!.Action);
        Assert.Equal("https://example.com", browser.LastNavigationRequest.Url);
        Assert.Contains(context.ScopeRunId.Value, browser.LastNavigationRequest.SessionId, StringComparison.Ordinal);
        Assert.Contains(context.ScopeSessionKey.Value, browser.LastNavigationRequest.SessionId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebExtractStructuredTool_InvalidMaxItems_ReturnsError()
    {
        var browser = new StubWebBrowserService();
        var tool = new WebExtractStructuredTool(browser);
        var call = new ToolCall(
            Guid.NewGuid().ToString("n"),
            "web_extract_structured",
            JsonSerializer.SerializeToElement(new
            {
                max_items = "oops"
            }));

        var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("max_items", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Null(browser.LastStructuredRequest);
    }

    [Fact]
    public async Task WebExtractStructuredTool_Success_ReturnsUntrustedTrust()
    {
        var browser = new StubWebBrowserService();
        var tool = new WebExtractStructuredTool(browser);
        var call = new ToolCall(
            Guid.NewGuid().ToString("n"),
            "web_extract_structured",
            JsonSerializer.SerializeToElement(new
            {
                include_metadata = true,
                include_tables = true,
                include_lists = true,
                max_items = 3
            }));

        var result = await tool.ExecuteAsync(call, CreateContext(agentId: "web_browser"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(ContentTrust.Untrusted, result.Trust);
        Assert.NotNull(browser.LastStructuredRequest);
        Assert.Equal(3, browser.LastStructuredRequest!.MaxItems);
    }

    private static ToolExecutionContext CreateContext(string? agentId = "web_browser")
    {
        return new ToolExecutionContext(
            RunId: new RunId("child-run"),
            SessionKey: new SessionKey("child-session"),
            Provider: "local",
            Model: "rule-based-v1",
            Caller: CallerIdentity.LocalOwner(),
            SubagentDepth: 1,
            AgentId: agentId,
            ParentRunId: new RunId("root-run"),
            ParentSessionKey: new SessionKey("root-session"));
    }

    private sealed class StubWebBrowserService : IWebBrowserService
    {
        public WebNavigationRequest? LastNavigationRequest { get; private set; }

        public WebStructuredDataRequest? LastStructuredRequest { get; private set; }

        public Task<WebNavigationResult> NavigateAsync(WebNavigationRequest request, CancellationToken ct)
        {
            LastNavigationRequest = request;
            return Task.FromResult(
                new WebNavigationResult(
                    SessionId: request.SessionId,
                    Url: request.Url ?? "https://example.com",
                    StatusCode: 200,
                    Title: "Example",
                    Description: "example page",
                    RetrievedAt: DateTimeOffset.UtcNow,
                    Links:
                    [
                        new WebLinkSummary(0, "Next", "/next", "https://example.com/next")
                    ],
                    Forms: []));
        }

        public Task<WebStructuredDataResult> ExtractStructuredDataAsync(WebStructuredDataRequest request, CancellationToken ct)
        {
            LastStructuredRequest = request;
            return Task.FromResult(
                new WebStructuredDataResult(
                    SessionId: request.SessionId,
                    Url: "https://example.com",
                    Title: "Example",
                    RetrievedAt: DateTimeOffset.UtcNow,
                    Metadata: new Dictionary<string, string> { ["description"] = "sample" },
                    Tables:
                    [
                        new WebTableData(0, ["A"], [["1"]])
                    ],
                    Lists:
                    [
                        new WebListData(0, "ul", ["x", "y"])
                    ]));
        }
    }
}
