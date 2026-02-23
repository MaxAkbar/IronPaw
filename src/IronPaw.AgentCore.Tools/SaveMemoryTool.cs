using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class SaveMemoryTool : ITool
{
    private readonly IMemoryStore _memoryStore;

    public SaveMemoryTool(IMemoryStore memoryStore)
    {
        _memoryStore = memoryStore;
        Descriptor = new ToolDescriptor(
            Name: "save_memory",
            Description:
                "Saves a persistent fact or preference to memory. " +
                "Global memories persist across all sessions. " +
                "Session memories persist only within the current session.",
            ParametersSchema: JsonSchemaBuilder.BuildObjectSchema(
            [
                new("content", "string", "The fact, preference, or note to remember.", Required: true),
                new("category", "string", "Optional category: preference, fact, context, task.", Required: false),
                new("scope", "string", "Memory scope: 'global' (default) or 'session'.", Required: false)
            ]),
            Group: "group:system",
            OwnerOnly: true);
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        if (!call.Arguments.TryGetProperty("content", out var contentEl) ||
            contentEl.ValueKind != JsonValueKind.String)
        {
            return ToolResult.Error("Missing required string argument: content");
        }

        var content = contentEl.GetString()!.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            return ToolResult.Error("content cannot be empty.");
        }

        var category = call.Arguments.TryGetProperty("category", out var catEl) &&
                       catEl.ValueKind == JsonValueKind.String
            ? catEl.GetString()
            : null;

        var scopeStr = call.Arguments.TryGetProperty("scope", out var scopeEl) &&
                       scopeEl.ValueKind == JsonValueKind.String
            ? scopeEl.GetString()
            : "global";

        var scope = scopeStr?.Equals("session", StringComparison.OrdinalIgnoreCase) == true
            ? MemoryScope.Session
            : MemoryScope.Global;

        var entry = new MemoryEntry(
            Id: Guid.NewGuid().ToString("n"),
            Content: content,
            Scope: scope,
            CreatedAt: DateTimeOffset.UtcNow,
            SessionId: scope == MemoryScope.Session ? context.SessionKey.Value : null,
            Category: category,
            Source: "agent");

        var saved = await _memoryStore.AddAsync(entry, ct);
        var payload = JsonSerializer.Serialize(new
        {
            success = true,
            memory_id = saved.Id,
            scope = scope.ToString().ToLowerInvariant()
        });

        return ToolResult.Success(payload);
    }
}
