using System.Text.Json;

namespace IronPaw.Workflow.CrashHarness;

// Independent JSON-RPC peer used to exercise the shipped host's real subprocess boundary.
public static class McpProcessFixture
{
    public static async Task RunHostAsync(string configuration, string ready)
    {
        using var host = IronPaw.AgentCore.Mcp.McpHost.Load(configuration);
        var policy = host.Grant(new(IronPaw.AgentCore.Abstractions.ToolProfile.Coding));
        await host.Tools[0].ExecuteAsync(new("call", host.ToolNames[0], JsonSerializer.SerializeToElement(new { value = 2 })),
            new(IronPaw.AgentCore.Abstractions.RunId.New(), new("parent-fixture"), "local", "fixture",
                IronPaw.AgentCore.Abstractions.CallerIdentity.LocalOwner(), ToolPolicy: policy), default);
        await File.WriteAllTextAsync(ready, "ready");
        await Task.Delay(Timeout.Infinite);
    }
    public static async Task RunAsync(string[] args)
    {
        var root = args[0];
        await File.WriteAllTextAsync(Path.Combine(root, "process-id"), Environment.ProcessId.ToString());
        var schema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"value":{"type":"integer"}},"required":["value"],"additionalProperties":false}""");
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var json = JsonDocument.Parse(line);
            var request = json.RootElement;
            if (!request.TryGetProperty("id", out var id)) continue;
            var method = request.GetProperty("method").GetString();
            object response;
            if (method == "initialize") response = new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "IronPaw independent process fixture", version = "1" } };
            else if (method == "tools/list") response = new { tools = new[] { new { name = "record", inputSchema = schema } } };
            else if (method == "tools/call")
            {
                await File.AppendAllTextAsync(Path.Combine(root, "effects"), "effect\n");
                if (args.Contains("stderr")) { await Console.Error.WriteLineAsync(new string('x', 9000)); await Console.Error.FlushAsync(); await Task.Delay(Timeout.Infinite); }
                if (args.Contains("hang")) { await Task.Delay(Timeout.Infinite); return; }
                if (args.Contains("oversized")) { await Console.Out.WriteLineAsync(new string('x', 9000)); await Console.Out.FlushAsync(); continue; }
                var inherited = Environment.GetEnvironmentVariable("IRONPAW_TEST_PRIVATE_SECRET") is not null;
                var explicitSecret = Environment.GetEnvironmentVariable("FIXTURE_TOKEN") is not null;
                response = new { content = new[] { new { type = "text", text = $"recorded; inherited={inherited}; explicit={explicitSecret}" } } };
            }
            else response = new { };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result = response }));
            await Console.Out.FlushAsync();
        }
        if (args.Contains("linger")) await Task.Delay(Timeout.Infinite); // Deliberately survive pipe EOF unless the owning job kills us.
    }
}
