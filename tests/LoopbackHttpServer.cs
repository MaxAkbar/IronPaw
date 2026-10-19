using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IronPaw.Testing;

internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    internal sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);
    internal sealed record Response(string Body, int Status = 200, string ContentType = "text/html", IReadOnlyDictionary<string, string>? Headers = null);
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentBag<Task> _clients = [];
    private readonly Task _accept;
    private readonly Func<Request, Task<Response>> _handler;
    public ConcurrentQueue<Request> Requests { get; } = [];
    public CancellationToken Token => _lifetime.Token;
    public string Origin { get; }
    public LoopbackHttpServer(Func<Request, Task<Response>> handler)
    {
        _handler = handler;
        _listener.Start();
        Origin = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptAsync);
    }
    private async Task AcceptAsync()
    {
        try { while (true) _clients.Add(ServeAsync(await _listener.AcceptTcpClientAsync(Token))); }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(Token);
                if (line is null) return;
                var parts = line.Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync(Token) is { Length: > 0 } header)
                {
                    var split = header.IndexOf(':');
                    headers[header[..split]] = header[(split + 1)..].Trim();
                }
                async Task<string> ReadBodyAsync(int length)
                {
                    if (length > 1_048_576) throw new IOException("Test fixture body is too large.");
                    var text = new char[length];
                    var offset = 0;
                    while (offset < text.Length)
                    {
                        var count = await reader.ReadAsync(text.AsMemory(offset), Token);
                        if (count == 0) throw new EndOfStreamException();
                        offset += count;
                    }
                    return new string(text);
                }
                string body;
                if (headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    var chunks = new StringBuilder();
                    while (true)
                    {
                        var chunkHeader = await reader.ReadLineAsync(Token) ?? throw new EndOfStreamException();
                        var length = int.Parse(chunkHeader.Split(';')[0], System.Globalization.NumberStyles.HexNumber);
                        if (length == 0) { while (await reader.ReadLineAsync(Token) is { Length: > 0 }) { } break; }
                        chunks.Append(await ReadBodyAsync(length));
                        if (chunks.Length > 1_048_576) throw new IOException("Test fixture body is too large.");
                        await reader.ReadLineAsync(Token);
                    }
                    body = chunks.ToString();
                }
                else body = await ReadBodyAsync(headers.TryGetValue("Content-Length", out var size) ? int.Parse(size) : 0);
                var request = new Request(parts[0], parts[1], headers, body);
                Requests.Enqueue(request);
                var response = await _handler(request);
                var bytes = Encoding.UTF8.GetBytes(response.Body);
                var text = $"HTTP/1.1 {response.Status} Fixture\r\nConnection: close\r\nContent-Type: {response.ContentType}\r\nContent-Length: {bytes.Length}\r\n";
                foreach (var pair in response.Headers ?? new Dictionary<string, string>()) text += pair.Key + ": " + pair.Value + "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(text + "\r\n"), Token);
                await stream.WriteAsync(bytes, Token);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_clients).WaitAsync(TimeSpan.FromSeconds(5));
        _lifetime.Dispose();
    }
}
