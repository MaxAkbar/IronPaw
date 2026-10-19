using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tools.Tests;

[TestClass]
public sealed class WebToolsTests
{
    [TestMethod]
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

        Assert.IsTrue(result.IsError);
        Assert.Contains("Invalid action", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.IsNull(browser.LastNavigationRequest);
    }

    [TestMethod]
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

        Assert.IsFalse(result.IsError);
        Assert.AreEqual(ContentTrust.Untrusted, result.Trust);
        Assert.IsNotNull(browser.LastNavigationRequest);
        Assert.AreEqual(WebNavigationAction.Open, browser.LastNavigationRequest!.Action);
        Assert.AreEqual("https://example.com", browser.LastNavigationRequest.Url);
        Assert.AreEqual(65, browser.LastNavigationRequest.SessionId.Length);
        Assert.DoesNotContain(context.ScopeSessionKey.Value, browser.LastNavigationRequest.SessionId, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WebNavigateTool_PassesCssSelectors()
    {
        var browser = new StubWebBrowserService();
        var tool = new WebNavigateTool(browser);
        var call = new ToolCall(
            Guid.NewGuid().ToString("n"),
            "web_navigate",
            JsonSerializer.SerializeToElement(new
            {
                action = "submit_form",
                session_id = "session-1",
                form_selector = "form.search",
                form_index = 0,
                fields = new
                {
                    q = "ironpaw"
                }
            }));

        var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

        Assert.IsFalse(result.IsError);
        Assert.IsNotNull(browser.LastNavigationRequest);
        Assert.AreEqual("form.search", browser.LastNavigationRequest!.FormSelector);
        Assert.AreEqual("ironpaw", browser.LastNavigationRequest.Fields!["q"]);
    }

    [TestMethod]
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

        Assert.IsTrue(result.IsError);
        Assert.Contains("max_items", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.IsNull(browser.LastStructuredRequest);
    }

    [TestMethod]
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

        Assert.IsFalse(result.IsError);
        Assert.AreEqual(ContentTrust.Untrusted, result.Trust);
        Assert.IsNotNull(browser.LastStructuredRequest);
        Assert.AreEqual(3, browser.LastStructuredRequest!.MaxItems);
    }

    [TestMethod]
    public async Task WebExtractStructuredTool_PassesCssSelectors()
    {
        var browser = new StubWebBrowserService();
        var tool = new WebExtractStructuredTool(browser);
        var call = new ToolCall(
            Guid.NewGuid().ToString("n"),
            "web_extract_structured",
            JsonSerializer.SerializeToElement(new
            {
                session_id = "s2",
                root_selector = "#main",
                table_selector = "table.metrics",
                list_selector = "ul.items"
            }));

        var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

        Assert.IsFalse(result.IsError);
        Assert.IsNotNull(browser.LastStructuredRequest);
        Assert.AreEqual("#main", browser.LastStructuredRequest!.RootSelector);
        Assert.AreEqual("table.metrics", browser.LastStructuredRequest.TableSelector);
        Assert.AreEqual("ul.items", browser.LastStructuredRequest.ListSelector);
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

    [TestMethod]
    public async Task ExplicitAliasesAndReturnedIdsCannotCrossCallerRootOrAgentScopes()
    {
        var browser = new StubWebBrowserService();
        var tool = new WebNavigateTool(browser);
        ToolCall Call(string id) => new("call", "web_navigate", JsonSerializer.SerializeToElement(new { action = "open", url = "https://example.com", session_id = id }));
        var context = CreateContext();
        await tool.ExecuteAsync(Call("shared"), context, default);
        var original = browser.LastNavigationRequest!.SessionId;
        await tool.ExecuteAsync(Call(original), context, default);
        Assert.AreEqual(original, browser.LastNavigationRequest!.SessionId);
        foreach (var foreign in new[] { context with { Caller = CallerIdentity.Authenticated("foreign", "Foreign") },
            context with { RootRunId = RunId.New() }, context with { AgentId = "other-agent" } })
        {
            await tool.ExecuteAsync(Call("shared"), foreign, default);
            Assert.AreNotEqual(original, browser.LastNavigationRequest!.SessionId);
            await tool.ExecuteAsync(Call(original), foreign, default);
            Assert.AreNotEqual(original, browser.LastNavigationRequest!.SessionId);
        }
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

        public IReadOnlyList<WebSessionSnapshot> GetSessionSnapshots()
        {
            return Array.Empty<WebSessionSnapshot>();
        }

        public WebSessionSnapshot? GetSessionSnapshot(string sessionId)
        {
            return null;
        }
    }
}
