using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace IronPaw.AgentCore.Mcp;

/// <summary>
/// Adapts a reviewed MCP catalog to ordinary IronPaw tools. The trusted host owns transport
/// authorization and supplies a new, unconnected transport; construction/registration does no I/O.
/// Dispose after all runs using these tools have settled. No reconnect or tool retry is performed.
/// </summary>
public sealed class McpConnector : IAsyncDisposable
{
    private readonly Func<IClientTransport> _transportFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly McpConnectorOptions _options;
    private McpClient? _client;
    private bool _faulted;
    private int _disposed;
    private readonly TaskCompletionSource _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<ITool> Tools { get; }
    public IReadOnlyList<string> ToolNames { get; }

    public McpConnector(McpConnectorOptions options, Func<IClientTransport> transportFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transportFactory);
        if (string.IsNullOrWhiteSpace(options.Id) || options.Id.Length > 64 || options.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Connector ID must contain 1-64 ASCII letters, digits, underscores or hyphens.");
        if (string.IsNullOrWhiteSpace(options.BindingRevision) || options.BindingRevision.Length > 1024)
            throw new ArgumentException("A nonsecret reviewed transport binding revision is required.");
        if (options.TimeoutSeconds is < 1 or > 300 || options.MaxArgumentBytes is < 256 or > 262_144 ||
            options.MaxResultCharacters is < 256 or > 65_536)
            throw new ArgumentException("Connector limits are outside their supported range.");
        if (options.Tools is null || options.Tools.Count is < 1 or > 128)
            throw new ArgumentException("A connector must have 1-128 reviewed tools.");
        var definitions = options.Tools.Select(definition =>
        {
            if (definition is null || string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 128 ||
                definition.Name.Any(char.IsControl) || definition.Description is null || definition.Description.Length > 4096)
                throw new ArgumentException("Invalid reviewed connector tool definition.");
            return definition with { InputSchema = definition.InputSchema.Clone(), OutputSchema = definition.OutputSchema?.Clone() };
        }).ToArray();
        if (definitions.Select(d => d.Name).Distinct(StringComparer.Ordinal).Count() != definitions.Length)
            throw new ArgumentException("A connector catalog cannot contain duplicate tool names.");
        _options = options with { Tools = Array.AsReadOnly(definitions) };
        _transportFactory = transportFactory;
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            ConnectorSchema.Canonical(JsonSerializer.SerializeToElement(_options)))));
        Tools = Array.AsReadOnly<ITool>(definitions.Select(definition => new ConnectorTool(this, definition, identity)).ToArray());
        ToolNames = Array.AsReadOnly(Tools.Select(tool => tool.Descriptor.Name).ToArray());
    }

    private async Task<ToolResult> InvokeAsync(ConnectorTool tool, ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!string.Equals(call.ToolName, tool.Descriptor.Name, StringComparison.Ordinal) || !context.Caller.IsOwner || context.ToolPolicy is null ||
            ToolPolicyEvaluator.Apply([tool], context.ToolPolicy with { SenderIsOwner = context.Caller.IsOwner }).Count != 1)
            return Rejected("Connector tool access is not granted by the current and inherited policies.");
        if (!ConnectorSchema.ValidValue(call.Arguments, _options.MaxArgumentBytes) || !tool.Input.Accepts(call.Arguments))
            return Rejected("Connector arguments do not satisfy the reviewed schema or input limits.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_faulted) throw new InvalidOperationException("Connector is unavailable after a transport failure; inspect saved effects before configuring a new connection.");
            _client ??= await McpClient.CreateAsync(_transportFactory(), new McpClientOptions
            {
                ClientInfo = new() { Name = "IronPaw", Version = "1.0.0" },
                Capabilities = new(), // No sampling, elicitation, roots, tasks or experimental handlers.
                ProtocolVersion = "2025-11-25",
                InitializationTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
            }, cancellationToken: deadline.Token).ConfigureAwait(false);

            // Re-check immediately before each call. Discovery can remove access, never grant it.
            if (!await CatalogMatchesAsync(tool, deadline.Token).ConfigureAwait(false))
                return Rejected("The server tool catalog differs from the reviewed binding; review and register a new binding before calling it.");
            var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments.GetRawText())!;
            var result = await _client.CallToolAsync(tool.Definition.Name, arguments,
                cancellationToken: deadline.Token).ConfigureAwait(false);
            return ConvertResult(tool, result);
        }
        catch (OperationCanceledException)
        {
            // A lost/cancelled response may follow an effect. Propagate to ToolExecutor's unknown
            // outcome path and poison this session. Never transparently repeat the request.
            _faulted = true;
            await CloseFaultedClientAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            _faulted = true;
            await CloseFaultedClientAsync().ConfigureAwait(false);
            // Protocol exceptions may contain server-controlled text. Do not promote that text
            // to ToolExecutor's trusted host error or retain an unbounded nested exception.
            throw new IOException("The MCP request did not establish a usable outcome. Inspect saved effects before resuming.");
        }
        finally { _gate.Release(); }
    }

    private async Task CloseFaultedClientAsync()
    {
        if (_client is null) return;
        try { await _client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception) { /* Preserve the original unknown outcome; never retry an effect. */ }
        _client = null;
    }

    private async Task<bool> CatalogMatchesAsync(ConnectorTool requested, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        Tool? match = null;
        string? cursor = null;
        for (var page = 0; page < 16; page++)
        {
            var result = await _client!.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, ct).ConfigureAwait(false);
            foreach (var item in result.Tools)
            {
                if (names.Count >= 128 || !names.Add(item.Name)) return false;
                if (item.Name == requested.Definition.Name) match = item;
            }
            cursor = result.NextCursor;
            if (cursor is null)
                return match is not null && SameSchema(match.InputSchema, requested.Definition.InputSchema) &&
                    SameSchema(match.OutputSchema, requested.Definition.OutputSchema);
            if (cursor.Length > 1024 || !cursors.Add(cursor)) return false;
        }
        return false;
    }

    private static bool SameSchema(JsonElement? actual, JsonElement? reviewed) => actual is null || reviewed is null
        ? actual is null && reviewed is null
        : Encoding.UTF8.GetByteCount(actual.Value.GetRawText()) <= 65_536 &&
            ConnectorSchema.Canonical(actual.Value) == ConnectorSchema.Canonical(reviewed.Value);

    private ToolResult ConvertResult(ConnectorTool tool, CallToolResult result)
    {
        var text = new StringBuilder();
        var truncated = false;
        void Append(string value)
        {
            var available = _options.MaxResultCharacters - text.Length;
            if (available <= 0) { truncated = true; return; }
            text.Append(value.AsSpan(0, Math.Min(available, value.Length)));
            if (value.Length > available) truncated = true;
        }
        if (result.StructuredContent is { } structured) Append(structured.GetRawText());
        foreach (var block in result.Content)
        {
            if (text.Length >= _options.MaxResultCharacters) { truncated = true; break; }
            if (text.Length > 0) Append("\n");
            // No fetching resource links, executing instructions, or decoding binary attachments.
            Append(block is TextContentBlock content ? content.Text : "[Non-text MCP content omitted]");
        }
        const string marker = "\n[MCP result truncated]";
        if (truncated)
        {
            text.Length = Math.Min(text.Length, _options.MaxResultCharacters - marker.Length);
            text.Append(marker);
        }
        var invalidOutput = result.IsError != true && tool.Output is not null &&
            (result.StructuredContent is not { } output || !tool.Output.Accepts(output));
        if (invalidOutput)
            return new(true, "Connector returned a result that does not satisfy the reviewed output schema.", ContentTrust.Untrusted, Details());
        return new(result.IsError == true, text.ToString(), ContentTrust.Untrusted, Details());

        IReadOnlyDictionary<string, object?> Details() => new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>
        {
            ["connector_id"] = _options.Id,
            ["connector_tool"] = tool.Descriptor.Name,
            ["remote_tool"] = tool.Definition.Name,
            ["result_truncated"] = truncated
        });
    }

    private static ToolResult Rejected(string message) => new(true, message, ContentTrust.Untrusted);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { await _disposal.Task.ConfigureAwait(false); return; }
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try { if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false); }
            finally { _gate.Release(); }
            _lifetime.Dispose();
            _disposal.TrySetResult();
        }
        catch (Exception error) { _disposal.TrySetException(error); throw; }
    }

    private sealed class ConnectorTool : ITool
    {
        private readonly McpConnector _owner;
        public McpToolDefinition Definition { get; }
        public ConnectorSchema Input { get; }
        public ConnectorSchema? Output { get; }
        public ToolDescriptor Descriptor { get; }

        public ConnectorTool(McpConnector owner, McpToolDefinition definition, string identity)
        {
            _owner = owner;
            Definition = definition;
            Input = new(definition.InputSchema, input: true);
            Output = definition.OutputSchema is { } output ? new(output, input: false) : null;
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\n" + definition.Name)));
            // 240-bit identity fits provider tool-name limits; target/schema/limits are bound into it.
            Descriptor = new("mcp_" + digest[..60], definition.Description, Input.Document, "group:mcp", OwnerOnly: true);
        }

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct) =>
            _owner.InvokeAsync(this, call, context, ct);
    }
}
