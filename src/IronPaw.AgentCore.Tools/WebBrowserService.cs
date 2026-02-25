using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Fizzler.Systems.HtmlAgilityPack;
using HtmlAgilityPack;

namespace IronPaw.AgentCore.Tools;

public enum WebNavigationAction
{
    Open,
    FollowLink,
    Click,
    SubmitForm
}

public sealed record WebNavigationRequest(
    string SessionId,
    WebNavigationAction Action,
    string? Url = null,
    int? LinkIndex = null,
    string? LinkText = null,
    string? LinkSelector = null,
    int? FormIndex = null,
    string? FormSelector = null,
    IReadOnlyDictionary<string, string>? Fields = null);

public sealed record WebStructuredDataRequest(
    string SessionId,
    bool IncludeMetadata = true,
    bool IncludeTables = true,
    bool IncludeLists = true,
    int MaxItems = 10,
    string? RootSelector = null,
    string? TableSelector = null,
    string? ListSelector = null);

public sealed record WebLinkSummary(
    int Index,
    string Text,
    string Href,
    string ResolvedUrl);

public sealed record WebFormSummary(
    int Index,
    string Method,
    string Action,
    string ResolvedAction,
    IReadOnlyList<string> Fields);

public sealed record WebNavigationResult(
    string SessionId,
    string Url,
    int StatusCode,
    string? Title,
    string? Description,
    DateTimeOffset RetrievedAt,
    IReadOnlyList<WebLinkSummary> Links,
    IReadOnlyList<WebFormSummary> Forms);

public sealed record WebTableData(
    int Index,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows);

public sealed record WebListData(
    int Index,
    string Kind,
    IReadOnlyList<string> Items);

public sealed record WebStructuredDataResult(
    string SessionId,
    string Url,
    string? Title,
    DateTimeOffset RetrievedAt,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<WebTableData> Tables,
    IReadOnlyList<WebListData> Lists);

public sealed record WebSessionSnapshot(
    string SessionId,
    string Url,
    string? Title,
    DateTimeOffset RetrievedAt,
    int StatusCode,
    string? MediaType,
    int LinkCount,
    int FormCount);

public interface IWebBrowserService
{
    Task<WebNavigationResult> NavigateAsync(WebNavigationRequest request, CancellationToken ct);

    Task<WebStructuredDataResult> ExtractStructuredDataAsync(WebStructuredDataRequest request, CancellationToken ct);

    IReadOnlyList<WebSessionSnapshot> GetSessionSnapshots();

    WebSessionSnapshot? GetSessionSnapshot(string sessionId);
}

public sealed class WebBrowserService : IWebBrowserService
{
    private const int DefaultPreviewLimit = 25;
    private const int DefaultMaxAttempts = 3;
    private const string BrowserUserAgent = "IronPawWebBrowser/1.0";

    private static readonly Regex MultiWhitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

    private readonly HttpClient _httpClient;
    private readonly int _maxAttempts;
    private readonly int _previewLimit;
    private readonly TimeSpan _minHostInterval;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly ConcurrentDictionary<string, BrowserSessionState> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _nextRequestAtByHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _rateLimitLock = new();

