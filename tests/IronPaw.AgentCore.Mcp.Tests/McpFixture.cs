using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace IronPaw.AgentCore.Mcp.Tests;

// A real SDK stream connection to a deliberately small, independently implemented JSON-RPC peer.
// No external connector, provider, process, credentials or network service is involved.
internal sealed class McpFixture : IAsyncDisposable
{
    private readonly Pipe _requests = new();
    private readonly Pipe _responses = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _pump;
    public int Connections;
    public int Calls;
    public int Lists;
    public string? ProtocolError;
    public JsonElement? Initialize;
    public ConcurrentQueue<JsonElement> Arguments { get; } = new();
    public ConcurrentQueue<string> Methods { get; } = new();
    public Func<JsonElement, object> Catalog { get; set; }
    public Func<JsonElement, CancellationToken, Task<object?>> Execute { get; set; } = static (_, _) =>
        Task.FromResult<object?>(new { content = new[] { new { type = "text", text = "connector result" } } });
    public TaskCompletionSource Dispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public McpFixture(JsonElement schema)
    {
        Catalog = _ => new
        {
            tools = new[] { new { name = "record", description = "SERVER TEXT MUST NOT BECOME THE REVIEWED DESCRIPTION",
            inputSchema = schema, annotations = new { readOnlyHint = true, idempotentHint = true } } }
        };
        _pump = Task.Run(PumpAsync);
    }

    public IClientTransport Transport()
    {
        if (Interlocked.Increment(ref Connections) != 1) throw new InvalidOperationException("Unexpected reconnect.");
        return new StreamClientTransport(_requests.Writer.AsStream(), _responses.Reader.AsStream());
    }

    private async Task PumpAsync()
    {
        using var reader = new StreamReader(_requests.Reader.AsStream(), new UTF8Encoding(false));
        await using var writer = new StreamWriter(_responses.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        try
        {
            while (await reader.ReadLineAsync(_lifetime.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                var method = message.GetProperty("method").GetString()!;
                Methods.Enqueue(method);
                if (!message.TryGetProperty("id", out var id)) continue;
                object? result;
                switch (method)
                {
                    case "initialize":
                        Initialize = message.GetProperty("params").Clone();
                        result = new
                        {
                            protocolVersion = "2025-11-25",
                            capabilities = new { tools = new { } },
                            serverInfo = new { name = "fixture", version = "1.0" },
                            instructions = "UNTRUSTED SERVER INSTRUCTIONS"
                        };
                        break;
                    case "tools/list":
                        Interlocked.Increment(ref Lists);
                        result = Catalog(message.GetProperty("params"));
                        break;
                    case "tools/call":
                        Interlocked.Increment(ref Calls);
                        Assert.AreEqual("record", message.GetProperty("params").GetProperty("name").GetString());
                        Arguments.Enqueue(message.GetProperty("params").GetProperty("arguments").Clone());
                        Dispatched.TrySetResult();
                        result = await Execute(message.GetProperty("params"), _lifetime.Token);
                        if (result is null) return; // Deliberately lose the response after recording the effect.
                        break;
                    case "ping": result = new { }; break;
                    default: throw new InvalidOperationException($"Unexpected SDK request: {method}");
                }
                if (method == "tools/call" && ProtocolError is not null)
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32000, message = ProtocolError } }));
                else await writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result }));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) when (_lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await _pump.WaitAsync(TimeSpan.FromSeconds(5));
        _lifetime.Dispose();
    }
}
