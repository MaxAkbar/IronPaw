namespace IronPaw.AgentCore.Tools;

internal static class WorkspacePathResolver
{
    public static string ResolveWithinRoot(string workspaceRoot, string relativeOrAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
        {
            throw new ArgumentException("Path cannot be empty.", nameof(relativeOrAbsolutePath));
        }

        var root = Path.GetFullPath(workspaceRoot);
        var candidate = Path.IsPathRooted(relativeOrAbsolutePath)
            ? Path.GetFullPath(relativeOrAbsolutePath)
            : Path.GetFullPath(Path.Combine(root, relativeOrAbsolutePath));

        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path '{relativeOrAbsolutePath}' escapes workspace root '{root}'.");
        }

        return candidate;
    }
}
