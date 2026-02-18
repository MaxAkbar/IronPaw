using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class ListFilesTool : ITool
{
    private readonly string _workspaceRoot;

    public ListFilesTool(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
        Descriptor = new ToolDescriptor(
            Name: "list_files",
            Description: "Lists files in a workspace-relative directory.",
            ParametersSchema: JsonSchemaBuilder.BuildObjectSchema(
                [
                    new("path", "string", "Workspace-relative directory path.", false),
                    new("max", "integer", "Maximum number of files to return.", false)
                ]),
            Group: "group:fs");
    }

    public ToolDescriptor Descriptor { get; }

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        var path = ".";
        var max = 100;

        if (call.Arguments.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String)
        {
            path = pathElement.GetString() ?? ".";
        }

        if (call.Arguments.TryGetProperty("max", out var maxElement) && maxElement.ValueKind is JsonValueKind.Number)
        {
            max = Math.Clamp(maxElement.GetInt32(), 1, 500);
        }

        var resolvedPath = WorkspacePathResolver.ResolveWithinRoot(_workspaceRoot, path);
        if (!Directory.Exists(resolvedPath))
        {
            return Task.FromResult(ToolResult.Error($"Directory does not exist: {resolvedPath}"));
        }

        var files = Directory.EnumerateFiles(resolvedPath, "*", SearchOption.TopDirectoryOnly)
            .Take(max)
            .Select(file => Path.GetRelativePath(_workspaceRoot, file))
            .ToArray();

        var content = JsonSerializer.Serialize(new { path = path, files });
        return Task.FromResult(ToolResult.Success(content));
    }
}
