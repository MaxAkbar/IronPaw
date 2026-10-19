using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;
using IronPaw.Testing;

namespace IronPaw.AgentCore.Browser.Tests;

[TestClass]
public sealed class ChromiumBrowserTests
{
    private static BrowserConfiguration Options(LoopbackHttpServer server) => new(1, [server.Origin], AllowPrivateNetwork: true, TimeoutSeconds: 8);

    [TestMethod]
    public async Task RealChromiumExecutesJavaScriptClicksAndExtractsRenderedTables()
    {
        await using var server = new LoopbackHttpServer(_ => Task.FromResult(new LoopbackHttpServer.Response("""
            <html><head><title>before</title></head><body><div id="root"></div>
            <script>setTimeout(() => { document.title='Rendered'; document.querySelector('#root').innerHTML=
            '<table><tr><th>Value</th></tr><tr><td id="rendered">42</td></tr></table><ul><li>dynamic item</li></ul><button id="append">Add</button>';
            document.querySelector('#append').onclick=()=>{document.querySelector('#rendered').textContent='43'; document.querySelector('#rendered').id='added';}; },40);</script></body></html>
            """)));
        await using var browser = new ChromiumBrowserService(Options(server));
        var opened = await browser.NavigateAsync(new("session", WebNavigationAction.Open, server.Origin) { WaitSelector = "#rendered" }, default);
        Assert.AreEqual("Rendered", opened.Title);
        var extracted = await browser.ExtractStructuredDataAsync(new("session"), default);
        Assert.AreEqual("42", Assert.ContainsSingle(extracted.Tables).Rows[0][0]);
        Assert.AreEqual("dynamic item", Assert.ContainsSingle(extracted.Lists).Items[0]);
        await browser.NavigateAsync(new("session", WebNavigationAction.Click, LinkSelector: "#append") { WaitSelector = "#added" }, default);
        Assert.AreEqual("43", (await browser.ExtractStructuredDataAsync(new("session"), default)).Tables[0].Rows[0][0]);
    }

    [TestMethod]
    public async Task CookiesAndLocalStorageAreIsolatedAcrossNonPersistentContexts()
    {
        await using var server = new LoopbackHttpServer(request => Task.FromResult(request.Path.StartsWith("/set", StringComparison.Ordinal)
            ? new LoopbackHttpServer.Response("<script>localStorage.setItem('private','only-a'); document.title='set';</script>", Headers: new Dictionary<string, string> { ["Set-Cookie"] = "session=only-a; Path=/" })
            : new LoopbackHttpServer.Response("<script>document.title=(document.cookie||'none')+':'+(localStorage.getItem('private')||'none');</script>")));
        await using var browser = new ChromiumBrowserService(Options(server));
        await browser.NavigateAsync(new("a", WebNavigationAction.Open, server.Origin + "/set"), default);
        var same = await browser.NavigateAsync(new("a", WebNavigationAction.Open, server.Origin + "/check"), default);
        var separate = await browser.NavigateAsync(new("b", WebNavigationAction.Open, server.Origin + "/check"), default);
        Assert.AreEqual("session=only-a:only-a", same.Title);
        Assert.AreEqual("none:none", separate.Title);
        Assert.AreEqual(2, browser.GetSessionSnapshots().Count);
    }

