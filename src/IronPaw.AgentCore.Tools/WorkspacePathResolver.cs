namespace IronPaw.AgentCore.Tools;

internal static class WorkspacePathResolver
{
    public static string ResolveWithinRoot(string workspaceRoot, string relativeOrAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
        {
            throw new ArgumentException("Path cannot be empty.", nameof(relativeOrAbsolutePath));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var candidate = Path.IsPathRooted(relativeOrAbsolutePath)
            ? Path.GetFullPath(relativeOrAbsolutePath)
            : Path.GetFullPath(Path.Combine(root, relativeOrAbsolutePath));

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, comparison) && !candidate.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException($"Path '{relativeOrAbsolutePath}' escapes workspace root '{root}'.");
        }

        // A lexically contained path can still escape through a junction or symbolic link.
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".") continue;
            current = Path.Combine(current, component);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException($"Workspace paths cannot traverse symbolic links or junctions: '{current}'.");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        return candidate;
    }
}
