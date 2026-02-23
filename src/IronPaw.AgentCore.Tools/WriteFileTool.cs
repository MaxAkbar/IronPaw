using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class WriteFileTool : ITool
{
    private readonly string _workspaceRoot;

    public WriteFileTool(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
        Descriptor = new ToolDescriptor(
            Name: "write_file",
            Description: "Writes UTF-8 text content to a workspace file path.",
            ParametersSchema: JsonSchemaBuilder.BuildObjectSchema(
                [
                    new("path", "string", "Relative path from workspace root.", true),
                    new("content", "string", "UTF-8 content to write.", true)
                ]),
            Group: "group:fs",
            OwnerOnly: true);
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        if (!call.Arguments.TryGetProperty("path", out var pathElement) ||
            pathElement.ValueKind != JsonValueKind.String)
        {
            return ToolResult.Error("Missing required string argument: path");
        }

        if (!call.Arguments.TryGetProperty("content", out var contentElement) ||
            contentElement.ValueKind != JsonValueKind.String)
        {
            return ToolResult.Error("Missing required string argument: content");
        }

        var resolvedPath = WorkspacePathResolver.ResolveWithinRoot(_workspaceRoot, pathElement.GetString()!);
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedPath)!);
        await File.WriteAllTextAsync(resolvedPath, contentElement.GetString()!, ct);

        return ToolResult.Success(
            $"Wrote file: {resolvedPath}",
            new Dictionary<string, object?>
            {
                ["path"] = resolvedPath,
                ["bytes"] = new FileInfo(resolvedPath).Length
            });
    }
}
