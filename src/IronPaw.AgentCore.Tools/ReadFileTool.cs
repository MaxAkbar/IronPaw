using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class ReadFileTool : ITool
{
    private readonly string _workspaceRoot;
    private readonly int _maxChars;

    public ReadFileTool(string workspaceRoot, int maxChars = 16_384)
    {
        _workspaceRoot = workspaceRoot;
        _maxChars = maxChars;
        Descriptor = new ToolDescriptor(
            Name: "read_file",
            Description: "Reads UTF-8 text from a workspace file path.",
            ParametersSchema: JsonSchemaBuilder.BuildObjectSchema(
                [
                    new("path", "string", "Relative path from workspace root.", true)
                ]),
            Group: "group:fs");
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        if (!call.Arguments.TryGetProperty("path", out var pathElement) ||
            pathElement.ValueKind != JsonValueKind.String)
        {
            return ToolResult.Error("Missing required string argument: path");
        }

        var resolvedPath = WorkspacePathResolver.ResolveWithinRoot(_workspaceRoot, pathElement.GetString()!);
        if (!File.Exists(resolvedPath))
        {
            return ToolResult.Error($"File does not exist: {resolvedPath}");
        }

        var content = await File.ReadAllTextAsync(resolvedPath, ct);
        if (content.Length > _maxChars)
        {
            content = content[.._maxChars];
        }

        return ToolResult.Success(
            content,
            new Dictionary<string, object?>
            {
                ["path"] = resolvedPath,
                ["truncated"] = content.Length >= _maxChars
            });
    }
}
