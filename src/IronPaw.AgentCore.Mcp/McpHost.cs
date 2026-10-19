using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Mcp;

public sealed record McpHostConfiguration(int SchemaVersion, IReadOnlyList<McpHostConnector> Connectors);
public sealed record McpHostConnector(string Id, string BindingRevision, McpHostTransport Transport,
    IReadOnlyList<McpToolDefinition> Tools, int TimeoutSeconds = 30, int MaxArgumentBytes = 65_536,
    int MaxResultCharacters = 16_384);
public sealed record McpHostTransport(
    string Kind,
    string? Command = null,
    string? ExecutableSha256 = null,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    string? Endpoint = null,
    string? BearerTokenEnvironmentVariable = null,
    string? CredentialIdentity = null,
    bool AllowPrivateNetwork = false,
    int MaxFrameBytes = 1_048_576,
    int MaxSessionBytes = 16_777_216,
    int MaxSessionFrames = 4096,
    IReadOnlyDictionary<string, string>? ReviewedFiles = null,
    McpProcessLimits? ProcessLimits = null);

/// <summary>Explicit host grants. Loading a document never connects, launches a process or reads secrets.</summary>
public sealed class McpHost : IDisposable
{
    private readonly McpConnector[] _connectors;
    private int _disposed;
    public IReadOnlyList<ITool> Tools { get; }
    public IReadOnlyList<string> ToolNames { get; }
    public string Review { get; }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static McpHost Load(string path)
    {
        using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1_048_576) throw new ArgumentException("Connector configuration exceeds 1 MiB.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return Parse(reader.ReadToEnd());
    }

    public static McpHost Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 1_048_576) throw new ArgumentException("Connector configuration exceeds 1 MiB.");
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 });
        RejectDuplicates(document.RootElement);
        var configuration = document.Deserialize<McpHostConfiguration>(JsonOptions)
            ?? throw new ArgumentException("Connector configuration must be an object.");
        return new(configuration);
    }

    private McpHost(McpHostConfiguration configuration)
    {
        if (configuration.SchemaVersion != 1 || configuration.Connectors is null || configuration.Connectors.Count is < 1 or > 16)
            throw new ArgumentException("Connector configuration requires schemaVersion 1 and 1-16 connectors.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connectors = new List<McpConnector>();
        var review = new StringBuilder("Only the listed tools are enabled. Each operation requires approval.\n");
        foreach (var supplied in configuration.Connectors)
        {
            if (supplied is null || !ids.Add(supplied.Id)) throw new ArgumentException("Connector IDs must be distinct.");
            var definition = supplied with { Transport = supplied.Transport is { Kind: "stdio", ProcessLimits: null } transport ? transport with { ProcessLimits = new() } : supplied.Transport };
            if (string.IsNullOrWhiteSpace(definition.BindingRevision)) throw new ArgumentException("Each connector requires a reviewed bindingRevision.");
            HostedMcpTransport.Validate(definition.Transport);
            // Bind every transport option, secret reference/identity, catalog and limit into saved grants.
            var binding = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                ConnectorSchema.Canonical(JsonSerializer.SerializeToElement(definition, JsonOptions)))));
            var connector = new McpConnector(new(definition.Id, binding, definition.Tools,
                definition.TimeoutSeconds, definition.MaxArgumentBytes, definition.MaxResultCharacters),
                () => new HostedMcpTransport(definition.Id, definition.Transport));
            connectors.Add(connector);
            review.AppendLine($"{definition.Id}: {JsonSerializer.Serialize(definition.Transport.Command ?? definition.Transport.Endpoint)}");
            if (definition.Transport.Kind == "stdio")
            {
                review.AppendLine("  Launch arguments: " + JsonSerializer.Serialize(definition.Transport.Arguments ?? []));
                review.AppendLine("  Working directory: " + JsonSerializer.Serialize(definition.Transport.WorkingDirectory));
                review.AppendLine("  Executable SHA-256: " + definition.Transport.ExecutableSha256);
                review.AppendLine("  Reviewed launch files: " + JsonSerializer.Serialize(definition.Transport.ReviewedFiles));
                review.AppendLine("  Environment references (target: source variable): " + JsonSerializer.Serialize(definition.Transport.EnvironmentVariables));
                review.AppendLine("  Process limits: " + JsonSerializer.Serialize(definition.Transport.ProcessLimits));
                review.AppendLine("  Process runs with your OS user authority; these limits do not sandbox its filesystem/network access.");
            }
            review.AppendLine("  Credential identity: " + JsonSerializer.Serialize(definition.Transport.CredentialIdentity ?? "none"));
            review.AppendLine("  Bearer token variable: " + JsonSerializer.Serialize(definition.Transport.BearerTokenEnvironmentVariable));
            review.AppendLine("  Private network: " + definition.Transport.AllowPrivateNetwork);
            review.AppendLine($"  Deadline: {definition.TimeoutSeconds}s; argument bytes: {definition.MaxArgumentBytes}; result characters: {definition.MaxResultCharacters}");
            review.AppendLine($"  Wire limits: {definition.Transport.MaxFrameBytes} bytes/line; {definition.Transport.MaxSessionBytes} bytes and {definition.Transport.MaxSessionFrames} frames/requests per session");
            for (var index = 0; index < connector.Tools.Count; index++)
            {
                review.AppendLine($"  {JsonSerializer.Serialize(definition.Tools[index].Name)}: {JsonSerializer.Serialize(definition.Tools[index].Description)}\n    {connector.ToolNames[index]}");
                review.AppendLine("    Input schema: " + JsonSerializer.Serialize(definition.Tools[index].InputSchema));
                review.AppendLine("    Output schema: " + JsonSerializer.Serialize(definition.Tools[index].OutputSchema));
            }
        }
        _connectors = connectors.ToArray();
        Tools = Array.AsReadOnly(_connectors.SelectMany(connector => connector.Tools).ToArray());
        ToolNames = Array.AsReadOnly(_connectors.SelectMany(connector => connector.ToolNames).ToArray());
        Review = review.ToString();
    }

    public ToolSelectionPolicy Grant(ToolSelectionPolicy policy) => policy with { AllowedMcpTools = ToolNames };
    public IReadOnlyCollection<string> RequireApprovals(IEnumerable<string>? otherTools) =>
        (otherTools ?? []).Concat(ToolNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Connector configuration contains duplicate property names.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Task.Run(async () =>
        {
            await Task.WhenAll(_connectors.Select(connector => connector.DisposeAsync().AsTask())).ConfigureAwait(false);
        }).GetAwaiter().GetResult();
    }
}
