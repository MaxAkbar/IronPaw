using System.Text.Json;

namespace IronPaw.AgentCore.Mcp;

/// <summary>A host-reviewed catalog, never populated automatically from a server's discovery response.</summary>
public sealed record McpToolDefinition(string Name, string Description, JsonElement InputSchema,
    JsonElement? OutputSchema = null);

public sealed record McpConnectorOptions(
    string Id,
    string BindingRevision,
    IReadOnlyList<McpToolDefinition> Tools,
    int TimeoutSeconds = 30,
    int MaxArgumentBytes = 65_536,
    int MaxResultCharacters = 16_384);