    public WebBrowserService(
        HttpClient? httpClient = null,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? minHostInterval = null,
        int previewLimit = DefaultPreviewLimit,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "maxAttempts must be greater than zero.");
        }

        if (previewLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(previewLimit), "previewLimit must be greater than zero.");
        }

        _httpClient = httpClient ?? SharedHttpClient;
        _maxAttempts = maxAttempts;
        _previewLimit = previewLimit;
        _minHostInterval = minHostInterval ?? TimeSpan.FromMilliseconds(750);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public IReadOnlyList<WebSessionSnapshot> GetSessionSnapshots()
    {
        var snapshots = new List<WebSessionSnapshot>();
        foreach (var (sessionId, session) in _sessions)
        {
            if (TryBuildSessionSnapshot(sessionId, session, out var snapshot))
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots
            .OrderByDescending(snapshot => snapshot.RetrievedAt)
            .ToArray();
    }

    public WebSessionSnapshot? GetSessionSnapshot(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        if (!_sessions.TryGetValue(sessionId.Trim(), out var session))
        {
            return null;
        }

        return TryBuildSessionSnapshot(sessionId.Trim(), session, out var snapshot)
            ? snapshot
            : null;
    }

    public async Task<WebNavigationResult> NavigateAsync(WebNavigationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sessionId = EnsureNonEmpty(request.SessionId, nameof(request.SessionId));
        var session = _sessions.GetOrAdd(sessionId, _ => new BrowserSessionState());

        await session.Gate.WaitAsync(ct);
        try
        {
            return request.Action switch
            {
                WebNavigationAction.Open => await NavigateOpenAsync(sessionId, session, request.Url, ct),
                WebNavigationAction.FollowLink => await NavigateFollowLinkAsync(
                    sessionId,
                    session,
                    request.LinkIndex,
                    request.LinkText,
                    request.LinkSelector,
                    ct),
                WebNavigationAction.Click => await NavigateFollowLinkAsync(
                    sessionId,
                    session,
                    request.LinkIndex,
                    request.LinkText,
                    request.LinkSelector,
                    ct),
                WebNavigationAction.SubmitForm => await NavigateSubmitFormAsync(
                    sessionId,
                    session,
                    request.FormIndex,
                    request.FormSelector,
                    request.Fields,
                    ct),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Action), request.Action, "Unsupported action.")
            };
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<WebStructuredDataResult> ExtractStructuredDataAsync(WebStructuredDataRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sessionId = EnsureNonEmpty(request.SessionId, nameof(request.SessionId));
        var session = _sessions.GetOrAdd(sessionId, _ => new BrowserSessionState());

        await session.Gate.WaitAsync(ct);
        try
        {
            var page = session.CurrentPage
                ?? throw new InvalidOperationException(
                    $"No active page is loaded for session '{sessionId}'. Navigate first.");

            if (!request.IncludeMetadata && !request.IncludeTables && !request.IncludeLists)
            {
                throw new ArgumentException(
                    "At least one extraction target must be enabled (metadata, tables, or lists).",
                    nameof(request));
            }

            var maxItems = Math.Clamp(request.MaxItems, 1, 50);
            var document = LoadDocument(page.Html);
            var rootNode = ResolveExtractionRoot(document, request.RootSelector);
            var title = ExtractTitle(document);
            var metadata = request.IncludeMetadata
                ? ExtractMetadata(document, page.Uri, title)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tables = request.IncludeTables
                ? ExtractTables(rootNode, maxItems, request.TableSelector)
                : Array.Empty<WebTableData>();
            var lists = request.IncludeLists
                ? ExtractLists(rootNode, maxItems, request.ListSelector)
                : Array.Empty<WebListData>();

            return new WebStructuredDataResult(
                SessionId: sessionId,
                Url: page.Uri.ToString(),
                Title: title,
                RetrievedAt: page.RetrievedAt,
                Metadata: metadata,
                Tables: tables,
                Lists: lists);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<WebNavigationResult> NavigateOpenAsync(
        string sessionId,
        BrowserSessionState session,
        string? rawUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            throw new ArgumentException("url is required for action 'open'.", nameof(rawUrl));
        }

        var requestUri = ResolveUri(rawUrl.Trim(), session.CurrentPage?.Uri);
        var page = await FetchPageAsync(requestUri, HttpMethod.Get, fields: null, ct);
        session.CurrentPage = page;
        return BuildNavigationResult(sessionId, page);
    }

    private async Task<WebNavigationResult> NavigateFollowLinkAsync(
        string sessionId,
        BrowserSessionState session,
        int? linkIndex,
        string? linkText,
        string? linkSelector,
        CancellationToken ct)
    {
        var currentPage = session.CurrentPage
            ?? throw new InvalidOperationException(
                $"No active page is loaded for session '{sessionId}'. Navigate first.");

        if (linkIndex is null &&
            string.IsNullOrWhiteSpace(linkText) &&
            string.IsNullOrWhiteSpace(linkSelector))
        {
            throw new ArgumentException("Provide link_selector, link_index, or link_text to follow/click a link.");
        }

        var document = LoadDocument(currentPage.Html);
        var links = string.IsNullOrWhiteSpace(linkSelector)
            ? ExtractLinks(document.DocumentNode, currentPage.Uri, maxItems: 250, selector: null)
            : ExtractLinks(document.DocumentNode, currentPage.Uri, maxItems: 250, selector: linkSelector);
        if (links.Count == 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(linkSelector)
                    ? "The current page has no navigable links."
                    : $"No navigable links matched selector '{linkSelector}'.");
        }

        var selected = SelectLink(links, linkIndex, linkText);
        var requestUri = new Uri(selected.ResolvedUrl, UriKind.Absolute);
        var nextPage = await FetchPageAsync(requestUri, HttpMethod.Get, fields: null, ct);
        session.CurrentPage = nextPage;
        return BuildNavigationResult(sessionId, nextPage);
    }

    private async Task<WebNavigationResult> NavigateSubmitFormAsync(
        string sessionId,
        BrowserSessionState session,
        int? formIndex,
        string? formSelector,
        IReadOnlyDictionary<string, string>? fields,
        CancellationToken ct)
    {
        var currentPage = session.CurrentPage
            ?? throw new InvalidOperationException(
                $"No active page is loaded for session '{sessionId}'. Navigate first.");

        var document = LoadDocument(currentPage.Html);
        var forms = ExtractForms(document.DocumentNode, currentPage.Uri, maxItems: 100, selector: formSelector);
        if (forms.Count == 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(formSelector)
                    ? "The current page has no forms to submit."
                    : $"No forms matched selector '{formSelector}'.");
        }

        var resolvedIndex = formIndex ?? 0;
        if (resolvedIndex < 0 || resolvedIndex >= forms.Count)
        {
            throw new InvalidOperationException(
                $"Form index {resolvedIndex} is out of range. Available form indexes: 0-{forms.Count - 1}.");
        }

        var selected = forms[resolvedIndex];
        var mergedFields = new Dictionary<string, string>(selected.DefaultFields, StringComparer.OrdinalIgnoreCase);
        if (fields is not null)
        {
            foreach (var (name, value) in fields)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                mergedFields[name.Trim()] = value ?? string.Empty;
            }
        }

        BrowserPage nextPage;
        if (selected.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            nextPage = await FetchPageAsync(selected.ActionUri, HttpMethod.Post, mergedFields, ct);
        }
        else
        {
            var requestUri = BuildGetUri(selected.ActionUri, mergedFields);
            nextPage = await FetchPageAsync(requestUri, HttpMethod.Get, fields: null, ct);
        }

        session.CurrentPage = nextPage;
        return BuildNavigationResult(sessionId, nextPage);
    }

    private async Task<BrowserPage> FetchPageAsync(
        Uri requestUri,
        HttpMethod method,
        IReadOnlyDictionary<string, string>? fields,
        CancellationToken ct)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await ApplyRateLimitAsync(requestUri, ct);

            using var request = new HttpRequestMessage(method, requestUri);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);

            if (method == HttpMethod.Post)
            {
                request.Content = new FormUrlEncodedContent(fields ?? new Dictionary<string, string>());
            }

            try
            {
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);

                if (ShouldRetry(response.StatusCode) && attempt < _maxAttempts)
                {
                    await DelayBeforeRetryAsync(attempt, response.Headers.RetryAfter, ct);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                var finalUri = response.RequestMessage?.RequestUri ?? requestUri;

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"Request to '{finalUri}' failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
                }

                return new BrowserPage(
                    Uri: finalUri,
                    StatusCode: (int)response.StatusCode,
                    Html: body,
                    RetrievedAt: _timeProvider.GetUtcNow(),
                    MediaType: response.Content.Headers.ContentType?.MediaType);
            }
            catch (HttpRequestException ex) when (attempt < _maxAttempts)
            {
                lastException = ex;
                await DelayBeforeRetryAsync(attempt, retryAfter: null, ct);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < _maxAttempts)
            {
                lastException = ex;
                await DelayBeforeRetryAsync(attempt, retryAfter: null, ct);
            }
        }

        throw new InvalidOperationException(
            $"Request to '{requestUri}' failed after {_maxAttempts} attempts.",
            lastException);
    }

    private async Task ApplyRateLimitAsync(Uri requestUri, CancellationToken ct)
    {
        if (_minHostInterval <= TimeSpan.Zero)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        TimeSpan delay = TimeSpan.Zero;
        lock (_rateLimitLock)
        {
            if (_nextRequestAtByHost.TryGetValue(requestUri.Host, out var nextAllowed) && nextAllowed > now)
            {
                delay = nextAllowed - now;
                now = nextAllowed;
            }

            _nextRequestAtByHost[requestUri.Host] = now + _minHostInterval;
        }

        if (delay > TimeSpan.Zero)
        {
            await _delayAsync(delay, ct);
        }
    }

    private async Task DelayBeforeRetryAsync(
        int attempt,
        RetryConditionHeaderValue? retryAfter,
        CancellationToken ct)
    {
        var delay = GetRetryDelay(attempt, retryAfter);
        if (delay > TimeSpan.Zero)
        {
            await _delayAsync(delay, ct);
        }
    }

    private TimeSpan GetRetryDelay(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
        {
            return CapDelay(delta);
        }

        if (retryAfter?.Date is DateTimeOffset retryDate)
        {
            var untilRetry = retryDate - _timeProvider.GetUtcNow();
            if (untilRetry > TimeSpan.Zero)
            {
                return CapDelay(untilRetry);
            }
        }

        var delayMs = Math.Min(5000d, 250d * Math.Pow(2d, Math.Max(0, attempt - 1)));
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private static TimeSpan CapDelay(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var max = TimeSpan.FromSeconds(30);
        return value > max ? max : value;
    }

    private WebNavigationResult BuildNavigationResult(string sessionId, BrowserPage page)
    {
        var document = LoadDocument(page.Html);
        var title = ExtractTitle(document);
        var metadata = ExtractMetadata(document, page.Uri, title);
        metadata.TryGetValue("description", out var description);
        var links = ExtractLinks(document.DocumentNode, page.Uri, _previewLimit, selector: null);
        var forms = ExtractForms(document.DocumentNode, page.Uri, _previewLimit, selector: null)
            .Select(form => new WebFormSummary(
                Index: form.Index,
                Method: form.Method,
                Action: form.Action,
                ResolvedAction: form.ActionUri.ToString(),
                Fields: form.DefaultFields.Keys.ToArray()))
            .ToArray();

        return new WebNavigationResult(
            SessionId: sessionId,
            Url: page.Uri.ToString(),
            StatusCode: page.StatusCode,
            Title: title,
            Description: description,
            RetrievedAt: page.RetrievedAt,
            Links: links,
            Forms: forms);
    }

    private static HtmlDocument LoadDocument(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html ?? string.Empty);
        return document;
    }

    private static string? ExtractTitle(HtmlDocument document)
    {
        return CleanNodeText(document.DocumentNode.SelectSingleNode("//title"));
    }

    private static Dictionary<string, string> ExtractMetadata(HtmlDocument document, Uri pageUri, string? title)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(title))
        {
            metadata["title"] = title;
        }

        var htmlNode = document.DocumentNode.SelectSingleNode("//html");
        if (htmlNode is not null)
        {
            var lang = htmlNode.GetAttributeValue("lang", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(lang))
            {
                metadata["language"] = lang;
            }
        }

        var metaNodes = document.DocumentNode.SelectNodes("//meta");
        if (metaNodes is not null)
        {
            foreach (var node in metaNodes)
            {
                var content = node.GetAttributeValue("content", string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var name = node.GetAttributeValue("name", string.Empty).Trim();
                var property = node.GetAttributeValue("property", string.Empty).Trim();
                var key = !string.IsNullOrWhiteSpace(name) ? name : property;
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var normalizedKey = key.ToLowerInvariant();
                switch (normalizedKey)
                {
                    case "description":
                    case "og:title":
                    case "og:description":
                    case "og:url":
                    case "author":
                        metadata[normalizedKey.Replace(':', '_')] = content;
                        break;
                }
            }
        }

        var canonicalHref = document.DocumentNode
            .SelectSingleNode("//link[translate(@rel,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='canonical']")
            ?.GetAttributeValue("href", string.Empty)
            ?.Trim();
        if (!string.IsNullOrWhiteSpace(canonicalHref) &&
            TryResolveUri(pageUri, canonicalHref, out var canonicalUri))
        {
            metadata["canonical_url"] = canonicalUri.ToString();
        }

        return metadata;
    }

    private static IReadOnlyList<WebTableData> ExtractTables(HtmlNode rootNode, int maxItems, string? selector)
    {
        var tableNodes = ResolveTableNodes(rootNode, selector);
        if (tableNodes.Count == 0)
        {
            return Array.Empty<WebTableData>();
        }

        var maxRowsPerTable = Math.Clamp(maxItems * 4, 5, 100);
        var tables = new List<WebTableData>();
        foreach (var tableNode in tableNodes)
        {
            if (tables.Count >= maxItems)
            {
                break;
            }

            var headers = new List<string>();
            var headerNodes = tableNode.SelectNodes("./thead/tr[1]/th");
            if (headerNodes is not null)
            {
                headers.AddRange(headerNodes
                    .Select(CleanNodeText)
                    .Where(static text => !string.IsNullOrWhiteSpace(text))
                    .Select(static text => text!));
            }

            var rows = new List<IReadOnlyList<string>>();
            var rowNodes = tableNode.SelectNodes(".//tr");
            if (rowNodes is null)
            {
                continue;
            }

            foreach (var rowNode in rowNodes)
            {
                if (rows.Count >= maxRowsPerTable)
                {
                    break;
                }

                var cellNodes = rowNode.SelectNodes("./th|./td");
                if (cellNodes is null || cellNodes.Count == 0)
                {
                    continue;
                }

                var values = cellNodes
                    .Select(CleanNodeText)
                    .Select(value => value ?? string.Empty)
                    .ToArray();
                if (values.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                if (headers.Count == 0 &&
                    rowNode.SelectNodes("./th") is { Count: > 0 })
                {
                    headers.AddRange(values);
                    continue;
                }

                rows.Add(values);
            }

            if (headers.Count == 0 && rows.Count == 0)
            {
                continue;
            }

            tables.Add(new WebTableData(
                Index: tables.Count,
                Headers: headers,
                Rows: rows));
        }

        return tables;
    }

    private static IReadOnlyList<WebListData> ExtractLists(HtmlNode rootNode, int maxItems, string? selector)
    {
        var listNodes = ResolveListNodes(rootNode, selector);
        if (listNodes.Count == 0)
        {
            return Array.Empty<WebListData>();
        }

        var maxItemsPerList = Math.Clamp(maxItems * 5, 5, 200);
        var lists = new List<WebListData>();
        foreach (var listNode in listNodes)
        {
            if (lists.Count >= maxItems)
            {
                break;
            }

            var itemNodes = listNode.SelectNodes("./li");
            if (itemNodes is null || itemNodes.Count == 0)
            {
                continue;
            }

            var items = itemNodes
                .Select(CleanNodeText)
                .Where(static text => !string.IsNullOrWhiteSpace(text))
                .Select(static text => text!)
                .Take(maxItemsPerList)
                .ToArray();
            if (items.Length == 0)
            {
                continue;
            }

            lists.Add(new WebListData(
                Index: lists.Count,
                Kind: listNode.Name.ToLowerInvariant(),
                Items: items));
        }

        return lists;
    }

    private static IReadOnlyList<WebLinkSummary> ExtractLinks(
        HtmlNode rootNode,
        Uri pageUri,
        int maxItems,
        string? selector)
    {
        var linkNodes = ResolveLinkNodes(rootNode, selector);
        if (linkNodes.Count == 0)
        {
            return Array.Empty<WebLinkSummary>();
        }

        var links = new List<WebLinkSummary>();
        foreach (var linkNode in linkNodes)
        {
            if (links.Count >= maxItems)
            {
                break;
            }

            var href = linkNode.GetAttributeValue("href", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#'))
            {
                continue;
            }

            if (!TryResolveUri(pageUri, href, out var resolved))
            {
                continue;
            }

            var text = CleanNodeText(linkNode);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = linkNode.GetAttributeValue("title", string.Empty).Trim();
            }

            links.Add(new WebLinkSummary(
                Index: links.Count,
                Text: text ?? string.Empty,
                Href: href,
                ResolvedUrl: resolved.ToString()));
        }

        return links;
    }

    private static IReadOnlyList<WebFormInfo> ExtractForms(
        HtmlNode rootNode,
        Uri pageUri,
        int maxItems,
        string? selector)
    {
        var formNodes = ResolveFormNodes(rootNode, selector);
        if (formNodes.Count == 0)
        {
            return Array.Empty<WebFormInfo>();
        }

        var forms = new List<WebFormInfo>();
        foreach (var formNode in formNodes)
        {
            if (forms.Count >= maxItems)
            {
                break;
            }

            var rawMethod = formNode.GetAttributeValue("method", "GET").Trim();
            var method = rawMethod.Equals("post", StringComparison.OrdinalIgnoreCase) ? "POST" : "GET";
            var rawAction = formNode.GetAttributeValue("action", string.Empty).Trim();
            var actionUri = string.IsNullOrWhiteSpace(rawAction)
                ? pageUri
                : ResolveUri(rawAction, pageUri);

            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fieldNodes = formNode.SelectNodes(".//*[@name]");
            if (fieldNodes is not null)
            {
                foreach (var fieldNode in fieldNodes)
                {
                    var fieldName = fieldNode.GetAttributeValue("name", string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(fieldName))
                    {
                        continue;
                    }

                    var value = ResolveFieldValue(fieldNode);
                    if (value is null)
                    {
                        continue;
                    }

                    defaults[fieldName] = value;
                }
            }

            forms.Add(new WebFormInfo(
                Index: forms.Count,
                Method: method,
                Action: rawAction,
                ActionUri: actionUri,
                DefaultFields: defaults));
        }

        return forms;
    }

    private static WebLinkSummary SelectLink(
        IReadOnlyList<WebLinkSummary> links,
        int? linkIndex,
        string? linkText)
    {
        if (linkIndex is int requestedIndex)
        {
            if (requestedIndex < 0 || requestedIndex >= links.Count)
            {
                throw new InvalidOperationException(
                    $"Link index {requestedIndex} is out of range. Available link indexes: 0-{links.Count - 1}.");
            }

            return links[requestedIndex];
        }

        var requestedText = linkText?.Trim();
        if (string.IsNullOrWhiteSpace(requestedText))
        {
            return links[0];
        }

        var match = links.FirstOrDefault(link =>
            (!string.IsNullOrWhiteSpace(link.Text) &&
             link.Text.Contains(requestedText, StringComparison.OrdinalIgnoreCase)) ||
            link.Href.Contains(requestedText, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new InvalidOperationException(
            $"No link matched '{requestedText}'. Use link_index for deterministic navigation.");
    }

    private static HtmlNode ResolveExtractionRoot(HtmlDocument document, string? rootSelector)
    {
        if (string.IsNullOrWhiteSpace(rootSelector))
        {
            return document.DocumentNode;
        }

        var selector = rootSelector.Trim();
        try
        {
            var node = document.DocumentNode.QuerySelector(selector);
            return node ?? throw new InvalidOperationException(
                $"No node matched root_selector '{selector}'.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new ArgumentException(
                $"Invalid CSS selector for root_selector: {ex.Message}",
                nameof(rootSelector));
        }
    }

    private static IReadOnlyList<HtmlNode> ResolveTableNodes(HtmlNode rootNode, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return rootNode.SelectNodes(".//table")?.ToArray() ?? Array.Empty<HtmlNode>();
        }

        var selected = SelectNodesByCss(rootNode, selector, "table_selector");
        var tables = new List<HtmlNode>();
        foreach (var node in selected)
        {
            if (node.Name.Equals("table", StringComparison.OrdinalIgnoreCase))
            {
                AddDistinctNode(tables, node);
                continue;
            }

            var nestedTables = node.SelectNodes(".//table");
            if (nestedTables is null)
            {
                continue;
            }

            foreach (var nested in nestedTables)
            {
                AddDistinctNode(tables, nested);
            }
        }

        return tables;
    }

    private static IReadOnlyList<HtmlNode> ResolveListNodes(HtmlNode rootNode, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return rootNode.SelectNodes(".//ul|.//ol")?.ToArray() ?? Array.Empty<HtmlNode>();
        }

        var selected = SelectNodesByCss(rootNode, selector, "list_selector");
        var lists = new List<HtmlNode>();
        foreach (var node in selected)
        {
            if (node.Name.Equals("ul", StringComparison.OrdinalIgnoreCase) ||
                node.Name.Equals("ol", StringComparison.OrdinalIgnoreCase))
            {
                AddDistinctNode(lists, node);
                continue;
            }

            var nestedLists = node.SelectNodes(".//ul|.//ol");
            if (nestedLists is null)
            {
                continue;
            }

            foreach (var nested in nestedLists)
            {
                AddDistinctNode(lists, nested);
            }
        }

        return lists;
    }

    private static IReadOnlyList<HtmlNode> ResolveLinkNodes(HtmlNode rootNode, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return rootNode.SelectNodes(".//a[@href]")?.ToArray() ?? Array.Empty<HtmlNode>();
        }

        var selected = SelectNodesByCss(rootNode, selector, "link_selector");
        var links = new List<HtmlNode>();
        foreach (var node in selected)
        {
            if (node.Name.Equals("a", StringComparison.OrdinalIgnoreCase) &&
                node.Attributes["href"] is not null)
            {
                AddDistinctNode(links, node);
                continue;
            }

            var descendant = node.SelectSingleNode(".//a[@href]");
            if (descendant is not null)
            {
                AddDistinctNode(links, descendant);
            }
        }

        return links;
    }

    private static IReadOnlyList<HtmlNode> ResolveFormNodes(HtmlNode rootNode, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return rootNode.SelectNodes(".//form")?.ToArray() ?? Array.Empty<HtmlNode>();
        }

        var selected = SelectNodesByCss(rootNode, selector, "form_selector");
        var forms = new List<HtmlNode>();
        foreach (var node in selected)
        {
            if (node.Name.Equals("form", StringComparison.OrdinalIgnoreCase))
            {
                AddDistinctNode(forms, node);
                continue;
            }

            var ancestor = node.Ancestors("form").FirstOrDefault();
            if (ancestor is not null)
            {
                AddDistinctNode(forms, ancestor);
                continue;
            }

            var descendant = node.SelectSingleNode(".//form");
            if (descendant is not null)
            {
                AddDistinctNode(forms, descendant);
            }
        }

        return forms;
    }

    private static IReadOnlyList<HtmlNode> SelectNodesByCss(
        HtmlNode rootNode,
        string selector,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return Array.Empty<HtmlNode>();
        }

        try
        {
            return rootNode.QuerySelectorAll(selector.Trim()).ToArray();
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"Invalid CSS selector for {parameterName}: {ex.Message}",
                parameterName);
        }
    }

    private static void AddDistinctNode(List<HtmlNode> collection, HtmlNode node)
    {
        if (collection.Contains(node))
        {
            return;
        }

        collection.Add(node);
    }

    private static Uri BuildGetUri(Uri actionUri, IReadOnlyDictionary<string, string> fields)
    {
        if (fields.Count == 0)
        {
            return actionUri;
        }

        var encodedPairs = fields
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
            .Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value ?? string.Empty)}")
            .ToArray();
        if (encodedPairs.Length == 0)
        {
            return actionUri;
        }

        var builder = new UriBuilder(actionUri);
        var existingQuery = builder.Query.TrimStart('?');
        var newQuery = string.Join("&", encodedPairs);
        builder.Query = string.IsNullOrWhiteSpace(existingQuery)
            ? newQuery
            : $"{existingQuery}&{newQuery}";
        return builder.Uri;
    }

    private static Uri ResolveUri(string raw, Uri? baseUri)
    {
        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute) && IsSupportedScheme(absolute))
        {
            return absolute;
        }

        if (baseUri is not null &&
            Uri.TryCreate(baseUri, raw, out var relative) &&
            IsSupportedScheme(relative))
        {
            return relative;
        }

        throw new ArgumentException($"Unable to resolve URL '{raw}'. Use an absolute http/https URL.");
    }

    private static bool TryResolveUri(Uri baseUri, string raw, out Uri resolved)
    {
        resolved = null!;
        if (!Uri.TryCreate(baseUri, raw, out var candidate))
        {
            return false;
        }

        if (!IsSupportedScheme(candidate))
        {
            return false;
        }

        resolved = candidate;
        return true;
    }

    private static bool IsSupportedScheme(Uri uri)
    {
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryBuildSessionSnapshot(
        string sessionId,
        BrowserSessionState session,
        out WebSessionSnapshot snapshot)
    {
        snapshot = default!;
        var page = session.CurrentPage;
        if (page is null)
        {
            return false;
        }

        var document = LoadDocument(page.Html);
        var title = ExtractTitle(document);
        var links = ExtractLinks(document.DocumentNode, page.Uri, _previewLimit, selector: null);
        var forms = ExtractForms(document.DocumentNode, page.Uri, _previewLimit, selector: null);
        snapshot = new WebSessionSnapshot(
            SessionId: sessionId,
            Url: page.Uri.ToString(),
            Title: title,
            RetrievedAt: page.RetrievedAt,
            StatusCode: page.StatusCode,
            MediaType: page.MediaType,
            LinkCount: links.Count,
            FormCount: forms.Count);
        return true;
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        var numeric = (int)statusCode;
        return statusCode == (HttpStatusCode)429 || (numeric >= 500 && numeric <= 599);
    }

    private static string EnsureNonEmpty(string? value, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{argumentName} cannot be empty.", argumentName);
        }

        return value.Trim();
    }

    private static string? CleanNodeText(HtmlNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return NormalizeText(HtmlEntity.DeEntitize(node.InnerText));
    }

    private static string NormalizeText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return MultiWhitespace.Replace(raw, " ").Trim();
    }

    private static string? ResolveFieldValue(HtmlNode fieldNode)
    {
        var tagName = fieldNode.Name.ToLowerInvariant();
        if (tagName == "input")
        {
            var type = fieldNode.GetAttributeValue("type", "text").Trim().ToLowerInvariant();
            return type switch
            {
                "checkbox" or "radio" => fieldNode.Attributes["checked"] is null
                    ? null
                    : fieldNode.GetAttributeValue("value", "on"),
                "submit" or "button" or "image" or "file" => null,
                _ => fieldNode.GetAttributeValue("value", string.Empty)
            };
        }

        if (tagName == "textarea")
        {
            return NormalizeText(fieldNode.InnerText);
        }

        if (tagName == "select")
        {
            var selectedOption = fieldNode.SelectSingleNode("./option[@selected]") ??
                                 fieldNode.SelectSingleNode("./option");
            if (selectedOption is null)
            {
                return string.Empty;
            }

            var selectedValue = selectedOption.GetAttributeValue("value", string.Empty);
            return string.IsNullOrWhiteSpace(selectedValue)
                ? NormalizeText(selectedOption.InnerText)
                : selectedValue;
        }

        return fieldNode.GetAttributeValue("value", string.Empty);
    }

    private static HttpClient CreateSharedHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private sealed class BrowserSessionState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public BrowserPage? CurrentPage { get; set; }
    }

    private sealed record BrowserPage(
        Uri Uri,
        int StatusCode,
        string Html,
        DateTimeOffset RetrievedAt,
        string? MediaType);

    private sealed record WebFormInfo(
        int Index,
        string Method,
        string Action,
        Uri ActionUri,
        IReadOnlyDictionary<string, string> DefaultFields);
}
