using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

/// <summary>Commit snapshots and host-owned artifact revisions. Does not launch task processes.</summary>
public sealed partial class GitTaskWorkspaceManager : ITaskWorkspaceManager
{
    public async Task<string> ResolveCommitAsync(string sourceWorkspaceRoot, string revision, CancellationToken ct)
    {
        var source = TaskWorkspacePaths.Root(sourceWorkspaceRoot);
        TaskWorkspacePaths.NoLinks(source);
        TaskWorkspacePaths.NoLinks(Path.Combine(source, ".git"));
        if (string.IsNullOrWhiteSpace(revision)) throw new ArgumentException("A base revision is required.", nameof(revision));
        var top = (await GitTextAsync(source, ["rev-parse", "--show-toplevel"], ct)).Trim();
        if (!TaskWorkspacePaths.Same(source, Path.GetFullPath(top)))
            throw new InvalidOperationException("The source workspace must be the repository root.");
        var commit = (await GitTextAsync(source, ["rev-parse", "--verify", "--end-of-options", revision + "^{commit}"], ct)).Trim();
        if (!IsCommit(commit)) throw new InvalidOperationException("Git did not resolve an exact commit identity.");
        return commit.ToLowerInvariant();
    }

    public async Task<TaskWorkspace> OpenOrCreateAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct)
    {
        if (binding.DependencyMode == TaskDependencyMode.AcceptedChanges)
            throw new TaskInputConflictException("Accepted-change attempts require pinned dependency preparation.");
        ct.ThrowIfCancellationRequested();
        var workspace = Describe(binding, claim);
        var source = TaskWorkspacePaths.Root(binding.SourceWorkspaceRoot);
        var store = Path.GetDirectoryName(workspace.AttemptPath)!;
        Directory.CreateDirectory(store);
        TaskWorkspacePaths.NoLinks(store);
        using var owner = OpenLock(workspace.AttemptPath + ".lock");
        var marker = Path.Combine(workspace.AttemptPath, "workspace.json");
        if (Directory.Exists(workspace.AttemptPath))
        {
            ValidateWorkspace(workspace);
            return workspace;
        }
        Directory.CreateDirectory(workspace.AttemptPath);
        Directory.CreateDirectory(workspace.WorkspacePath);
        await ArchiveAsync(source, binding.BaseCommit, workspace.WorkspacePath, ct);
        await ValidateArchiveAsync(source, binding.BaseCommit, workspace.WorkspacePath, ct);
        TaskWorkspacePaths.NoLinks(workspace.WorkspacePath);
        await WriteNewDurableAsync(marker, JsonSerializer.SerializeToUtf8Bytes(workspace), ct);
        return workspace;
    }

    public Task<TaskWorkspace> OpenExistingAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var workspace = Describe(binding, claim);
        ValidateWorkspace(workspace);
        return Task.FromResult(workspace);
    }

    private static TaskWorkspace Describe(TaskGraphAgentBinding binding, TaskGraphClaim claim)
    {
        if (!IsCommit(binding.BaseCommit)) throw new ArgumentException("The accepted binding must contain an exact commit identity.");
        var source = TaskWorkspacePaths.Root(binding.SourceWorkspaceRoot);
        var store = TaskWorkspacePaths.Root(binding.WorkspaceStoreRoot);
        TaskWorkspacePaths.NoLinks(source);
        TaskWorkspacePaths.NoLinks(store);
        if (TaskWorkspacePaths.Within(source, store) || TaskWorkspacePaths.Within(store, source))
            throw new InvalidOperationException("The task workspace store and source repository must not overlap.");
        var identity = Digest(JsonSerializer.SerializeToUtf8Bytes(new[] { claim.GraphFingerprint, claim.Task.TaskId, claim.AttemptId }));
        var attempt = Path.Combine(store, identity);
        return new TaskWorkspace(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId, claim.AttemptId,
            binding.BaseCommit.ToLowerInvariant(), Path.Combine(attempt, "workspace"), attempt)
        { InputDigest = binding.DependencyMode == TaskDependencyMode.AcceptedChanges ? RequireInputs(binding, claim).Digest : null };
    }

    public async Task<TaskWorkspaceCapture> CaptureAsync(TaskWorkspace workspace,
        ImmutableArray<TaskArtifactRequirement> requirements, CancellationToken ct)
    {
        ValidateWorkspace(workspace);
        if (requirements.IsDefault) throw new ArgumentException("Artifact requirements must be initialized.", nameof(requirements));
        foreach (var requirement in requirements)
        {
            if (requirement is null) throw new ArgumentException("Artifact requirements cannot contain null.");
            if (requirement.RelativePath is { } relative) TaskWorkspacePaths.Relative(relative, allowRoot: false);
        }
        using var owner = OpenLock(Path.Combine(workspace.AttemptPath, "capture.lock"));
        var entries = await ScanAsync(workspace.WorkspacePath, ct);
        var manifest = await CreateRevisionManifestAsync(workspace, requirements, entries, ct);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var revision = "sha256:" + Digest(bytes);
        var revisionRoot = RevisionRoot(workspace, revision);
        var files = Path.Combine(revisionRoot, "files");
        if (Directory.Exists(revisionRoot))
        {
            if (!await VerifyManifestAsync(workspace, revision, ct))
                throw new InvalidOperationException("An existing artifact revision is incomplete or has changed.");
        }
        else
        {
            Directory.CreateDirectory(files);
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var target = TaskWorkspacePaths.Resolve(files, entry.Path);
                if (entry.IsDirectory) Directory.CreateDirectory(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var source = TaskWorkspacePaths.Resolve(workspace.WorkspacePath, entry.Path);
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, ct);
                    await output.FlushAsync(ct);
                    output.Flush(flushToDisk: true);
                }
            }
            if (!Equal(entries, await ScanAsync(files, ct)) || !Equal(entries, await ScanAsync(workspace.WorkspacePath, ct)))
                throw new InvalidOperationException("Workspace content changed while capturing its revision.");
            await WriteNewDurableAsync(Path.Combine(revisionRoot, "manifest.json"), bytes, ct);
        }
        return Artifacts(manifest, revision);
    }

    public async Task<bool> VerifyAsync(TaskWorkspace workspace, WorkflowTaskResult result, CancellationToken ct)
    {
        try
        {
            ValidateWorkspace(workspace);
            if (result.GraphId != workspace.GraphId || result.GraphFingerprint != workspace.GraphFingerprint ||
                result.TaskId != workspace.TaskId || !await VerifyManifestAsync(workspace, result.Revision, ct)) return false;
            var manifest = await ReadManifestAsync(workspace, result.Revision, ct);
            var expected = Artifacts(manifest, result.Revision);
            if (!expected.MissingRequiredArtifacts.IsEmpty || result.Artifacts.IsDefault || result.Artifacts.Any(item => item is null) || expected.Artifacts.Length != result.Artifacts.Length)
                return false;
            var actual = result.Artifacts.ToDictionary(item => item.ArtifactId, StringComparer.Ordinal);
            return expected.Artifacts.All(item => actual.TryGetValue(item.ArtifactId, out var artifact) && artifact == item);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        { return false; }
    }

    private static void ValidateWorkspace(TaskWorkspace workspace)
    {
        TaskWorkspacePaths.NoLinks(workspace.AttemptPath);
        TaskWorkspacePaths.NoLinks(workspace.WorkspacePath);
        if (!TaskWorkspacePaths.Same(workspace.WorkspacePath, Path.Combine(workspace.AttemptPath, "workspace")))
            throw new InvalidOperationException("The task workspace path does not match its attempt.");
        var marker = Path.Combine(workspace.AttemptPath, "workspace.json");
        TaskWorkspacePaths.NoLinks(marker);
        if (!File.Exists(marker) || JsonSerializer.Deserialize<TaskWorkspace>(File.ReadAllText(marker)) != workspace || !Directory.Exists(workspace.WorkspacePath))
            throw new InvalidOperationException("Task workspace initialization is incomplete or its identity has changed; preserve it for reconciliation.");
        if (workspace.InputDigest is not null)
        {
            var inputsPath = Path.Combine(workspace.AttemptPath, "inputs.json");
            TaskWorkspacePaths.NoLinks(inputsPath);
            var inputBytes = File.ReadAllBytes(inputsPath);
            ValidateInitialDigest(workspace, inputBytes);
            var initial = JsonSerializer.Deserialize<InitialManifest>(inputBytes, InputJson)
                ?? throw new TaskInputConflictException("The frozen task baseline is missing.");
            ValidateInitial(workspace, initial);
        }
    }

    private static async Task<bool> VerifyManifestAsync(TaskWorkspace workspace, string revision, CancellationToken ct)
    {
        var root = RevisionRoot(workspace, revision);
        TaskWorkspacePaths.NoLinks(root);
        var path = Path.Combine(root, "manifest.json");
        TaskWorkspacePaths.NoLinks(path);
        if (!File.Exists(path)) return false;
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (revision != "sha256:" + Digest(bytes)) return false;
        var manifest = JsonSerializer.Deserialize<RevisionManifest>(bytes);
        if (manifest is null || manifest.SchemaVersion is not (1 or 2) || manifest.Workspace != workspace || manifest.Entries.IsDefault || manifest.Requirements.IsDefault)
            return false;
        if (Directory.EnumerateFileSystemEntries(root).Any(item => Path.GetFileName(item) is not "files" and not "manifest.json")) return false;
        return Equal(manifest.Entries, await ScanAsync(Path.Combine(root, "files"), ct)) &&
            await VerifyRevisionInputsAsync(manifest, ct);
    }

    private static async Task<RevisionManifest> ReadManifestAsync(TaskWorkspace workspace, string revision, CancellationToken ct) =>
        JsonSerializer.Deserialize<RevisionManifest>(await File.ReadAllBytesAsync(Path.Combine(RevisionRoot(workspace, revision), "manifest.json"), ct))!;

    private static TaskWorkspaceCapture Artifacts(RevisionManifest manifest, string revision)
    {
        var artifacts = ImmutableArray.CreateBuilder<WorkflowTaskArtifact>();
        var missing = ImmutableArray.CreateBuilder<string>();
        var files = Path.Combine(RevisionRoot(manifest.Workspace, revision), "files");
        foreach (var requirement in manifest.Requirements)
        {
            var relative = requirement.RelativePath is null ? null : TaskWorkspacePaths.Relative(requirement.RelativePath, allowRoot: false);
            var entry = relative is null ? null : manifest.Entries.FirstOrDefault(item => item.Path.Equals(relative, TaskWorkspacePaths.Comparison));
            if (entry is null || entry.IsDirectory != (requirement.Kind == TaskArtifactKind.Directory))
            {
                if (requirement.Required) missing.Add(requirement.ArtifactId);
                continue;
            }
            var digest = entry.IsDirectory
                ? "sha256:" + Digest(JsonSerializer.SerializeToUtf8Bytes(manifest.Entries.Where(item => item.Path.StartsWith(entry.Path + "/", StringComparison.Ordinal)).ToArray()))
                : entry.ContentDigest!;
            artifacts.Add(new(requirement.ArtifactId, manifest.Workspace.TaskId, requirement.Kind,
                TaskWorkspacePaths.Resolve(files, entry.Path), digest, revision));
        }
        return new(revision, artifacts.ToImmutable(), missing.ToImmutable());
    }

    private static async Task<ImmutableArray<ManifestEntry>> ScanAsync(string root, CancellationToken ct)
    {
        TaskWorkspacePaths.NoLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The workspace revision directory is missing.");
        var result = new List<ManifestEntry>();
        var pending = new Queue<string>();
        var casePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(root);
        while (pending.TryDequeue(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                var relative = TaskWorkspacePaths.Relative(Path.GetRelativePath(root, path), allowRoot: false);
                CheckCollision(relative, casePaths);
                TaskWorkspacePaths.NoLinks(path);
                var isDirectory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
                if (isDirectory) { result.Add(new(relative, true, null)); pending.Enqueue(path); }
                else
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    result.Add(new(relative, false, "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct))));
                }
            }
        }
        return result.OrderBy(item => item.Path, StringComparer.Ordinal).ToImmutableArray();
    }

    private static async Task ArchiveAsync(string source, string commit, string target, CancellationToken ct)
    {
        TaskWorkspacePaths.NoLinks(Path.Combine(source, ".git"));
        // Read raw objects, never a checkout/archive conversion: repository attributes can
        // otherwise execute smudge/process filters or change the accepted commit's bytes.
        var entries = await SnapshotTreeAsync(source, commit, ct);
        foreach (var entry in entries.Where(item => item.IsDirectory))
            Directory.CreateDirectory(TaskWorkspacePaths.Resolve(target, entry.Path));
        using var process = StartGit(source, ["cat-file", "--batch"], redirectInput: true);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var cancelled = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        try
        {
            await using var input = new BufferedStream(process.StandardOutput.BaseStream, 81920);
            var buffer = new byte[81920];
            foreach (var entry in entries.Where(item => !item.IsDirectory))
            {
                await process.StandardInput.WriteAsync((entry.ObjectId + "\n").AsMemory(), ct);
                await process.StandardInput.FlushAsync(ct);
                var header = await ReadObjectHeaderAsync(input, ct);
                var fields = header.Split(' ');
                if (fields.Length != 3 || fields[0] != entry.ObjectId || fields[1] != "blob" ||
                    !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                    throw new InvalidOperationException("Git returned an invalid or unavailable snapshot blob.");
                var path = TaskWorkspacePaths.Resolve(target, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    while (length > 0)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), ct);
                        if (read == 0) throw new InvalidOperationException("Git returned a truncated snapshot blob.");
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        length -= read;
                    }
                    await output.FlushAsync(ct);
                    output.Flush(flushToDisk: true);
                }
                if (await input.ReadAsync(buffer.AsMemory(0, 1), ct) != 1 || buffer[0] != '\n')
                    throw new InvalidOperationException("Git returned an invalid snapshot object terminator.");
            }
            process.StandardInput.Close();
            if (await input.ReadAsync(buffer.AsMemory(0, 1), ct) != 0)
                throw new InvalidOperationException("Git returned unexpected snapshot data.");
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new InvalidOperationException("Git object extraction failed: " + await error);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<string> ReadObjectHeaderAsync(Stream input, CancellationToken ct)
    {
        var header = new byte[256];
        for (var index = 0; index < header.Length; index++)
        {
            if (await input.ReadAsync(header.AsMemory(index, 1), ct) != 1)
                throw new InvalidOperationException("Git returned an incomplete snapshot object header.");
            if (header[index] == '\n') return Encoding.ASCII.GetString(header, 0, index);
            if (header[index] is < 32 or > 126)
                throw new InvalidOperationException("Git returned a malformed snapshot object header.");
        }
        throw new InvalidOperationException("Git returned an oversized snapshot object header.");
    }

    private static async Task<ImmutableArray<SnapshotEntry>> SnapshotTreeAsync(string source, string commit, CancellationToken ct)
    {
        var tree = await GitTextAsync(source, ["ls-tree", "-r", "-t", "-z", "--full-tree", commit], ct);
        var entries = ImmutableArray.CreateBuilder<SnapshotEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in tree.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf('\t');
            if (separator < 0) throw new InvalidOperationException("Git returned an invalid tree entry.");
            var fields = item[..separator].Split(' ');
            var rawPath = item[(separator + 1)..];
            var relative = TaskWorkspacePaths.Relative(rawPath, allowRoot: false);
            CheckCollision(relative, paths);
            if (relative != rawPath || !seen.Add(relative) || fields.Length != 3 || !IsCommit(fields[2]) || fields[2].Length != commit.Length ||
                !(fields[0] == "040000" && fields[1] == "tree" || fields[0] is "100644" or "100755" && fields[1] == "blob"))
                throw new InvalidOperationException("Git snapshots require portable regular files and directories without links, submodules or collisions.");
            entries.Add(new(relative, fields[1] == "tree", fields[2]));
        }
        return entries.ToImmutable();
    }

    private static async Task ValidateArchiveAsync(string source, string commit, string target, CancellationToken ct)
    {
        // Independently hash extracted files using Git's object framing, and check the
        // complete path set against the exact commit before admitting model execution.
        var tree = await GitTextAsync(source, ["ls-tree", "-r", "-t", "-z", "--full-tree", commit], ct);
        var expected = new HashSet<string>(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in tree.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf('\t');
            if (separator < 0) throw new InvalidOperationException("Git returned an invalid tree entry.");
            var fields = item[..separator].Split(' ');
            var relative = TaskWorkspacePaths.Relative(item[(separator + 1)..], allowRoot: false);
            CheckCollision(relative, paths);
            if (!expected.Add(relative) || fields.Length != 3 || fields[1] is not "tree" and not "blob")
                throw new InvalidOperationException("Git snapshots cannot contain submodules or colliding entries.");
            var path = TaskWorkspacePaths.Resolve(target, relative);
            if (fields[1] == "tree")
            {
                if (!Directory.Exists(path)) throw new InvalidOperationException("Git snapshot omitted an accepted directory.");
                continue;
            }
            if (fields[0] is not "100644" and not "100755" || !File.Exists(path))
                throw new InvalidOperationException("Git snapshot omitted an accepted file or contains a nonregular entry.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hash = IncrementalHash.CreateHash(fields[2].Length == 64 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
            hash.AppendData(Encoding.UTF8.GetBytes("blob " + stream.Length + "\0"));
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) != 0) hash.AppendData(buffer, 0, read);
            if (!fields[2].Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), StringComparison.Ordinal))
                throw new InvalidOperationException("Git snapshot changed accepted file content.");
        }
        if (!expected.SetEquals((await ScanAsync(target, ct)).Select(item => item.Path)))
            throw new InvalidOperationException("Git snapshot content differs from the accepted commit.");
    }

    private static void CheckCollision(string path, Dictionary<string, string> paths)
    {
        var prefix = "";
        foreach (var segment in path.Split('/'))
        {
            prefix = prefix.Length == 0 ? segment : prefix + "/" + segment;
            if (paths.TryGetValue(prefix, out var previous) && previous != prefix)
                throw new InvalidOperationException("Workspace paths collide when compared without case.");
            paths[prefix] = prefix;
        }
    }

    private static string RevisionRoot(TaskWorkspace workspace, string revision)
    {
        if (string.IsNullOrEmpty(revision) || !revision.StartsWith("sha256:", StringComparison.Ordinal) || revision.Length != 71 || !revision.AsSpan(7).ToString().All(Uri.IsHexDigit))
            throw new ArgumentException("Artifact revisions must be SHA256 identities.", nameof(revision));
        return Path.Combine(workspace.AttemptPath, "revisions", revision[7..]);
    }

    private static bool Equal(ImmutableArray<ManifestEntry> left, ImmutableArray<ManifestEntry> right) => left.SequenceEqual(right);
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool IsCommit(string commit) => commit.Length is 40 or 64 && commit.All(Uri.IsHexDigit);
    private static async Task WriteNewDurableAsync(string path, byte[] bytes, CancellationToken ct)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(bytes, ct);
        await output.FlushAsync(ct);
        output.Flush(flushToDisk: true);
    }

    private static FileStream OpenLock(string path)
    {
        TaskWorkspacePaths.NoLinks(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static Process StartGit(string source, IEnumerable<string> arguments, bool redirectInput = false)
    {
        var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = redirectInput, StandardOutputEncoding = new UTF8Encoding(false, true) };
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["GIT_NO_LAZY_FETCH"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.ArgumentList.Add("--no-replace-objects");
        info.ArgumentList.Add("-C"); info.ArgumentList.Add(source);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("Git could not be started.");
    }

    private static async Task<string> GitTextAsync(string source, IEnumerable<string> arguments, CancellationToken ct)
    {
        using var process = StartGit(source, arguments);
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var cancelled = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException("Git revision resolution failed: " + await error);
        return await output;
    }

    private sealed record ManifestEntry(string Path, bool IsDirectory, string? ContentDigest);
    private sealed record SnapshotEntry(string Path, bool IsDirectory, string ObjectId);
    private sealed record RevisionManifest(int SchemaVersion, TaskWorkspace Workspace,
        ImmutableArray<TaskArtifactRequirement> Requirements, ImmutableArray<ManifestEntry> Entries,
        string? InputManifestDigest = null, ImmutableArray<FileChange> Changes = default);
}

internal static class TaskWorkspacePaths
{
    internal static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static string Root(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    internal static bool Same(string left, string right) => string.Equals(Root(left), Root(right), Comparison);
    internal static bool Within(string root, string path) => Same(root, path) || Root(path).StartsWith(Path.EndsInDirectorySeparator(Root(root)) ? Root(root) : Root(root) + Path.DirectorySeparatorChar, Comparison);

    internal static string Relative(string path, bool allowRoot = true)
    {
        if (allowRoot && path == ".") return ".";
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || Path.IsPathRooted(path) || path[0] is '/' or '\\' || path.Contains(':'))
            throw new InvalidOperationException("Task paths must be safe workspace-relative paths.");
        var segments = path.Replace('\\', '/').Split('/');
        foreach (var segment in segments)
        {
            var stem = segment.Split('.')[0];
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') || segment.Contains('*') || segment.Contains('?') ||
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase) || segment.IndexOfAny(['<', '>', '|', '"']) >= 0 ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9')
                throw new InvalidOperationException("Task paths cannot contain traversal, repository metadata, or nonportable segments.");
        }
        return string.Join('/', segments);
    }

    internal static string Resolve(string root, string relative)
    {
        relative = Relative(relative);
        NoLinks(root);
        var result = relative == "." ? Root(root) : Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Within(root, result)) throw new InvalidOperationException("Task path escaped its workspace.");
        NoLinks(result);
        return result;
    }

    internal static void NoLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var component in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Task workspace paths cannot traverse symbolic links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
