using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Playwright;

namespace IronPaw.AgentCore.Browser;

internal sealed class BrowserNetwork : IDisposable
{
    private readonly BrowserConfiguration _options;
    private readonly HttpClient _client;
    public BrowserNetwork(BrowserConfiguration options)
    {
        _options = options;
        _client = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            MaxResponseHeadersLength = 32,
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 8,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
                var address = addresses.FirstOrDefault(ip => options.AllowPrivateNetwork || IsPublic(ip))
                    ?? throw new IOException("The browser origin resolves outside its network grant.");
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            }
        })
        { Timeout = Timeout.InfiniteTimeSpan };
    }
    public bool Allowed(string value) => value.Length <= 8192 && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 &&
        uri.Scheme is "http" or "https" && _options.AllowedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal);

    public async Task SendAsync(IRoute route, ChromiumBrowserService.Session session)
    {
        var request = route.Request;
        if (!Allowed(request.Url) || request.ResourceType is "image" or "media" or "font")
        { await AbortAsync(route).ConfigureAwait(false); return; }
        if (!session.TryAdmitRequest(request.Method, _options, out var ct))
        { await AbortAsync(route).ConfigureAwait(false); return; }
        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
            if (request.PostDataBuffer is { } body)
            {
                if (body.Length > 65_536) throw new IOException("Browser request body exceeds 64 KiB.");
                message.Content = new ByteArrayContent(body);
            }
            foreach (var header in await request.AllHeadersAsync().ConfigureAwait(false))
            {
                if (header.Key.Equals("host", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("connection", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.StartsWith("proxy-", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase)) continue;
                if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value)) message.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                // Chromium may bypass route handlers for an HTTP redirect chain. Never hand it a 3xx.
                // A reviewed GET navigation can instead start a fresh, intercepted document navigation.
                var target = response.Headers.Location is { } location ? new Uri(new Uri(request.Url), location) : null;
                if (target is null || !Allowed(target.AbsoluteUri) || !request.IsNavigationRequest ||
                    request.Method != "GET" || Interlocked.Increment(ref session.Redirects) > 5)
                    throw new IOException("Browser redirect is outside its grant or cannot be followed without repeating an effect.");
                var redirectHeaders = new Dictionary<string, string>();
                if (response.Headers.TryGetValues("Set-Cookie", out var cookies)) redirectHeaders["set-cookie"] = string.Join("\n", cookies);
                await route.FulfillAsync(new()
                {
                    Status = 200,
                    ContentType = "text/html",
                    Headers = redirectHeaders,
                    Body = "<!doctype html><script>location.replace(" + JsonSerializer.Serialize(target.AbsoluteUri) + ")</script>"
                }).ConfigureAwait(false);
                return;
            }
            if (response.Content.Headers.ContentLength > _options.MaxResponseBytes) throw new IOException("Browser response exceeds its size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (output.Length + read > _options.MaxResponseBytes || Interlocked.Add(ref session.Bytes, read) > _options.MaxSessionBytes)
                    throw new IOException("Browser response or session exceeds its byte limit.");
                output.Write(buffer, 0, read);
            }
            var headers = response.Headers.Concat(response.Content.Headers)
                .Where(pair => !new[] { "content-length", "transfer-encoding", "connection", "content-encoding" }.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => string.Join(pair.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase) ? "\n" : ", ", pair.Value), StringComparer.OrdinalIgnoreCase);
            // Request identity and networking remain in this process; Chromium never resolves the target itself.
            await route.FulfillAsync(new() { Status = (int)response.StatusCode, BodyBytes = output.ToArray(), Headers = headers }).ConfigureAwait(false);
            if (request.IsNavigationRequest && request.Frame.ParentFrame is null) session.StatusCode = (int)response.StatusCode;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or PlaywrightException)
        {
            session.Fail("The browser request did not establish a complete response within its reviewed limits.");
            await AbortAsync(route).ConfigureAwait(false);
        }
        finally { Interlocked.Decrement(ref session.Pending); }
    }
    private static async Task AbortAsync(IRoute route)
    { try { await route.AbortAsync().ConfigureAwait(false); } catch (PlaywrightException) { } }
    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 ? (bytes[0] & 0xe0) == 0x20 : bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 &&
            !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
            !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }
    public void Dispose() => _client.Dispose();
}
