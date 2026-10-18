using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

/// <summary>A filesystem-only, path-mediated task registry. No process or external capabilities are granted.</summary>
public static class ScopedTaskToolRegistry
{
    public static IToolRegistry Create(TaskWorkspace workspace, TaskResourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.NetworkHosts.IsDefault || scope.ReadPaths.IsDefault || scope.WritePaths.IsDefault)
            throw new ArgumentException("Task resource collections must be initialized.", nameof(scope));
        if (!scope.NetworkHosts.IsEmpty || scope.AllowProcessExecution || scope.AllowCredentialAccess || scope.AllowExternalMutations)
            throw new NotSupportedException("This task host supports scoped filesystem tools only.");
        TaskWorkspacePaths.NoLinks(workspace.WorkspacePath);
        var reads = scope.ReadPaths.Select(item => TaskWorkspacePaths.Relative(item)).ToArray();
        var writes = scope.WritePaths.Select(item => TaskWorkspacePaths.Relative(item)).ToArray();
        return new InMemoryToolRegistry([
            new ScopedFileTool(new ReadFileTool(workspace.WorkspacePath), workspace.WorkspacePath, reads),
            new ScopedFileTool(new WriteFileTool(workspace.WorkspacePath), workspace.WorkspacePath, writes),
            new ScopedListingTool(workspace.WorkspacePath, reads)
        ]);
    }

    private static bool Allows(string[] scopes, string path) => scopes.Any(scope => scope == "." ||
        path.Equals(scope, TaskWorkspacePaths.Comparison) || path.StartsWith(scope + "/", TaskWorkspacePaths.Comparison));

    private sealed class ScopedFileTool(ITool inner, string root, string[] scopes) : ITool
    {
        public ToolDescriptor Descriptor => inner.Descriptor;
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
        {
            if (!call.Arguments.TryGetProperty("path", out var value) || value.ValueKind != JsonValueKind.String)
                return Task.FromResult(ToolResult.Error("Missing required string argument: path"));
            string relative;
            try
            {
                relative = TaskWorkspacePaths.Relative(value.GetString()!);
                TaskWorkspacePaths.Resolve(root, relative);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
            { return Task.FromResult(ToolResult.Error("Task path denied: " + error.Message)); }
            if (!Allows(scopes, relative)) return Task.FromResult(ToolResult.Error("The task resource scope does not permit this path."));
            var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in call.Arguments.EnumerateObject()) arguments[property.Name] = property.Value;
            arguments["path"] = JsonSerializer.SerializeToElement(relative);
            return inner.ExecuteAsync(call with { Arguments = JsonSerializer.SerializeToElement(arguments) }, context, ct);
        }
    }

    private sealed class ScopedListingTool(string root, string[] scopes) : ITool
    {
        public ToolDescriptor Descriptor { get; } = new ListFilesTool(root).Descriptor;

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
        {
            string relative;
            string directory;
            try
            {
                relative = call.Arguments.TryGetProperty("path", out var value) && value.ValueKind == JsonValueKind.String
                    ? TaskWorkspacePaths.Relative(value.GetString()!) : ".";
                directory = TaskWorkspacePaths.Resolve(root, relative);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
            { return Task.FromResult(ToolResult.Error("Task path denied: " + error.Message)); }
            if (!Allows(scopes, relative) && !scopes.Any(scope => relative == "." || scope.StartsWith(relative + "/", TaskWorkspacePaths.Comparison)))
                return Task.FromResult(ToolResult.Error("The task resource scope does not permit this directory."));
            if (!Directory.Exists(directory)) return Task.FromResult(ToolResult.Error("The task directory does not exist."));
            var maximum = call.Arguments.TryGetProperty("max", out var limit) && limit.TryGetInt32(out var count) ? Math.Clamp(count, 1, 500) : 100;
            var files = new List<string>();
            foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                string item;
                try { item = TaskWorkspacePaths.Relative(Path.GetRelativePath(root, path)); TaskWorkspacePaths.NoLinks(path); }
                catch (InvalidOperationException) { continue; }
                if (Allows(scopes, item)) files.Add(item);
                if (files.Count == maximum) break;
            }
            return Task.FromResult(ToolResult.Success(JsonSerializer.Serialize(new { path = relative, files })));
        }
    }
}
