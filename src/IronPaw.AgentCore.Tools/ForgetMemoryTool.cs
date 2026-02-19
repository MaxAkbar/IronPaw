using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class ForgetMemoryTool : ITool
{
    private readonly IMemoryStore _memoryStore;

    public ForgetMemoryTool(IMemoryStore memoryStore)
    {
        _memoryStore = memoryStore;
        Descriptor = new ToolDescriptor(
            Name: "forget_memory",
            Description: "Removes a previously saved memory entry by its ID.",
            ParametersSchema: JsonSchemaBuilder.BuildObjectSchema(
            [
                new("memory_id", "string", "The ID of the memory entry to remove.", Required: true)
            ]),
            Group: "group:system");
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        if (!call.Arguments.TryGetProperty("memory_id", out var idEl) ||
            idEl.ValueKind != JsonValueKind.String)
        {
            return ToolResult.Error("Missing required string argument: memory_id");
        }

        var memoryId = idEl.GetString()!.Trim();
        if (string.IsNullOrWhiteSpace(memoryId))
        {
            return ToolResult.Error("memory_id cannot be empty.");
        }

        var removed = await _memoryStore.RemoveAsync(memoryId, ct);
        var payload = JsonSerializer.Serialize(new
        {
            success = removed,
            memory_id = memoryId
        });

        return removed
            ? ToolResult.Success(payload)
            : ToolResult.Error($"Memory entry '{memoryId}' was not found.");
    }
}
