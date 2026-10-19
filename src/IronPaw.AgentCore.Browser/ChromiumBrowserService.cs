using System.Collections.Concurrent;
using System.Text;
using IronPaw.AgentCore.Tools;
using Microsoft.Playwright;

namespace IronPaw.AgentCore.Browser;

public sealed class ChromiumBrowserService : IWebBrowserService, IAsyncDisposable
{
    public BrowserConfiguration Configuration { get; }
    private readonly BrowserNetwork _network;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WebSessionSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _creation = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private int _disposed;

    public ChromiumBrowserService(BrowserConfiguration configuration)
    {
        Configuration = configuration.RequireValid();
        _network = new(Configuration);
    }
    public IReadOnlyList<WebSessionSnapshot> GetSessionSnapshots() => _snapshots.Values.OrderByDescending(s => s.RetrievedAt).ToArray();
    public WebSessionSnapshot? GetSessionSnapshot(string sessionId) => _snapshots.GetValueOrDefault(sessionId);
    public async Task CloseSessionAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        await session.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            session.EndAdmission();
            await session.Context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            _sessions.TryRemove(sessionId, out _);
            _snapshots.TryRemove(sessionId, out _);
        }
        catch { session.Faulted = true; throw; }
        finally { session.Gate.Release(); }
    }

    /// <summary>Checks the installed driver and browser with static JavaScript; all network admission stays closed.</summary>
    public async Task VerifyInstallationAsync(CancellationToken ct)
    {
        var id = "installation-" + Guid.NewGuid().ToString("N");
        var session = await GetSessionAsync(id, ct).ConfigureAwait(false);
        await WithSessionAsync(id, session, async token =>
        {
            await session.Page.SetContentAsync("<button id='check' onclick=\"document.title='Browser ready'\">Check</button>").ConfigureAwait(false);
            await session.Page.Locator("#check").ClickAsync().ConfigureAwait(false);
            if (await session.Page.TitleAsync().ConfigureAwait(false) != "Browser ready") throw new IOException("The packaged JavaScript browser did not execute the installation probe.");
            token.ThrowIfCancellationRequested();
            return true;
        }, ct).ConfigureAwait(false);
        _sessions.TryRemove(id, out _);
        await session.Context.CloseAsync().ConfigureAwait(false);
    }

    private async Task<Session> GetSessionAsync(string id, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 512) throw new ArgumentException("A bounded browser session ID is required.");
        await _creation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_sessions.TryGetValue(id, out var existing))
            {
                if (existing.Faulted) throw new InvalidOperationException("This browser context could not be closed safely. Restart the host before using it again.");
                return existing;
            }
            if (_sessions.Count >= Configuration.MaxSessions) throw new InvalidOperationException("Browser session limit reached. Restart the host to clear the reviewed browser sessions.");
            _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
            _browser ??= await _playwright.Chromium.LaunchAsync(new()
            {
                Headless = true,
                ChromiumSandbox = true,
                Timeout = 15_000,
                ExecutablePath = BundledExecutable(),
                Args = ["--disable-quic", "--disable-background-networking", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp"]
            }).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var context = await _browser.NewContextAsync(new()
            {
                AcceptDownloads = false,
                ServiceWorkers = ServiceWorkerPolicy.Block,
                JavaScriptEnabled = true,
                IgnoreHTTPSErrors = false
            }).ConfigureAwait(false);
            try
            {
                var session = new Session(context);
                await context.RouteAsync("**/*", route => _network.SendAsync(route, session)).ConfigureAwait(false);
                await context.RouteWebSocketAsync("**/*", socket => socket.CloseAsync()).ConfigureAwait(false);
                await context.AddInitScriptAsync("""
                    for (const name of ['RTCPeerConnection', 'webkitRTCPeerConnection', 'Worker', 'SharedWorker'])
                        Object.defineProperty(globalThis, name, { value: undefined, configurable: false, writable: false });
                    """).ConfigureAwait(false);
                session.Page = await context.NewPageAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                session.Page.Dialog += (_, dialog) => { _ = dialog.DismissAsync(); };
                context.Page += (_, page) => { if (page != session.Page) _ = page.CloseAsync(); };
                _sessions[id] = session;
                return session;
            }
            catch { await context.CloseAsync().ConfigureAwait(false); throw; }
        }
        finally { _creation.Release(); }
    }

    private static string? BundledExecutable()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "browser-runtime", "chrome-headless-shell.exe"));
        return File.Exists(path) ? path : null;
    }

    public async Task<WebNavigationResult> NavigateAsync(WebNavigationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var actionDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        actionDeadline.CancelAfter(TimeSpan.FromSeconds(Configuration.TimeoutSeconds));
        if (request.Action == WebNavigationAction.Open && !_network.Allowed(request.Url ?? ""))
            throw new ArgumentException("Navigation target is outside the reviewed browser origins.");
        if (request.WaitSelector?.Length > 512 || request.Fields?.Count > 64 || request.Fields?.Any(p => p.Key.Length > 256 || p.Value.Length > 4096) == true)
            throw new ArgumentException("Browser selectors or form fields exceed their limits.");
        var session = await GetSessionAsync(request.SessionId, actionDeadline.Token).WaitAsync(actionDeadline.Token).ConfigureAwait(false);
        return await WithSessionAsync(request.SessionId, session, async token =>
        {
            session.BeginAction(token);
            if (request.Action == WebNavigationAction.Open)
                await session.Page.GotoAsync(request.Url!, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = Configuration.TimeoutSeconds * 1000 }).ConfigureAwait(false);
            else if (request.Action is WebNavigationAction.Click or WebNavigationAction.FollowLink)
            {
                if (request.LinkIndex is < 0 || (request.LinkIndex is null && string.IsNullOrWhiteSpace(request.LinkText) && string.IsNullOrWhiteSpace(request.LinkSelector)))
                    throw new ArgumentException("Select a link or button by CSS selector, index or text.");
                var locator = session.Page.Locator(request.LinkSelector ?? "a[href]");
                if (!string.IsNullOrWhiteSpace(request.LinkText)) locator = locator.Filter(new() { HasText = request.LinkText });
                await locator.Nth(request.LinkIndex ?? 0).ClickAsync(new() { Timeout = Configuration.TimeoutSeconds * 1000 }).ConfigureAwait(false);
            }
            else if (request.Action == WebNavigationAction.SubmitForm)
            {
                if (request.FormIndex is < 0) throw new ArgumentException("Form index must be nonnegative.");
                var form = session.Page.Locator(request.FormSelector ?? "form").Nth(request.FormIndex ?? 0);
                var method = await form.GetAttributeAsync("method").ConfigureAwait(false) ?? "get";
                if (!Configuration.AllowMutations && !method.Equals("get", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("This form requires a reviewed mutation grant.");
                foreach (var field in request.Fields ?? new Dictionary<string, string>())
                {
                    // Names and values are data arguments, never interpolated JavaScript or selectors.
                    await form.EvaluateAsync("""
                        (form, field) => {
                            const element = form.elements.namedItem(field.name);
                            if (!element || element instanceof RadioNodeList) throw new Error('Select a unique named form field');
                            if (element.type === 'file') throw new Error('File uploads are disabled');
                            if (element.type === 'checkbox') element.checked = field.value === 'true';
                            else element.value = field.value;
                            element.dispatchEvent(new Event('input', {bubbles:true}));
                            element.dispatchEvent(new Event('change', {bubbles:true}));
                        }
                        """, new { name = field.Key, value = field.Value }).ConfigureAwait(false);
                }
                await form.EvaluateAsync("form => form.requestSubmit()").ConfigureAwait(false);
            }
            else throw new ArgumentException("Unsupported browser action.");
            await Task.Delay(100, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.WaitSelector))
                await session.Page.WaitForSelectorAsync(request.WaitSelector, new() { Timeout = Configuration.TimeoutSeconds * 1000 }).ConfigureAwait(false);
            try { await session.Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = Math.Min(5000, Configuration.TimeoutSeconds * 1000) }).ConfigureAwait(false); }
            catch (TimeoutException) { /* End admission and settle the bounded requests below. */ }
            session.EndAdmission();
            while (Volatile.Read(ref session.Pending) != 0) await Task.Delay(10, token).ConfigureAwait(false);
            if (session.Error is { } error) throw new IOException(error);
            var html = await HtmlAsync(session.Page).ConfigureAwait(false);
            var result = WebBrowserService.DescribeRenderedPage(request.SessionId, session.Page.Url, html, session.StatusCode);
            _snapshots[request.SessionId] = new(request.SessionId, result.Url, result.Title, result.RetrievedAt, result.StatusCode, "text/html", result.Links.Count, result.Forms.Count);
            return result;
        }, actionDeadline.Token).ConfigureAwait(false);
    }

    public async Task<WebStructuredDataResult> ExtractStructuredDataAsync(WebStructuredDataRequest request, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(request.SessionId, out var session) || !_snapshots.ContainsKey(request.SessionId))
            throw new InvalidOperationException("Navigate this browser session before extracting its page.");
        return await WithSessionAsync(request.SessionId, session, async token =>
            await WebBrowserService.ExtractRenderedPageAsync(request, session.Page.Url, await HtmlAsync(session.Page).ConfigureAwait(false), token).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    private async Task<T> WithSessionAsync<T>(string id, Session session, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(Configuration.TimeoutSeconds));
        await session.Gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return await operation(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            session.EndAdmission();
            await deadline.CancelAsync().ConfigureAwait(false);
            _snapshots.TryRemove(id, out _);
            try
            {
                await session.Context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                _sessions.TryRemove(id, out _);
            }
            catch (Exception) { session.Faulted = true; }
            throw;
        }
        finally { session.EndAdmission(); session.Gate.Release(); }
    }

    private static async Task<string> HtmlAsync(IPage page)
    {
        var html = await page.EvaluateAsync<string>("() => { const html = document.documentElement.outerHTML; if (html.length > 1048576) throw new Error('Rendered page exceeds its limit'); return html; }").ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(html) > 2_097_152) throw new IOException("Rendered page exceeds 2 MiB.");
        return html;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _creation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_browser is not null)
                try { await _browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
            _playwright?.Dispose();
            _network.Dispose();
            _sessions.Clear(); _snapshots.Clear();
        }
        finally { _creation.Release(); _lifetime.Dispose(); }
    }

    internal sealed class Session(IBrowserContext context)
    {
        public IBrowserContext Context { get; } = context;
        public IPage Page { get; set; } = null!;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        private readonly object _networkGate = new();
        private bool _admitted;
        private CancellationToken _actionToken;
        private int _requests;
        public int Pending;
        public int Redirects;
        public volatile bool Faulted;
        public long Bytes;
        public int StatusCode = 200;
        public string? Error;
        public void Fail(string error) => Interlocked.CompareExchange(ref Error, error, null);
        public void BeginAction(CancellationToken token)
        { lock (_networkGate) { _actionToken = token; _requests = 0; Redirects = 0; Error = null; _admitted = true; } }
        public void EndAdmission() { lock (_networkGate) _admitted = false; }
        public bool TryAdmitRequest(string method, BrowserConfiguration options, out CancellationToken token)
        {
            lock (_networkGate)
            {
                token = _actionToken;
                if (!_admitted) return false;
                if (!options.AllowMutations && method is not ("GET" or "HEAD"))
                { Fail("The page attempted a request that changes data without a mutation grant."); return false; }
                if (++_requests > options.MaxRequestsPerAction)
                { Fail("The browser action exceeded its request limit."); return false; }
                Interlocked.Increment(ref Pending);
                return true;
            }
        }
    }
}
