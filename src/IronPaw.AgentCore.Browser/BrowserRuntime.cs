using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;
using System.Collections.Concurrent;

namespace IronPaw.AgentCore.Browser;

/// <summary>HTML browsing remains available unless the host explicitly enables reviewed JavaScript sites.</summary>
public sealed class BrowserRuntime : IBoundWebBrowserService, IWebBrowserRunScope, IDisposable
{
    private readonly IWebBrowserService _html = new WebBrowserService();
    private ChromiumBrowserService? _javascript;
    private readonly ConcurrentDictionary<RunId, ConcurrentDictionary<string, byte>> _scopes = new();
    private int _disposed;
    public string? CleanupWarning { get; private set; }
    public string? BrowserBinding => _javascript?.Configuration.Binding;
    public bool RequiresMutationApproval => _javascript?.Configuration.AllowMutations == true;
    public void Enable(BrowserConfiguration configuration)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_javascript is not null) throw new InvalidOperationException("Restart the host to change its reviewed browser configuration.");
        _javascript = new(configuration);
    }
    private IWebBrowserService Backend
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (CleanupWarning is not null) throw new InvalidOperationException(CleanupWarning);
            return (IWebBrowserService?)_javascript ?? _html;
        }
    }
    public ToolSelectionPolicy Grant(ToolSelectionPolicy policy) => policy with { BrowserBinding = BrowserBinding };
    public IReadOnlyCollection<string> RequireApprovals(IEnumerable<string>? tools) =>
        (tools ?? []).Concat(RequiresMutationApproval ? ["web_navigate"] : []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public void GrantSpecialist(IAgentRegistry agents)
    {
        if (BrowserBinding is not null && agents.Get("web_browser") is { } agent)
            agents.Register(agent with { ToolPolicy = Grant(agent.ToolPolicy) });
    }
    public Task<WebNavigationResult> NavigateAsync(WebNavigationRequest request, CancellationToken ct) => Backend.NavigateAsync(request, ct);
    public Task<WebStructuredDataResult> ExtractStructuredDataAsync(WebStructuredDataRequest request, CancellationToken ct) => Backend.ExtractStructuredDataAsync(request, ct);
    public IReadOnlyList<WebSessionSnapshot> GetSessionSnapshots() => Backend.GetSessionSnapshots();
    public WebSessionSnapshot? GetSessionSnapshot(string sessionId) => Backend.GetSessionSnapshot(sessionId);
    public void BindSession(string sessionId, RunId rootRunId) => _scopes.GetOrAdd(rootRunId, _ => new())[sessionId] = 0;
    public async Task ReleaseRunAsync(RunId rootRunId)
    {
        if (!_scopes.TryRemove(rootRunId, out var sessions)) return;
        foreach (var id in sessions.Keys)
        {
            if (_javascript is not null)
            {
                try { await _javascript.CloseSessionAsync(id).ConfigureAwait(false); }
                catch (Exception) { CleanupWarning = "A browser context could not be closed. Restart the host before browsing again."; }
            }
            if (_html is WebBrowserService html) html.ForgetSession(id);
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_javascript is not null) Task.Run(async () => await _javascript.DisposeAsync()).GetAwaiter().GetResult();
        _scopes.Clear();
    }
}
