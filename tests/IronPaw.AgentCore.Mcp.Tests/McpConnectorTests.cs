using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Mcp.Tests;

[TestClass]
public sealed class McpConnectorTests
{
    internal static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    internal static readonly JsonElement Schema = Json("""{"type":"object","properties":{"value":{"type":"integer","minimum":1,"maximum":5}},"required":["value"],"additionalProperties":false}""");
    internal static McpConnectorOptions Options => new("local_fixture", "reviewed-endpoint-revision-1", [new("record", "Reviewed effect", Schema)]);
    internal static ToolSelectionPolicy Policy(McpConnector connector) => new(ToolProfile.Full, SenderIsOwner: true) { AllowedMcpTools = connector.ToolNames };
    internal static ToolExecutionContext Context(McpConnector connector) => new(RunId.New(), new("mcp"), "local", "fixture", CallerIdentity.LocalOwner(), ToolPolicy: Policy(connector));
    internal static Task<ToolResult> Invoke(McpConnector connector, string arguments = "{\"value\":2}", ToolExecutionContext? context = null, CancellationToken ct = default) =>
        connector.Tools[0].ExecuteAsync(new("call-1", connector.ToolNames[0], Json(arguments)), context ?? Context(connector), ct);

    [TestMethod]
    public async Task RegistrationIsLazy_ProfilesAndInheritedPoliciesCannotGrantImplicitAccess()
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        var registry = new InMemoryToolRegistry(connector.Tools);
        foreach (var profile in Enum.GetValues<ToolProfile>())
            Assert.IsEmpty(registry.ResolveTools(new(profile, SenderIsOwner: true)));
        Assert.ContainsSingle(registry.ResolveTools(Policy(connector)));
        Assert.ContainsSingle(registry.ResolveTools(Policy(connector) with { Profile = ToolProfile.Coding }));
        Assert.IsEmpty(registry.ResolveTools(Policy(connector) with { SenderIsOwner = false }));
        Assert.IsEmpty(registry.ResolveTools(Policy(connector) with { Profile = ToolProfile.Minimal }));
        Assert.IsEmpty(registry.ResolveTools(Policy(connector) with { DenyList = connector.ToolNames }));
        Assert.IsEmpty(registry.ResolveTools(Policy(connector) with { AllowList = ["read_file"] }));
        Assert.IsEmpty(registry.ResolveTools(Policy(connector) with { AllowedGroups = ["group:fs"] }));
        Assert.IsEmpty(registry.ResolveTools(Policy(connector) with { InheritedPolicy = new(ToolProfile.Full, true) }));
        Assert.ContainsSingle(registry.ResolveTools(Policy(connector) with { InheritedPolicy = Policy(connector) }));
        var reviewer = new InMemoryAgentRegistry();
        DefaultAgentCatalog.Register(reviewer, new());
        Assert.IsEmpty(registry.ResolveTools(reviewer.Get("reviewer")!.ToolPolicy with { SenderIsOwner = true, AllowedMcpTools = connector.ToolNames }));
        Assert.AreEqual(0, fixture.Connections);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DirectInvocationRechecksActualCallerAndInheritedGrantBeforeConnecting(bool inherited)
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        var context = Context(connector);
        context = inherited ? context with { ToolPolicy = Policy(connector) with { InheritedPolicy = new(ToolProfile.Full, true) } }
            : context with { Caller = CallerIdentity.Authenticated("outsider", "Outsider") };
        var result = await Invoke(connector, context: context);
        Assert.IsTrue(result.IsError);
        Assert.AreEqual(0, fixture.Connections);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("[]")]
    [DataRow("{\"value\":\"2\"}")]
    [DataRow("{\"value\":0}")]
    [DataRow("{\"value\":6}")]
    [DataRow("{\"value\":2,\"extra\":1}")]
    [DataRow("{\"value\":2,\"value\":3}")]
    public async Task InvalidArgumentsNeverConnectOrDispatch(string arguments)
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        Assert.IsTrue((await Invoke(connector, arguments)).IsError);
        Assert.AreEqual(0, fixture.Connections);
    }

    [TestMethod]
    public async Task RealSdkHandshakeAndToolCallUseReviewedNamesAndUntrustedResults()
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        var tool = connector.Tools[0];
        Assert.AreEqual("Reviewed effect", tool.Descriptor.Description);
        Assert.IsTrue(tool.Descriptor.OwnerOnly);
        Assert.AreEqual(64, tool.Descriptor.Name.Length);
        var result = await Invoke(connector);
        Assert.IsFalse(result.IsError);
        Assert.AreEqual(ContentTrust.Untrusted, result.Trust);
        Assert.AreEqual("connector result", result.Content);
        Assert.AreEqual(2, Assert.ContainsSingle(fixture.Arguments).GetProperty("value").GetInt32());
        Assert.IsEmpty(fixture.Initialize!.Value.GetProperty("capabilities").EnumerateObject());
        Assert.AreEqual(1, fixture.Connections);
        Assert.AreEqual(1, fixture.Calls);
        Assert.AreEqual(1, fixture.Lists);
        Assert.IsFalse(result.Details!.ContainsKey("error"));
        await Invoke(connector);
        Assert.AreEqual(2, fixture.Lists); // The SDK's discovery cache cannot silently authorize drift.
        Assert.AreEqual(2, fixture.Calls);
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("cursor")]
    [DataRow("output")]
    public async Task CatalogDriftAndUnboundedDiscoveryNeverDispatch(string change)
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        var item = new { name = "record", inputSchema = Schema };
        fixture.Catalog = _ => change switch
        {
            "schema" => new { tools = new[] { new { name = "record", inputSchema = Json("""{"type":"object"}""") } } },
            "missing" => new { tools = Array.Empty<object>() },
            "duplicate" => new { tools = new[] { item, item } },
            "cursor" => new { tools = new[] { item }, nextCursor = "repeat" },
            _ => new { tools = new[] { new { name = "record", inputSchema = Schema, outputSchema = Json("""{"type":"object"}""") } } }
        };
        Assert.IsTrue((await Invoke(connector)).IsError);
        Assert.AreEqual(0, fixture.Calls);
        Assert.IsInRange(1, 2, fixture.Lists);
    }

    [TestMethod]
    public async Task CatalogIsCheckedOnEveryCall_AdditionalToolsCannotBecomeModelTools()
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        fixture.Catalog = _ => new { tools = new[] { new { name = "record", inputSchema = Schema }, new { name = "unreviewed", inputSchema = Schema } } };
        Assert.IsFalse((await Invoke(connector)).IsError);
        Assert.ContainsSingle(connector.Tools);
        fixture.Catalog = _ => new { tools = new[] { new { name = "record", inputSchema = Json("""{"type":"object"}""") } } };
        Assert.IsTrue((await Invoke(connector)).IsError);
        Assert.AreEqual(1, fixture.Calls);
    }

    [TestMethod]
    public async Task PresentationIsBoundedAndResourceLinksAreNeverFetched()
    {
        await using var fixture = new McpFixture(Schema);
        fixture.Execute = (_, _) => Task.FromResult<object?>(new
        {
            content = new object[] {
            new { type = "resource_link", uri = "https://private.invalid/secret", name = "private" },
            new { type = "text", text = new string('x', 100_000) } }
        });
        await using var connector = new McpConnector(Options with { MaxResultCharacters = 256 }, fixture.Transport);
        var result = await Invoke(connector);
        Assert.AreEqual(256, result.Content.Length);
        Assert.Contains("Non-text MCP content omitted", result.Content);
        Assert.EndsWith("[MCP result truncated]", result.Content);
        Assert.DoesNotContain("private.invalid", result.Content);
        Assert.AreEqual(true, result.Details!["result_truncated"]);
        Assert.DoesNotContain(method => method.StartsWith("resources/", StringComparison.Ordinal), fixture.Methods);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ServerDeclaredErrorIsACompletedUntrustedResult(bool isError)
    {
        await using var fixture = new McpFixture(Schema);
        fixture.Execute = (_, _) => Task.FromResult<object?>(new { isError, content = new[] { new { type = "text", text = "effect response" } } });
        await using var connector = new McpConnector(Options, fixture.Transport);
        var result = await Invoke(connector);
        Assert.AreEqual(isError, result.IsError);
        Assert.AreEqual(ContentTrust.Untrusted, result.Trust);
        Assert.IsFalse(result.Details!.ContainsKey("error"));
    }

    [TestMethod]
    [DataRow(2, false)]
    [DataRow(8, true)]
    public async Task StructuredResultsMustSatisfyTheReviewedOutputSchema(int value, bool error)
    {
        await using var fixture = new McpFixture(Schema);
        fixture.Catalog = _ => new { tools = new[] { new { name = "record", inputSchema = Schema, outputSchema = Schema } } };
        fixture.Execute = (_, _) => Task.FromResult<object?>(new { structuredContent = new { value }, content = Array.Empty<object>() });
        await using var connector = new McpConnector(Options with { Tools = [new("record", "Reviewed effect", Schema, Schema)] }, fixture.Transport);
        var result = await Invoke(connector);
        Assert.AreEqual(error, result.IsError);
        Assert.AreEqual(ContentTrust.Untrusted, result.Trust);
        Assert.IsFalse(result.Details!.ContainsKey("error"));
    }

    [TestMethod]
    public async Task BindingCatalogAndLimitsChangesRequireDifferentGrants_SnapshotsAreImmutable()
    {
        await using var fixture = new McpFixture(Schema);
        var definitions = Options.Tools.ToList();
        await using var original = new McpConnector(Options with { Tools = definitions }, fixture.Transport);
        definitions.Clear();
        Assert.ContainsSingle(original.Tools);
        foreach (var options in new[] {
            Options with { BindingRevision = "other-target-revision" }, Options with { TimeoutSeconds = 31 },
            Options with { MaxResultCharacters = 512 }, Options with { Tools = [new("record", "New meaning", Schema)] } })
        {
            await using var changed = new McpConnector(options, fixture.Transport);
            Assert.AreNotEqual(original.ToolNames[0], changed.ToolNames[0]);
            Assert.IsEmpty(new InMemoryToolRegistry(changed.Tools).ResolveTools(Policy(original)));
        }
        var saved = JsonSerializer.Deserialize<ToolSelectionPolicy>(JsonSerializer.Serialize(Policy(original)))!;
        Assert.AreSequenceEqual(original.ToolNames, saved.AllowedMcpTools);
        Assert.AreEqual(0, fixture.Connections);
    }

    [TestMethod]
    [DataRow("http://json-schema.org/draft-07/schema#")]
    [DataRow("https://json-schema.org/draft/2020-12/schema")]
    public async Task ExplicitSupportedDialectsValidateBeforeDispatch(string dialect)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(Schema.GetRawText())!.AsObject();
        document["$schema"] = dialect;
        var schema = Json(document.ToJsonString());
        await using var fixture = new McpFixture(schema);
        await using var connector = new McpConnector(Options with { Tools = [new("record", "Reviewed", schema)] }, fixture.Transport);
        Assert.IsTrue((await Invoke(connector, "{\"value\":0}")).IsError);
        Assert.AreEqual(0, fixture.Connections);
        Assert.IsFalse((await Invoke(connector)).IsError);
        Assert.AreEqual(1, fixture.Calls);
    }

    [TestMethod]
    [DataRow("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"$ref\":\"https://example.invalid/schema\"}")]
    [DataRow("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"properties\":{\"v\":{\"pattern\":\"(x+)+$\"}}}")]
    [DataRow("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"properties\":{\"v\":{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\"}}}")]
    [DataRow("{\"$schema\":\"https://example.invalid/dialect\",\"type\":\"object\"}")]
    [DataRow("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"dependencies\":{\"x\":{\"properties\":{\"v\":{\"pattern\":\"(x+)+$\"}}}}}")]
    [DataRow("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"definitions\":{\"v\":{\"$ref\":\"https://example.invalid/remote\"}}}")]
    [DataRow("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"properties\":{\"v\":{\"items\":[{}],\"additionalItems\":{\"pattern\":\"(x+)+$\"}}}}")]
    public void UnsupportedDialectOrUnsafeDraft7SchemaFailsWithoutTransport(string schema) => UnsupportedSchemasFailAtRegistrationWithoutTransport(schema);

    [TestMethod]
    [DataRow("{\"type\":\"object\",\"$ref\":\"https://example.invalid/remote\"}")]
    [DataRow("{\"type\":\"object\",\"$ref\":\"#/$defs/value\"}")]
    [DataRow("{\"type\":\"object\",\"patternProperties\":{\".*\":{}}}")]
    [DataRow("{\"type\":\"object\",\"properties\":{\"v\":{\"type\":\"string\",\"pattern\":\"(x+)+$\"}}}")]
    [DataRow("{\"type\":\"array\"}")]
    public void UnsupportedSchemasFailAtRegistrationWithoutTransport(string schema)
    {
        var calls = 0;
        Assert.Throws<ArgumentException>(() => new McpConnector(Options with { Tools = [new("record", "Reviewed", Json(schema))] }, () =>
        {
            calls++; throw new InvalidOperationException();
        }));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task CancellationAfterDispatchPoisonsTheConnectionAndNeverAutomaticallyRetries()
    {
        await using var fixture = new McpFixture(Schema);
        fixture.Execute = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; };
        await using var connector = new McpConnector(Options, fixture.Transport);
        using var cancellation = new CancellationTokenSource();
        var pending = Invoke(connector, ct: cancellation.Token);
        await fixture.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsExactlyAsync<IOException>(() => Invoke(connector));
        Assert.AreEqual(1, fixture.Calls);
        Assert.AreEqual(1, fixture.Connections);
    }

    [TestMethod]
    public async Task ConfiguredTimeoutStopsAnUnansweredRequestWithoutRetrying()
    {
        await using var fixture = new McpFixture(Schema);
        fixture.Execute = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; };
        await using var connector = new McpConnector(Options with { TimeoutSeconds = 1 }, fixture.Transport);
        await Assert.ThrowsAsync<OperationCanceledException>(() => Invoke(connector).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, fixture.Calls);
        await Assert.ThrowsExactlyAsync<IOException>(() => Invoke(connector));
        Assert.AreEqual(1, fixture.Connections);
    }

    [TestMethod]
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
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        Assert.AreEqual(1, fixture.Calls);
        release.TrySetResult();
        Assert.IsFalse((await first).IsError);
        Assert.IsFalse((await Invoke(connector)).IsError);
        Assert.AreEqual(2, fixture.Calls);
        Assert.AreEqual(1, fixture.Connections);
    }

    [TestMethod]
    public async Task DisposalCancelsAnInFlightCallAndIsIdempotent()
    {
        await using var fixture = new McpFixture(Schema);
        fixture.Execute = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; };
        var connector = new McpConnector(Options, fixture.Transport);
        var pending = Invoke(connector);
        await fixture.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = connector.DisposeAsync().AsTask();
        var second = connector.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => Invoke(connector));
        Assert.AreEqual(1, fixture.Calls);
    }

    [TestMethod]
    public async Task OversizeAndExcessivelyNestedArgumentsNeverConnect()
    {
        await using var fixture = new McpFixture(Schema);
        var schema = Json("""{"type":"object"}""");
        await using var connector = new McpConnector(Options with { Tools = [new("record", "Reviewed", schema)], MaxArgumentBytes = 256 }, fixture.Transport);
        Assert.IsTrue((await Invoke(connector, "{\"text\":\"" + new string('x', 256) + "\"}")).IsError);
        var nested = string.Concat(Enumerable.Repeat("{\"x\":", 33)) + "{}" + new string('}', 33);
        Assert.IsTrue((await Invoke(connector, nested)).IsError);
        Assert.AreEqual(0, fixture.Connections);
    }

    [TestMethod]
    public async Task ProtocolExceptionTextCannotBecomeTrustedHostContent()
    {
        await using var fixture = new McpFixture(Schema) { ProtocolError = "PRIVATE server-controlled error payload" };
        await using var connector = new McpConnector(Options, fixture.Transport);
        var result = await new ToolExecutor().ExecuteAsync(connector.Tools[0],
            new("call-1", connector.ToolNames[0], Json("""{"value":2}""")), Context(connector), default);
        Assert.IsTrue(result.IsError);
        Assert.IsTrue(result.Details!.ContainsKey("error"));
        Assert.DoesNotContain("PRIVATE", result.Content);
        var errorDetail = result.Details["error"]!.ToString();
        Assert.IsNotNull(errorDetail);
        Assert.DoesNotContain("PRIVATE", errorDetail);
        Assert.AreEqual(1, fixture.Calls);
        await Assert.ThrowsExactlyAsync<IOException>(() => Invoke(connector));
        Assert.AreEqual(1, fixture.Calls);
    }
}
