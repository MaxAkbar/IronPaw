using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class CurrentTimeTool : ITool
{
    public CurrentTimeTool()
    {
        Descriptor = new ToolDescriptor(
            Name: "current_time",
            Description: "Returns the current local and UTC timestamps.",
            ParametersSchema: JsonSchemaBuilder.BuildObjectSchema(Array.Empty<JsonSchemaBuilder.SchemaProperty>()),
            Group: "group:system");
    }

    public ToolDescriptor Descriptor { get; }

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        var local = DateTimeOffset.Now;
        var utc = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(
            new
            {
                local = local.ToString("O"),
                utc = utc.ToString("O")
            });

        return Task.FromResult(ToolResult.Success(payload));
    }
}
