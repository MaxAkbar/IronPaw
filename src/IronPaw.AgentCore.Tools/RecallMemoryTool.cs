using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class RecallMemoryTool : ITool
{
    private readonly IMemoryStore _memoryStore;

    public RecallMemoryTool(IMemoryStore memoryStore)
    {
        _memoryStore = memoryStore;
        Descriptor = new ToolDescriptor(
            Name: "recall_memory",
            Description: "Searches persistent memories by keyword. Returns matching entries.",
            ParametersSchema: JsonSchemaBuilder.BuildObjectSchema(
            [
                new("query", "string", "The keyword or phrase to search for in memories.", Required: true),
                new("scope", "string", "Filter by scope: 'global', 'session', or omit for both.", Required: false)
            ]),
            Group: "group:system");
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        if (!call.Arguments.TryGetProperty("query", out var queryEl) ||
            queryEl.ValueKind != JsonValueKind.String)
        {
            return ToolResult.Error("Missing required string argument: query");
        }

        var query = queryEl.GetString()!.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return ToolResult.Error("query cannot be empty.");
        }

        MemoryScope? scope = null;
        if (call.Arguments.TryGetProperty("scope", out var scopeEl) &&
            scopeEl.ValueKind == JsonValueKind.String)
        {
            var scopeStr = scopeEl.GetString();
            if (scopeStr?.Equals("global", StringComparison.OrdinalIgnoreCase) == true)
            {
                scope = MemoryScope.Global;
            }
            else if (scopeStr?.Equals("session", StringComparison.OrdinalIgnoreCase) == true)
            {
                scope = MemoryScope.Session;
            }
        }

        var results = await _memoryStore.SearchAsync(query, scope, context.SessionKey, ct);

        var payload = JsonSerializer.Serialize(results.Select(m => new
        {
            id = m.Id,
            content = m.Content,
            scope = m.Scope.ToString().ToLowerInvariant(),
            category = m.Category,
            source = m.Source,
            created_at = m.CreatedAt.ToString("O")
        }));

        return ToolResult.Success(payload);
    }
}
