using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Mcp.Tests;

public sealed class McpConnectorTests
{
    internal static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    internal static readonly JsonElement Schema = Json("""{"type":"object","properties":{"value":{"type":"integer","minimum":1,"maximum":5}},"required":["value"],"additionalProperties":false}""");
    internal static McpConnectorOptions Options => new("local_fixture", "reviewed-endpoint-revision-1", [new("record", "Reviewed effect", Schema)]);
    internal static ToolSelectionPolicy Policy(McpConnector connector) => new(ToolProfile.Full, SenderIsOwner: true) { AllowedMcpTools = connector.ToolNames };
    internal static ToolExecutionContext Context(McpConnector connector) => new(RunId.New(), new("mcp"), "local", "fixture", CallerIdentity.LocalOwner(), ToolPolicy: Policy(connector));
    internal static Task<ToolResult> Invoke(McpConnector connector, string arguments = "{\"value\":2}", ToolExecutionContext? context = null, CancellationToken ct = default) =>
        connector.Tools[0].ExecuteAsync(new("call-1", connector.ToolNames[0], Json(arguments)), context ?? Context(connector), ct);

    [Fact]
    public async Task RegistrationIsLazy_ProfilesAndInheritedPoliciesCannotGrantImplicitAccess()
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        var registry = new InMemoryToolRegistry(connector.Tools);
        foreach (var profile in Enum.GetValues<ToolProfile>())
            Assert.Empty(registry.ResolveTools(new(profile, SenderIsOwner: true)));
        Assert.Single(registry.ResolveTools(Policy(connector)));
        Assert.Single(registry.ResolveTools(Policy(connector) with { Profile = ToolProfile.Coding }));
        Assert.Empty(registry.ResolveTools(Policy(connector) with { SenderIsOwner = false }));
        Assert.Empty(registry.ResolveTools(Policy(connector) with { Profile = ToolProfile.Minimal }));
        Assert.Empty(registry.ResolveTools(Policy(connector) with { DenyList = connector.ToolNames }));
        Assert.Empty(registry.ResolveTools(Policy(connector) with { AllowList = ["read_file"] }));
        Assert.Empty(registry.ResolveTools(Policy(connector) with { AllowedGroups = ["group:fs"] }));
        Assert.Empty(registry.ResolveTools(Policy(connector) with { InheritedPolicy = new(ToolProfile.Full, true) }));
        Assert.Single(registry.ResolveTools(Policy(connector) with { InheritedPolicy = Policy(connector) }));
        var reviewer = new InMemoryAgentRegistry();
        DefaultAgentCatalog.Register(reviewer, new());
        Assert.Empty(registry.ResolveTools(reviewer.Get("reviewer")!.ToolPolicy with { SenderIsOwner = true, AllowedMcpTools = connector.ToolNames }));
        Assert.Equal(0, fixture.Connections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectInvocationRechecksActualCallerAndInheritedGrantBeforeConnecting(bool inherited)
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        var context = Context(connector);
        context = inherited ? context with { ToolPolicy = Policy(connector) with { InheritedPolicy = new(ToolProfile.Full, true) } }
            : context with { Caller = CallerIdentity.Authenticated("outsider", "Outsider") };
        var result = await Invoke(connector, context: context);
        Assert.True(result.IsError);
        Assert.Equal(0, fixture.Connections);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"value\":\"2\"}")]
    [InlineData("{\"value\":0}")]
    [InlineData("{\"value\":6}")]
    [InlineData("{\"value\":2,\"extra\":1}")]
    [InlineData("{\"value\":2,\"value\":3}")]
    public async Task InvalidArgumentsNeverConnectOrDispatch(string arguments)
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        Assert.True((await Invoke(connector, arguments)).IsError);
        Assert.Equal(0, fixture.Connections);
    }

    [Fact]
    public async Task RealSdkHandshakeAndToolCallUseReviewedNamesAndUntrustedResults()
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        var tool = connector.Tools[0];
        Assert.Equal("Reviewed effect", tool.Descriptor.Description);
        Assert.True(tool.Descriptor.OwnerOnly);
        Assert.Equal(64, tool.Descriptor.Name.Length);
        var result = await Invoke(connector);
        Assert.False(result.IsError);
        Assert.Equal(ContentTrust.Untrusted, result.Trust);
        Assert.Equal("connector result", result.Content);
        Assert.Equal(2, Assert.Single(fixture.Arguments).GetProperty("value").GetInt32());
        Assert.Empty(fixture.Initialize!.Value.GetProperty("capabilities").EnumerateObject());
        Assert.Equal(1, fixture.Connections);
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(1, fixture.Lists);
        Assert.False(result.Details!.ContainsKey("error"));
        await Invoke(connector);
        Assert.Equal(2, fixture.Lists); // The SDK's discovery cache cannot silently authorize drift.
        Assert.Equal(2, fixture.Calls);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("cursor")]
    [InlineData("output")]
    public async Task CatalogDriftAndUnboundedDiscoveryNeverDispatch(string change)
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        var item = new { name = "record", inputSchema = Schema };
        fixture.Catalog = _ => change switch {
            "schema" => new { tools = new[] { new { name = "record", inputSchema = Json("""{"type":"object"}""") } } },
            "missing" => new { tools = Array.Empty<object>() },
            "duplicate" => new { tools = new[] { item, item } },
            "cursor" => new { tools = new[] { item }, nextCursor = "repeat" },
            _ => new { tools = new[] { new { name = "record", inputSchema = Schema, outputSchema = Json("""{"type":"object"}""") } } }
        };
        Assert.True((await Invoke(connector)).IsError);
        Assert.Equal(0, fixture.Calls);
        Assert.InRange(fixture.Lists, 1, 2);
    }

    [Fact]
    public async Task CatalogIsCheckedOnEveryCall_AdditionalToolsCannotBecomeModelTools()
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        fixture.Catalog = _ => new { tools = new[] { new { name = "record", inputSchema = Schema }, new { name = "unreviewed", inputSchema = Schema } } };
        Assert.False((await Invoke(connector)).IsError);
        Assert.Single(connector.Tools);
        fixture.Catalog = _ => new { tools = new[] { new { name = "record", inputSchema = Json("""{"type":"object"}""") } } };
        Assert.True((await Invoke(connector)).IsError);
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task PresentationIsBoundedAndResourceLinksAreNeverFetched()
    {
        await using var fixture = new McpFixture(Schema);
        fixture.Execute = (_, _) => Task.FromResult<object?>(new { content = new object[] {
            new { type = "resource_link", uri = "https://private.invalid/secret", name = "private" },
            new { type = "text", text = new string('x', 100_000) } } });
        await using var connector = new McpConnector(Options with { MaxResultCharacters = 256 }, fixture.Transport);
        var result = await Invoke(connector);
        Assert.Equal(256, result.Content.Length);
        Assert.Contains("Non-text MCP content omitted", result.Content);
        Assert.EndsWith("[MCP result truncated]", result.Content);
        Assert.DoesNotContain("private.invalid", result.Content);
        Assert.Equal(true, result.Details!["result_truncated"]);
        Assert.DoesNotContain(fixture.Methods, method => method.StartsWith("resources/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ServerDeclaredErrorIsACompletedUntrustedResult(bool isError)
    {
        await using var fixture = new McpFixture(Schema);
        fixture.Execute = (_, _) => Task.FromResult<object?>(new { isError, content = new[] { new { type = "text", text = "effect response" } } });
        await using var connector = new McpConnector(Options, fixture.Transport);
        var result = await Invoke(connector);
        Assert.Equal(isError, result.IsError);
        Assert.Equal(ContentTrust.Untrusted, result.Trust);
        Assert.False(result.Details!.ContainsKey("error"));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(8, true)]
    public async Task StructuredResultsMustSatisfyTheReviewedOutputSchema(int value, bool error)
    {
        await using var fixture = new McpFixture(Schema);
        fixture.Catalog = _ => new { tools = new[] { new { name = "record", inputSchema = Schema, outputSchema = Schema } } };
        fixture.Execute = (_, _) => Task.FromResult<object?>(new { structuredContent = new { value }, content = Array.Empty<object>() });
        await using var connector = new McpConnector(Options with { Tools = [new("record", "Reviewed effect", Schema, Schema)] }, fixture.Transport);
        var result = await Invoke(connector);
        Assert.Equal(error, result.IsError);
        Assert.Equal(ContentTrust.Untrusted, result.Trust);
        Assert.False(result.Details!.ContainsKey("error"));
    }

    [Fact]
    public async Task BindingCatalogAndLimitsChangesRequireDifferentGrants_SnapshotsAreImmutable()
    {
        await using var fixture = new McpFixture(Schema);
        var definitions = Options.Tools.ToList();
        await using var original = new McpConnector(Options with { Tools = definitions }, fixture.Transport);
        definitions.Clear();
        Assert.Single(original.Tools);
        foreach (var options in new[] {
            Options with { BindingRevision = "other-target-revision" }, Options with { TimeoutSeconds = 31 },
            Options with { MaxResultCharacters = 512 }, Options with { Tools = [new("record", "New meaning", Schema)] } })
        {
            await using var changed = new McpConnector(options, fixture.Transport);
            Assert.NotEqual(original.ToolNames[0], changed.ToolNames[0]);
            Assert.Empty(new InMemoryToolRegistry(changed.Tools).ResolveTools(Policy(original)));
        }
        var saved = JsonSerializer.Deserialize<ToolSelectionPolicy>(JsonSerializer.Serialize(Policy(original)))!;
        Assert.Equal(original.ToolNames, saved.AllowedMcpTools);
        Assert.Equal(0, fixture.Connections);
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"$ref\":\"https://example.invalid/remote\"}")]
    [InlineData("{\"type\":\"object\",\"$ref\":\"#/$defs/value\"}")]
    [InlineData("{\"type\":\"object\",\"patternProperties\":{\".*\":{}}}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"v\":{\"type\":\"string\",\"pattern\":\"(x+)+$\"}}}")]
    [InlineData("{\"type\":\"array\"}")]
    public void UnsupportedSchemasFailAtRegistrationWithoutTransport(string schema)
    {
        var calls = 0;
        Assert.ThrowsAny<ArgumentException>(() => new McpConnector(Options with { Tools = [new("record", "Reviewed", Json(schema))] }, () => {
            calls++; throw new InvalidOperationException();
        }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellationAfterDispatchPoisonsTheConnectionAndNeverAutomaticallyRetries()
    {
        await using var fixture = new McpFixture(Schema);
        fixture.Execute = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; };
        await using var connector = new McpConnector(Options, fixture.Transport);
        using var cancellation = new CancellationTokenSource();
        var pending = Invoke(connector, ct: cancellation.Token);
        await fixture.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<IOException>(() => Invoke(connector));
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(1, fixture.Connections);
    }

    [Fact]
    public async Task ConfiguredTimeoutStopsAnUnansweredRequestWithoutRetrying()
    {
        await using var fixture = new McpFixture(Schema);
        fixture.Execute = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; };
        await using var connector = new McpConnector(Options with { TimeoutSeconds = 1 }, fixture.Transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(connector).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fixture.Calls);
        await Assert.ThrowsAsync<IOException>(() => Invoke(connector));
        Assert.Equal(1, fixture.Connections);
    }

    [Fact]
    public async Task ConcurrentRequestsAreSerialized_QueueCancellationDoesNotPoisonAnUnsentCall()
    {
        await using var fixture = new McpFixture(Schema);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Execute = async (_, ct) => { await release.Task.WaitAsync(ct); return new { content = Array.Empty<object>() }; };
        await using var connector = new McpConnector(Options, fixture.Transport);
        var first = Invoke(connector);
        await fixture.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var queued = Invoke(connector, ct: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, fixture.Calls);
        release.TrySetResult();
        Assert.False((await first).IsError);
        Assert.False((await Invoke(connector)).IsError);
        Assert.Equal(2, fixture.Calls);
        Assert.Equal(1, fixture.Connections);
    }

    [Fact]
    public async Task DisposalCancelsAnInFlightCallAndIsIdempotent()
    {
        await using var fixture = new McpFixture(Schema);
        fixture.Execute = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; };
        var connector = new McpConnector(Options, fixture.Transport);
        var pending = Invoke(connector);
        await fixture.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = connector.DisposeAsync().AsTask();
        var second = connector.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Invoke(connector));
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task OversizeAndExcessivelyNestedArgumentsNeverConnect()
    {
        await using var fixture = new McpFixture(Schema);
        var schema = Json("""{"type":"object"}""");
        await using var connector = new McpConnector(Options with { Tools = [new("record", "Reviewed", schema)], MaxArgumentBytes = 256 }, fixture.Transport);
        Assert.True((await Invoke(connector, "{\"text\":\"" + new string('x', 256) + "\"}")).IsError);
        var nested = string.Concat(Enumerable.Repeat("{\"x\":", 33)) + "{}" + new string('}', 33);
        Assert.True((await Invoke(connector, nested)).IsError);
        Assert.Equal(0, fixture.Connections);
    }

    [Fact]
    public async Task ProtocolExceptionTextCannotBecomeTrustedHostContent()
    {
        await using var fixture = new McpFixture(Schema) { ProtocolError = "PRIVATE server-controlled error payload" };
        await using var connector = new McpConnector(Options, fixture.Transport);
        var result = await new ToolExecutor().ExecuteAsync(connector.Tools[0],
            new("call-1", connector.ToolNames[0], Json("""{"value":2}""")), Context(connector), default);
        Assert.True(result.IsError);
        Assert.True(result.Details!.ContainsKey("error"));
        Assert.DoesNotContain("PRIVATE", result.Content);
        Assert.DoesNotContain("PRIVATE", result.Details["error"]!.ToString());
        Assert.Equal(1, fixture.Calls);
        await Assert.ThrowsAsync<IOException>(() => Invoke(connector));
        Assert.Equal(1, fixture.Calls);
    }
}