    [TestMethod]
    public async Task OutOfGrantOriginsAndRedirectsAreNeverFetched()
    {
        await using var forbidden = new LoopbackHttpServer(_ => Task.FromResult(new LoopbackHttpServer.Response("private")));
        await using var allowed = new LoopbackHttpServer(request => Task.FromResult(request.Path == "/redirect"
            ? new LoopbackHttpServer.Response("", 302, Headers: new Dictionary<string, string> { ["Location"] = forbidden.Origin })
            : new LoopbackHttpServer.Response($"<title>Allowed</title><script src='{forbidden.Origin}/private.js'></script>")));
        await using var browser = new ChromiumBrowserService(Options(allowed));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => browser.NavigateAsync(new("bad", WebNavigationAction.Open, forbidden.Origin), default));
        await browser.NavigateAsync(new("ok", WebNavigationAction.Open, allowed.Origin), default);
        await Assert.ThrowsAsync<Exception>(() => browser.NavigateAsync(new("redirect", WebNavigationAction.Open, allowed.Origin + "/redirect"), default));
        Assert.IsEmpty(forbidden.Requests);
    }

    [TestMethod]
    public async Task ReadOnlyConfigurationBlocksScriptedPostBeforeEffect()
    {
        await using var server = new LoopbackHttpServer(_ => Task.FromResult(new LoopbackHttpServer.Response("<script>fetch('/effect',{method:'POST',body:'effect'}).catch(()=>{});</script>")));
        await using var browser = new ChromiumBrowserService(Options(server));
        await Assert.ThrowsExactlyAsync<IOException>(() => browser.NavigateAsync(new("read-only", WebNavigationAction.Open, server.Origin), default));
        Assert.DoesNotContain(request => request.Method == "POST", server.Requests);
    }

    [TestMethod]
    public async Task CancellationClosesSessionAndCannotLeaveBackgroundRequestsRunning()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LoopbackHttpServer? server = null;
        await using (server = new LoopbackHttpServer(async _ => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, server!.Token); return new(""); }))
        await using (var browser = new ChromiumBrowserService(Options(server)))
        {
            using var cancellation = new CancellationTokenSource();
            var pending = browser.NavigateAsync(new("cancel", WebNavigationAction.Open, server.Origin), cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsEmpty(browser.GetSessionSnapshots());
            Assert.AreEqual(1, server.Requests.Count);
        }
    }

    [TestMethod]
    public async Task TerminalRootCleanupReleasesItsBrowserCapacityAndSessionData()
    {
        await using var server = new LoopbackHttpServer(_ => Task.FromResult(new LoopbackHttpServer.Response("<title>Ready</title>")));
        using var runtime = new BrowserRuntime();
        runtime.Enable(Options(server) with { MaxSessions = 1 });
        var root = RunId.New();
        runtime.BindSession("first", root);
        await runtime.NavigateAsync(new("first", WebNavigationAction.Open, server.Origin), default);
        await runtime.ReleaseRunAsync(root);
        Assert.IsEmpty(runtime.GetSessionSnapshots());
        Assert.IsNull(runtime.CleanupWarning);
        await runtime.NavigateAsync(new("next", WebNavigationAction.Open, server.Origin), default);
        Assert.AreEqual("Ready", runtime.GetSessionSnapshot("next")!.Title);
    }

    [TestMethod]
    public async Task SessionAndResponseLimitsFailExplicitly()
    {
        await using var server = new LoopbackHttpServer(request => Task.FromResult(new LoopbackHttpServer.Response(request.Path == "/large" ? new string('x', 9000) : "<title>OK</title>")));
        await using var browser = new ChromiumBrowserService(Options(server) with { MaxSessions = 1, MaxResponseBytes = 4096 });
        await browser.NavigateAsync(new("one", WebNavigationAction.Open, server.Origin), default);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => browser.NavigateAsync(new("two", WebNavigationAction.Open, server.Origin), default));
        await Assert.ThrowsAsync<Exception>(() => browser.NavigateAsync(new("one", WebNavigationAction.Open, server.Origin + "/large"), default));
        Assert.IsEmpty(browser.GetSessionSnapshots());
    }

    [TestMethod]
    public async Task SavedAndInheritedPoliciesMustGrantTheExactBrowserConfiguration()
    {
        using var runtime = new BrowserRuntime();
        var config = new BrowserConfiguration(1, ["https://example.com"]);
        runtime.Enable(config);
        var tools = new InMemoryToolRegistry([new WebNavigateTool(runtime)]);
        var broad = new ToolSelectionPolicy(ToolProfile.Full, SenderIsOwner: true);
        Assert.IsEmpty(tools.ResolveTools(broad));
        var accepted = runtime.Grant(broad);
        Assert.ContainsSingle(tools.ResolveTools(accepted));
        Assert.IsEmpty(tools.ResolveTools(accepted with { InheritedPolicy = broad }));
        Assert.IsEmpty(tools.ResolveTools(accepted with { BrowserBinding = (config with { AllowMutations = true }).Binding }));
        var call = new ToolCall("no-grant", "web_navigate", JsonSerializer.SerializeToElement(new { action = "open", url = "https://example.com" }));
        Assert.IsTrue((await new WebNavigateTool(runtime).ExecuteAsync(call,
            new(RunId.New(), new("saved"), "local", "fixture", CallerIdentity.LocalOwner(), ToolPolicy: broad), default)).IsError);
        Assert.IsEmpty(runtime.RequireApprovals([]));
        using var mutating = new BrowserRuntime();
        mutating.Enable(config with { AllowMutations = true });
        Assert.Contains("web_navigate", mutating.RequireApprovals([]));
    }

    [TestMethod]
    public async Task SavedJavaScriptGrantCannotFallBackToAnUnconfiguredHtmlBrowser()
    {
        using var html = new BrowserRuntime();
        var saved = new ToolSelectionPolicy(ToolProfile.Full, SenderIsOwner: true) { BrowserBinding = new BrowserConfiguration(1, ["https://example.com"]).Binding };
        ITool[] tools = [new WebNavigateTool(html), new WebExtractStructuredTool(html)];
        var registry = new InMemoryToolRegistry(tools);
        Assert.IsEmpty(registry.ResolveTools(saved));
        Assert.IsEmpty(registry.ResolveTools(saved with { BrowserBinding = null, InheritedPolicy = saved }));
        foreach (var tool in tools)
        {
            var call = new ToolCall("saved", tool.Descriptor.Name, JsonSerializer.SerializeToElement(new { action = "open", url = "https://example.invalid/never" }));
            var result = await tool.ExecuteAsync(call, new(RunId.New(), new("saved"), "local", "fixture", CallerIdentity.LocalOwner(), ToolPolicy: saved), default);
            Assert.IsTrue(result.IsError);
            Assert.Contains("not granted", result.Content);
        }
        Assert.IsEmpty(html.GetSessionSnapshots());
    }

    [TestMethod]
    public async Task ReviewedGetRedirectStartsAFreshMediatedNavigation()
    {
        await using var server = new LoopbackHttpServer(request => Task.FromResult(request.Path == "/start"
            ? new LoopbackHttpServer.Response("", 302, Headers: new Dictionary<string, string> { ["Location"] = "/finish" })
            : new LoopbackHttpServer.Response("<title>Finished</title><div id='ready'>arrived</div>")));
        await using var browser = new ChromiumBrowserService(Options(server));
        var result = await browser.NavigateAsync(new("redirect", WebNavigationAction.Open, server.Origin + "/start") { WaitSelector = "#ready" }, default);
        Assert.AreEqual(server.Origin + "/finish", result.Url);
        Assert.AreEqual("Finished", result.Title);
        Assert.AreSequenceEqual(new[] { "/start", "/finish" }, server.Requests.Select(request => request.Path));
    }

    [TestMethod]
    public async Task ExplicitMutationGrantSubmitsAFormOnceAndReturnsTheResultingPage()
    {
        await using var server = new LoopbackHttpServer(request => Task.FromResult(request.Method == "POST"
            ? new LoopbackHttpServer.Response("<title>Submitted</title><div id='done'>recorded</div>")
            : new LoopbackHttpServer.Response("<form method='post' action='/effect'><input name='q'><button>Submit</button></form>")));
        await using var browser = new ChromiumBrowserService(Options(server) with { AllowMutations = true });
        await browser.NavigateAsync(new("form", WebNavigationAction.Open, server.Origin), default);
        var result = await browser.NavigateAsync(new("form", WebNavigationAction.SubmitForm, Fields: new Dictionary<string, string> { ["q"] = "reviewed value" }) { WaitSelector = "#done" }, default);
        Assert.AreEqual("Submitted", result.Title);
        var effect = Assert.ContainsSingle(request => request.Method == "POST", server.Requests);
        Assert.Contains("q=reviewed+value", effect.Body);
    }

    [TestMethod]
    public async Task TimerRequestsAfterAnActionCannotAcquireNewNetworkAuthority()
    {
        await using var server = new LoopbackHttpServer(_ => Task.FromResult(new LoopbackHttpServer.Response("<title>Ready</title><script>setTimeout(()=>fetch('/late').catch(()=>{}),1500)</script>")));
        await using var browser = new ChromiumBrowserService(Options(server));
        await browser.NavigateAsync(new("background", WebNavigationAction.Open, server.Origin), default);
        await Task.Delay(1600);
        Assert.DoesNotContain(request => request.Path == "/late", server.Requests);
    }

    [TestMethod]
    public void OldPolicySerializationRemainsByteCompatibleWithoutABrowserGrant()
    {
        var policy = new ToolSelectionPolicy(ToolProfile.Web);
        var json = JsonSerializer.Serialize(policy);
        Assert.DoesNotContain("BrowserBinding", json);
        var granted = policy with { BrowserBinding = new BrowserConfiguration(1, ["https://example.com"]).Binding };
        var saved = JsonSerializer.Deserialize<ToolSelectionPolicy>(JsonSerializer.Serialize(granted));
        Assert.AreEqual(granted, saved);
    }
}
