using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.Testing;

namespace IronPaw.AgentCore.Mcp.Tests;

[TestClass]
[DoNotParallelize]
public sealed class McpHttpHostTests
{
    private static string Document(string endpoint, string? secretName = null, bool allowPrivate = true) => JsonSerializer.Serialize(
        new McpHostConfiguration(1, [new("http_fixture", "reviewed-1",
            new("http", Endpoint: endpoint, AllowPrivateNetwork: allowPrivate, BearerTokenEnvironmentVariable: secretName, CredentialIdentity: secretName is null ? null : "fixture-account"),
            [new("record", "Reviewed record", McpConnectorTests.Schema)], TimeoutSeconds: 3)]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static Task<ToolResult> Invoke(McpHost host) => host.Tools[0].ExecuteAsync(
        new("call", host.ToolNames[0], McpConnectorTests.Json("{\"value\":2}")),
        new(RunId.New(), new("http-fixture"), "local", "fixture", CallerIdentity.LocalOwner(), ToolPolicy: host.Grant(new(ToolProfile.Coding))), default);

    [TestMethod]
    public async Task RealHttpHandshakeAndCallUseOnlyReviewedEndpointAndExplicitBearerReference()
    {
        const string variable = "IRONPAW_MCP_TEST_BEARER";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "fixture-secret");
        try
        {
            await using var server = new LoopbackHttpServer(request =>
            {
                Assert.AreEqual("Bearer fixture-secret", request.Headers["Authorization"]);
                Assert.AreEqual("/mcp", request.Path);
                using var json = JsonDocument.Parse(request.Body);
                var message = json.RootElement;
                if (!message.TryGetProperty("id", out var id)) return Task.FromResult(new LoopbackHttpServer.Response("", 202));
                object result = message.GetProperty("method").GetString() switch
                {
                    "initialize" => new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "independent-http-peer", version = "1" } },
                    "tools/list" => new { tools = new[] { new { name = "record", inputSchema = McpConnectorTests.Schema } } },
                    _ => new { content = new[] { new { type = "text", text = "http result" } } }
                };
                return Task.FromResult(new LoopbackHttpServer.Response(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result }), ContentType: "application/json"));
            });
            using var host = McpHost.Parse(Document(server.Origin + "/mcp", variable));
            Assert.IsEmpty(server.Requests);
            Assert.DoesNotContain("fixture-secret", host.Review);
            Assert.Contains("http result", (await Invoke(host)).Content);
            Assert.AreEqual(1, server.Requests.Count(request => request.Body.Contains("tools/call", StringComparison.Ordinal)));
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    [TestMethod]
    public async Task RedirectsCannotDeliverAuthorityToAnotherTarget()
    {
        await using var forbidden = new LoopbackHttpServer(_ => Task.FromResult(new LoopbackHttpServer.Response("must not be contacted")));
        await using var initial = new LoopbackHttpServer(_ => Task.FromResult(new LoopbackHttpServer.Response("", 302, Headers: new Dictionary<string, string> { ["Location"] = forbidden.Origin + "/other" })));
        using var host = McpHost.Parse(Document(initial.Origin + "/mcp"));
        await Assert.ThrowsExactlyAsync<IOException>(() => Invoke(host));
        Assert.IsEmpty(forbidden.Requests);
        Assert.AreEqual(1, initial.Requests.Count);
    }

    [TestMethod]
    public async Task PublicOnlyGrantRejectsPrivateResolutionBeforeConnection()
    {
        await using var server = new LoopbackHttpServer(_ => Task.FromResult(new LoopbackHttpServer.Response("must not be contacted")));
        using var host = McpHost.Parse(Document(server.Origin.Replace("http:", "https:", StringComparison.Ordinal) + "/mcp", allowPrivate: false));
        await Assert.ThrowsExactlyAsync<IOException>(() => Invoke(host));
        Assert.IsEmpty(server.Requests);
    }
}
