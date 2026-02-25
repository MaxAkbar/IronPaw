using System.Net;
using System.Text;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class WebBrowserServiceTests
{
    [Fact]
    public async Task NavigateAsync_OpenThenFollowLink_UsesSessionState()
    {
        var handler = new SequencedHandler(
        [
            HtmlResponse(HttpStatusCode.OK, """
                                           <html>
                                             <head><title>Start</title></head>
                                             <body>
                                               <a href="/next">Go next</a>
                                             </body>
                                           </html>
                                           """),
            HtmlResponse(HttpStatusCode.OK, """
                                           <html>
                                             <head><title>Next</title></head>
                                             <body>done</body>
                                           </html>
                                           """)
        ]);

        var service = new WebBrowserService(
            new HttpClient(handler),
            minHostInterval: TimeSpan.Zero);

        var open = await service.NavigateAsync(
            new WebNavigationRequest(
                SessionId: "s1",
                Action: WebNavigationAction.Open,
                Url: "https://example.com/start"),
            CancellationToken.None);
        var follow = await service.NavigateAsync(
            new WebNavigationRequest(
                SessionId: "s1",
                Action: WebNavigationAction.FollowLink,
                LinkIndex: 0),
            CancellationToken.None);

        Assert.Equal("Start", open.Title);
        Assert.Single(open.Links);
        Assert.Equal("https://example.com/next", follow.Url);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://example.com/start", handler.Requests[0].Url);
        Assert.Equal("https://example.com/next", handler.Requests[1].Url);
    }

    [Fact]
    public async Task NavigateAsync_SubmitForm_PostsMergedFields()
    {
        var handler = new SequencedHandler(
        [
            HtmlResponse(HttpStatusCode.OK, """
                                           <html>
                                             <body>
                                               <form method="post" action="/search">
                                                 <input name="q" value="default" />
                                                 <input name="category" value="all" />
                                               </form>
                                             </body>
                                           </html>
                                           """),
            HtmlResponse(HttpStatusCode.OK, "<html><body>results</body></html>")
        ]);

        var service = new WebBrowserService(
            new HttpClient(handler),
            minHostInterval: TimeSpan.Zero);

        await service.NavigateAsync(
            new WebNavigationRequest(
                SessionId: "form-session",
                Action: WebNavigationAction.Open,
                Url: "https://example.com/index"),
            CancellationToken.None);

        var submit = await service.NavigateAsync(
            new WebNavigationRequest(
                SessionId: "form-session",
                Action: WebNavigationAction.SubmitForm,
                FormIndex: 0,
                Fields: new Dictionary<string, string> { ["q"] = "ironpaw" }),
            CancellationToken.None);

        Assert.Equal("https://example.com/search", handler.Requests[1].Url);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.NotNull(handler.Requests[1].Body);
        Assert.Contains("q=ironpaw", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("category=all", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Equal(200, submit.StatusCode);
    }

    [Fact]
    public async Task NavigateAsync_FollowLink_UsesCssSelector()
    {
        var handler = new SequencedHandler(
        [
            HtmlResponse(HttpStatusCode.OK, """
                                           <html>
                                             <body>
                                               <a class="secondary" href="/ignore">Ignore</a>
                                               <nav><a class="primary-link" href="/target">Target</a></nav>
                                             </body>
                                           </html>
                                           """),
            HtmlResponse(HttpStatusCode.OK, "<html><body>target page</body></html>")
        ]);

        var service = new WebBrowserService(
            new HttpClient(handler),
            minHostInterval: TimeSpan.Zero);

        await service.NavigateAsync(
            new WebNavigationRequest(
                SessionId: "selector-nav",
                Action: WebNavigationAction.Open,
                Url: "https://example.com/start"),
            CancellationToken.None);

        var follow = await service.NavigateAsync(
            new WebNavigationRequest(
                SessionId: "selector-nav",
                Action: WebNavigationAction.FollowLink,
                LinkSelector: "a.primary-link"),
            CancellationToken.None);

        Assert.Equal("https://example.com/target", follow.Url);
    }

    [Fact]
    public async Task ExtractStructuredDataAsync_ReturnsMetadataTablesAndLists()
    {
        var handler = new SequencedHandler(
        [
            HtmlResponse(HttpStatusCode.OK, """
                                           <html lang="en">
                                             <head>
                                               <title>Catalog</title>
                                               <meta name="description" content="Sample catalog page" />
                                             </head>
                                             <body>
                                               <table>
                                                 <tr><th>Name</th><th>Price</th></tr>
                                                 <tr><td>Widget</td><td>12</td></tr>
                                               </table>
                                               <ul>
                                                 <li>One</li>
                                                 <li>Two</li>
                                               </ul>
                                             </body>
                                           </html>
                                           """)
        ]);

        var service = new WebBrowserService(
            new HttpClient(handler),
            minHostInterval: TimeSpan.Zero);

        await service.NavigateAsync(
            new WebNavigationRequest(
                SessionId: "extract",
                Action: WebNavigationAction.Open,
                Url: "https://example.com/catalog"),
            CancellationToken.None);

        var extracted = await service.ExtractStructuredDataAsync(
            new WebStructuredDataRequest(
                SessionId: "extract",
                IncludeMetadata: true,
                IncludeTables: true,
                IncludeLists: true,
                MaxItems: 5),
            CancellationToken.None);

        Assert.Equal("Catalog", extracted.Title);
        Assert.Equal("Sample catalog page", extracted.Metadata["description"]);
        Assert.Single(extracted.Tables);
        Assert.Single(extracted.Lists);
        Assert.Equal("Widget", extracted.Tables[0].Rows[0][0]);
        Assert.Equal("One", extracted.Lists[0].Items[0]);
    }

    [Fact]
    public async Task ExtractStructuredDataAsync_UsesRootAndCssSelectors()
    {
        var handler = new SequencedHandler(
        [
            HtmlResponse(HttpStatusCode.OK, """
                                           <html>
                                             <body>
                                               <section id="ignore">
                                                 <table><tr><td>ignore</td></tr></table>
                                               </section>
                                               <section id="target">
                                                 <table class="pricing">
                                                   <tr><th>Plan</th></tr>
                                                   <tr><td>Pro</td></tr>
                                                 </table>
                                                 <ul class="features">
                                                   <li>Support</li>
                                                 </ul>
                                               </section>
                                             </body>
                                           </html>
                                           """)
        ]);

        var service = new WebBrowserService(
            new HttpClient(handler),
            minHostInterval: TimeSpan.Zero);

        await service.NavigateAsync(
            new WebNavigationRequest(
                SessionId: "selector-extract",
                Action: WebNavigationAction.Open,
                Url: "https://example.com/source"),
            CancellationToken.None);

        var extracted = await service.ExtractStructuredDataAsync(
            new WebStructuredDataRequest(
                SessionId: "selector-extract",
                IncludeMetadata: false,
                IncludeTables: true,
                IncludeLists: true,
                MaxItems: 5,
                RootSelector: "#target",
                TableSelector: "table.pricing",
                ListSelector: "ul.features"),
            CancellationToken.None);

        Assert.Single(extracted.Tables);
        Assert.Single(extracted.Lists);
        Assert.Equal("Pro", extracted.Tables[0].Rows[0][0]);
        Assert.Equal("Support", extracted.Lists[0].Items[0]);
    }

    [Fact]
    public async Task NavigateAsync_RetriesOn429AndThenSucceeds()
    {
        var handler = new SequencedHandler(
        [
            HtmlResponse(HttpStatusCode.TooManyRequests, "<html><body>slow down</body></html>"),
            HtmlResponse(HttpStatusCode.OK, "<html><body>ok</body></html>")
        ]);
        var delays = new List<TimeSpan>();
        var service = new WebBrowserService(
            new HttpClient(handler),
            maxAttempts: 3,
            minHostInterval: TimeSpan.Zero,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await service.NavigateAsync(
            new WebNavigationRequest(
                SessionId: "retry",
                Action: WebNavigationAction.Open,
                Url: "https://example.com/retry"),
            CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.NotEmpty(delays);
    }

    [Fact]
    public async Task NavigateAsync_AppliesHostRateLimitBetweenRequests()
    {
        var handler = new SequencedHandler(
        [
            HtmlResponse(HttpStatusCode.OK, """
                                           <html>
                                             <body>
                                               <a href="/next">Go next</a>
                                             </body>
                                           </html>
                                           """),
            HtmlResponse(HttpStatusCode.OK, "<html><body>next page</body></html>")
        ]);
        var delays = new List<TimeSpan>();
        var service = new WebBrowserService(
            new HttpClient(handler),
            maxAttempts: 1,
            minHostInterval: TimeSpan.FromSeconds(2),
            timeProvider: new FixedTimeProvider(new DateTimeOffset(2026, 2, 25, 12, 0, 0, TimeSpan.Zero)),
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await service.NavigateAsync(
            new WebNavigationRequest(
                SessionId: "rate-limit",
                Action: WebNavigationAction.Open,
                Url: "https://example.com/start"),
            CancellationToken.None);

        await service.NavigateAsync(
            new WebNavigationRequest(
                SessionId: "rate-limit",
                Action: WebNavigationAction.FollowLink,
                LinkIndex: 0),
            CancellationToken.None);

        Assert.Contains(delays, delay => delay == TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GetSessionSnapshots_ReturnsLoadedSessionSummary()
    {
        var handler = new SequencedHandler(
        [
            HtmlResponse(HttpStatusCode.OK, """
                                           <html>
                                             <head><title>Snapshot</title></head>
                                             <body>
                                               <a href="/n">n</a>
                                               <form action="/f"></form>
                                             </body>
                                           </html>
                                           """)
        ]);
        var service = new WebBrowserService(
            new HttpClient(handler),
            minHostInterval: TimeSpan.Zero);

        await service.NavigateAsync(
            new WebNavigationRequest(
                SessionId: "snapshot-session",
                Action: WebNavigationAction.Open,
                Url: "https://example.com/snapshot"),
            CancellationToken.None);

        var all = service.GetSessionSnapshots();
        var single = service.GetSessionSnapshot("snapshot-session");

        Assert.Contains(all, session => session.SessionId == "snapshot-session");
        Assert.NotNull(single);
        Assert.Equal("Snapshot", single!.Title);
        Assert.Equal(1, single.LinkCount);
        Assert.Equal(1, single.FormCount);
    }

    private static HttpResponseMessage HtmlResponse(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html")
        };
    }

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequencedHandler(IEnumerable<HttpResponseMessage> responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No response queued for request.");
            }

            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new CapturedRequest(
                Method: request.Method,
                Url: request.RequestUri?.ToString() ?? string.Empty,
                Body: body));

            var response = _responses.Dequeue();
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Url,
        string? Body);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
