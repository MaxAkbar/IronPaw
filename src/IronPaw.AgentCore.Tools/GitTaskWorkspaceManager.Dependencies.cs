using System.Collections.Immutable;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed partial class GitTaskWorkspaceManager
{
    private static readonly JsonSerializerOptions InputJson = new() { MaxDepth = 256 };

    public async Task ValidateWorkingStateAsync(TaskWorkspace workspace, ImmutableArray<WorkflowToolExecution> tools,
        CancellationToken ct)
    {
        try
        {
            ValidateWorkspace(workspace);
            // Existing base-only attempts retain their original recovery contract.
            if (workspace.InputDigest is null) return;
            if (tools.IsDefault) throw new TaskInputConflictException("Saved task tool receipts are uninitialized.");
            var initial = await ReadInitialAsync(workspace, ct);
            var files = initial.Entries.Where(entry => !entry.IsDirectory)
                .ToDictionary(entry => entry.Path, entry => entry.ContentDigest!, StringComparer.Ordinal);
            var executionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in tools)
            {
                ct.ThrowIfCancellationRequested();
                if (tool is null || tool.Call is null || string.IsNullOrWhiteSpace(tool.ExecutionId) || !executionIds.Add(tool.ExecutionId))
                    throw new TaskInputConflictException("Saved task tool receipts have invalid execution identities.");
                if (tool.State == WorkflowToolState.Planned && tool.Result is null) continue;
                if (tool.State != WorkflowToolState.Completed || tool.Result is null)
                    throw new TaskInputConflictException("A task tool effect has no confirmed completed receipt.");
                if (tool.Result.IsError) continue;
                var name = tool.Call.ToolName;
                if (string.Equals(name, "read_file", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "list_files", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(name, "write_file", StringComparison.OrdinalIgnoreCase))
                    throw new TaskInputConflictException("A successful tool receipt is outside the audited file-tool boundary.");
                var arguments = tool.Call.Arguments;
                if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("path", out var pathValue) ||
                    pathValue.ValueKind != JsonValueKind.String || !arguments.TryGetProperty("content", out var contentValue) ||
                    contentValue.ValueKind != JsonValueKind.String)
                    throw new TaskInputConflictException("A successful file write has no exact path and content receipt.");
                var path = NormalizeReceiptPath(files.Keys,
                    TaskWorkspacePaths.Relative(pathValue.GetString()!, allowRoot: false));
                var digest = "sha256:" + Digest(System.Text.Encoding.UTF8.GetBytes(contentValue.GetString()!));
                RequireWriteScope(initial.Task.Resources.WritePaths, [new(path, null, digest)]);
                files[path] = digest;
            }
            ValidateComposedPaths(files.Keys);
            var expected = files.Select(item => new ManifestEntry(item.Key, false, item.Value)).ToList();
            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in files.Keys)
            {
                var parent = path;
                while (parent.LastIndexOf('/') is var separator && separator >= 0)
                {
                    parent = parent[..separator];
                    directories.Add(parent);
                }
            }
            expected.AddRange(directories.Select(path => new ManifestEntry(path, true, null)));
            if (!expected.OrderBy(entry => entry.Path, StringComparer.Ordinal).SequenceEqual(await ScanAsync(workspace.WorkspacePath, ct)))
                throw new TaskInputConflictException("Task workspace bytes differ from frozen inputs and confirmed file-tool receipts.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException ||
            error is InvalidOperationException and not TaskInputConflictException)
        { throw new TaskInputConflictException("The task workspace cannot be validated before execution: " + error.Message); }
    }

    private static string NormalizeReceiptPath(IEnumerable<string> files, string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        var casing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var prefix = "";
            foreach (var segment in file.Split('/'))
            {
                prefix = prefix.Length == 0 ? segment : prefix + "/" + segment;
                casing.TryAdd(prefix, prefix);
            }
        }
        var actual = "";
        foreach (var segment in path.Split('/'))
        {
            var candidate = actual.Length == 0 ? segment : actual + "/" + segment;
            actual = casing.TryGetValue(candidate, out var established) ? established : candidate;
        }
        return actual;
    }

    public async Task<TaskWorkspace> PrepareAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim,
        ImmutableArray<TaskDependencyInput> dependencies, CancellationToken ct)
    {
        if (binding.DependencyMode != TaskDependencyMode.AcceptedChanges)
        {
            if (!dependencies.IsDefaultOrEmpty) throw new TaskInputConflictException("Legacy attempts cannot import predecessor changes.");
            return await OpenOrCreateAsync(binding, claim, ct);
        }
        try
        {
            var inputs = RequireInputs(binding, claim);
            if (dependencies.IsDefault || dependencies.Length != inputs.Dependencies.Length)
                throw new TaskInputConflictException("Every pinned predecessor must have one sealed input.");
            var prepared = new List<DependencyChanges>();
            for (var index = 0; index < dependencies.Length; index++)
                prepared.Add(await ReadDependencyAsync(binding, claim, inputs.Dependencies[index], dependencies[index], ct));

            var workspace = Describe(binding, claim);
            var store = Path.GetDirectoryName(workspace.AttemptPath)!;
            Directory.CreateDirectory(store);
            TaskWorkspacePaths.NoLinks(store);
            using var owner = OpenLock(workspace.AttemptPath + ".lock");
            if (Directory.Exists(workspace.AttemptPath))
            {
                ValidateWorkspace(workspace);
                var initialized = await ReadInitialAsync(workspace, ct);
                if (!SameJson(initialized.Inputs, inputs) || !SameJson(initialized.Task, claim.Task))
                    throw new TaskInputConflictException("An existing attempt has different frozen inputs or permissions.");
                return workspace;
            }
            Directory.CreateDirectory(workspace.AttemptPath);
            Directory.CreateDirectory(workspace.WorkspacePath);
            await ArchiveAsync(binding.SourceWorkspaceRoot, binding.BaseCommit, workspace.WorkspacePath, ct);
            await ValidateArchiveAsync(binding.SourceWorkspaceRoot, binding.BaseCommit, workspace.WorkspacePath, ct);
            var baseEntries = await ScanAsync(workspace.WorkspacePath, ct);
            var composed = baseEntries.Where(entry => !entry.IsDirectory)
                .ToDictionary(entry => entry.Path, entry => new ComposedFile(entry.ContentDigest!, null), StringComparer.Ordinal);

            // Plan all changes before modifying the extracted base. A failed merge never grants tools.
            foreach (var predecessor in prepared)
                foreach (var change in predecessor.Changes)
                {
                    composed.TryGetValue(change.Path, out var current);
                    if (current?.Digest == change.AfterDigest) continue; // Identical writes/deletions coalesce.
                    if (current?.Digest != change.BeforeDigest)
                        throw new TaskInputConflictException($"Accepted predecessor changes conflict at '{change.Path}'.");
                    if (change.AfterDigest is null) composed.Remove(change.Path);
                    else composed[change.Path] = new(change.AfterDigest,
                        TaskWorkspacePaths.Resolve(predecessor.FilesRoot, change.Path));
                }
            ValidateComposedPaths(composed.Keys);
            // The archive is host-owned initialization data. Delete only precisely enumerated files.
            foreach (var file in baseEntries.Where(entry => !entry.IsDirectory))
                if (!composed.ContainsKey(file.Path)) File.Delete(TaskWorkspacePaths.Resolve(workspace.WorkspacePath, file.Path));
            foreach (var directory in baseEntries.Where(entry => entry.IsDirectory).OrderByDescending(entry => entry.Path.Length))
            {
                var path = TaskWorkspacePaths.Resolve(workspace.WorkspacePath, directory.Path);
                if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
            }
            foreach (var item in composed.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (item.Value.Source is null) continue;
                var target = TaskWorkspacePaths.Resolve(workspace.WorkspacePath, item.Key);
                if (Directory.Exists(target))
                    throw new TaskInputConflictException($"Accepted changes replace a populated directory at '{item.Key}'.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await CopyCheckedAsync(item.Value.Source, target, item.Value.Digest, ct);
            }
            var entries = await ScanAsync(workspace.WorkspacePath, ct);
            if (!composed.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new ManifestEntry(item.Key, false, item.Value.Digest))
                .SequenceEqual(entries.Where(entry => !entry.IsDirectory)))
                throw new TaskInputConflictException("The prepared workspace does not match its accepted composition.");
            var initial = new InitialManifest(1, workspace, inputs, claim.Task, entries);
            var initialBytes = JsonSerializer.SerializeToUtf8Bytes(initial, InputJson);
            await WriteNewDurableAsync(Path.Combine(workspace.AttemptPath, "inputs.json"), initialBytes, ct);
            await WriteNewDurableAsync(Path.Combine(workspace.AttemptPath, "inputs.sha256"), System.Text.Encoding.ASCII.GetBytes("sha256:" + Digest(initialBytes)), ct);
            await WriteNewDurableAsync(Path.Combine(workspace.AttemptPath, "workspace.json"), JsonSerializer.SerializeToUtf8Bytes(workspace), ct);
            return workspace;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException ||
            error is InvalidOperationException and not TaskInputConflictException)
        { throw new TaskInputConflictException("Accepted task inputs are missing, changed or cannot be composed: " + error.Message); }
    }

    private static TaskAttemptInputs RequireInputs(TaskGraphAgentBinding binding, TaskGraphClaim claim)
    {
        var inputs = claim.Inputs;
        if (inputs is null || inputs.GraphFingerprint != claim.GraphFingerprint || inputs.TaskId != claim.Task.TaskId ||
            inputs.AttemptId != claim.AttemptId || inputs.BaseCommit != binding.BaseCommit || inputs.Dependencies.IsDefault ||
            inputs.Dependencies.Any(pin => pin is null) || inputs.Dependencies.Select(pin => pin.TaskId).Distinct(StringComparer.Ordinal).Count() != inputs.Dependencies.Length ||
            inputs.Digest != TaskInputIdentity.Digest(inputs))
            throw new TaskInputConflictException("The attempt has no matching immutable input identity.");
        return inputs;
    }

    private async Task<DependencyChanges> ReadDependencyAsync(TaskGraphAgentBinding binding, TaskGraphClaim consumer,
        TaskDependencyReceipt pin, TaskDependencyInput dependency, CancellationToken ct)
    {
        if (dependency is null || dependency.Task is null || dependency.Inputs is null || dependency.Result is null || dependency.Acceptance is null ||
            pin.TaskId != dependency.Task.TaskId || pin.AttemptId != dependency.AttemptId ||
            pin.Revision != dependency.Result.Revision || pin.ResultDigest != TaskInputIdentity.ResultDigest(dependency.Result) ||
            !SameJson(pin.Acceptance, dependency.Acceptance) || dependency.Acceptance.Outcome != TaskAcceptanceOutcome.Accepted ||
            dependency.Acceptance.GraphFingerprint != consumer.GraphFingerprint || dependency.Acceptance.TaskId != pin.TaskId ||
            dependency.Acceptance.AttemptId != pin.AttemptId || dependency.Acceptance.Revision != pin.Revision ||
            dependency.Acceptance.ResultDigest != pin.ResultDigest || dependency.Result.GraphId != consumer.GraphId ||
            dependency.Result.GraphFingerprint != consumer.GraphFingerprint || dependency.Result.TaskId != pin.TaskId)
            throw new TaskInputConflictException("A predecessor differs from its accepted receipt.");
        var sourceClaim = new TaskGraphClaim(consumer.GraphId, consumer.GraphFingerprint, dependency.Task,
            dependency.AttemptId, "input-reader", Inputs: dependency.Inputs);
        var source = Describe(binding, sourceClaim);
        ValidateWorkspace(source);
        if (!await VerifyAsync(source, dependency.Result, ct))
            throw new TaskInputConflictException($"Accepted predecessor '{pin.TaskId}' has missing or altered sealed output.");
        var manifest = await ReadManifestAsync(source, pin.Revision, ct);
        var initial = await ReadInitialAsync(source, ct);
        if (manifest.SchemaVersion != 2 || !SameJson(initial.Inputs, dependency.Inputs) || !SameJson(initial.Task, dependency.Task) ||
            !SameJson(manifest.Requirements, dependency.Task.ExpectedArtifacts))
            throw new TaskInputConflictException("The predecessor revision has no matching frozen baseline.");
        return new(manifest.Changes, Path.Combine(RevisionRoot(source, pin.Revision), "files"));
    }

    private static async Task<RevisionManifest> CreateRevisionManifestAsync(TaskWorkspace workspace,
        ImmutableArray<TaskArtifactRequirement> requirements, ImmutableArray<ManifestEntry> entries, CancellationToken ct)
    {
        if (workspace.InputDigest is null) return new(1, workspace, requirements, entries, Changes: []);
        var initial = await ReadInitialAsync(workspace, ct);
        if (!SameJson(requirements, initial.Task.ExpectedArtifacts))
            throw new TaskInputConflictException("Captured artifact declarations differ from the accepted task.");
        var changes = Changes(initial.Entries, entries);
        RequireWriteScope(initial.Task.Resources.WritePaths, changes);
        RequireNoEmptyDirectories(entries);
        var inputBytes = await File.ReadAllBytesAsync(Path.Combine(workspace.AttemptPath, "inputs.json"), ct);
        return new(2, workspace, requirements, entries, "sha256:" + Digest(inputBytes), changes);
    }

    private static async Task<bool> VerifyRevisionInputsAsync(RevisionManifest manifest, CancellationToken ct)
    {
        if (manifest.SchemaVersion == 1) return manifest.Workspace.InputDigest is null;
        if (manifest.Workspace.InputDigest is null || manifest.Changes.IsDefault) return false;
        var initial = await ReadInitialAsync(manifest.Workspace, ct);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(manifest.Workspace.AttemptPath, "inputs.json"), ct);
        if (manifest.InputManifestDigest != "sha256:" + Digest(bytes) || !manifest.Changes.SequenceEqual(Changes(initial.Entries, manifest.Entries))) return false;
        RequireWriteScope(initial.Task.Resources.WritePaths, manifest.Changes);
        RequireNoEmptyDirectories(manifest.Entries);
        return true;
    }

    private static async Task<InitialManifest> ReadInitialAsync(TaskWorkspace workspace, CancellationToken ct)
    {
        var path = Path.Combine(workspace.AttemptPath, "inputs.json");
        TaskWorkspacePaths.NoLinks(path);
        var bytes = await File.ReadAllBytesAsync(path, ct);
        ValidateInitialDigest(workspace, bytes);
        var initial = JsonSerializer.Deserialize<InitialManifest>(bytes, InputJson)
            ?? throw new TaskInputConflictException("The frozen task baseline is missing.");
        ValidateInitial(workspace, initial);
        return initial;
    }

    private static void ValidateInitialDigest(TaskWorkspace workspace, byte[] bytes)
    {
        var checksum = Path.Combine(workspace.AttemptPath, "inputs.sha256");
        TaskWorkspacePaths.NoLinks(checksum);
        if (File.ReadAllText(checksum) != "sha256:" + Digest(bytes))
            throw new TaskInputConflictException("The frozen task baseline has changed since preparation.");
    }

    private static void ValidateInitial(TaskWorkspace workspace, InitialManifest initial)
    {
        if (initial.SchemaVersion != 1 || initial.Workspace != workspace || initial.Entries.IsDefault || initial.Inputs is null ||
            initial.Task is null || initial.Task.TaskId != workspace.TaskId || initial.Inputs.Digest != workspace.InputDigest ||
            initial.Inputs.GraphFingerprint != workspace.GraphFingerprint || initial.Inputs.TaskId != workspace.TaskId ||
            initial.Inputs.AttemptId != workspace.AttemptId || initial.Inputs.BaseCommit != workspace.BaseCommit ||
            initial.Inputs.Dependencies.IsDefault || initial.Inputs.Digest != TaskInputIdentity.Digest(initial.Inputs))
            throw new TaskInputConflictException("The frozen task baseline differs from its attempt.");
        if (initial.Entries.Any(entry => entry is null || entry.Path is null ||
            entry.IsDirectory && entry.ContentDigest is not null || !entry.IsDirectory &&
            (entry.ContentDigest is null || entry.ContentDigest.Length != 71 || !entry.ContentDigest.StartsWith("sha256:", StringComparison.Ordinal) ||
             !entry.ContentDigest.AsSpan(7).ToString().All(Uri.IsHexDigit))))
            throw new TaskInputConflictException("The frozen task baseline has invalid file identities.");
        ValidateComposedPaths(initial.Entries.Where(entry => !entry.IsDirectory).Select(entry => entry.Path));
    }

    private static ImmutableArray<FileChange> Changes(ImmutableArray<ManifestEntry> before, ImmutableArray<ManifestEntry> after)
    {
        var left = before.Where(entry => !entry.IsDirectory).ToDictionary(entry => entry.Path, entry => entry.ContentDigest, StringComparer.Ordinal);
        var right = after.Where(entry => !entry.IsDirectory).ToDictionary(entry => entry.Path, entry => entry.ContentDigest, StringComparer.Ordinal);
        return left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(path =>
        {
            left.TryGetValue(path, out var oldDigest); right.TryGetValue(path, out var newDigest);
            return new FileChange(path, oldDigest, newDigest);
        }).Where(change => change.BeforeDigest != change.AfterDigest).ToImmutableArray();
    }

    private static void RequireWriteScope(ImmutableArray<string> paths, ImmutableArray<FileChange> changes)
    {
        if (paths.IsDefault) throw new TaskInputConflictException("Task write permissions are uninitialized.");
        var allowed = paths.Select(path => TaskWorkspacePaths.Relative(path)).ToArray();
        foreach (var change in changes)
            if (!allowed.Any(path => path == "." || change.Path.Equals(path, TaskWorkspacePaths.Comparison) || change.Path.StartsWith(path + "/", TaskWorkspacePaths.Comparison)))
                throw new TaskInputConflictException($"Task output changes '{change.Path}' outside its accepted write scope.");
    }

    private static void RequireNoEmptyDirectories(ImmutableArray<ManifestEntry> entries)
    {
        foreach (var directory in entries.Where(entry => entry.IsDirectory))
            if (!entries.Any(entry => !entry.IsDirectory && entry.Path.StartsWith(directory.Path + "/", StringComparison.Ordinal)))
                throw new TaskInputConflictException("Accepted changes cannot include empty directories.");
    }

    private static void ValidateComposedPaths(IEnumerable<string> paths)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (TaskWorkspacePaths.Relative(path, allowRoot: false) != path || !files.Add(path))
                throw new TaskInputConflictException("Composed paths contain aliases or collisions.");
            CheckCollision(path, seen);
        }
        foreach (var path in files)
        {
            var parent = path;
            while (parent.LastIndexOf('/') is var separator && separator >= 0)
            {
                parent = parent[..separator];
                if (files.Contains(parent)) throw new TaskInputConflictException("Accepted changes contain a file/directory path collision.");
            }
        }
    }

    private static async Task CopyCheckedAsync(string source, string target, string digest, CancellationToken ct)
    {
        TaskWorkspacePaths.NoLinks(source);
        TaskWorkspacePaths.NoLinks(target);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, ct);
        await output.FlushAsync(ct); output.Flush(flushToDisk: true);
        // Validate the source bytes while it is locked against cooperating writers.
        input.Position = 0;
        var actual = "sha256:" + Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(input, ct));
        if (actual != digest) throw new TaskInputConflictException("A predecessor's sealed bytes changed during preparation.");
    }

    private static bool SameJson<T>(T left, T right) => JsonSerializer.Serialize(left, InputJson) == JsonSerializer.Serialize(right, InputJson);
    private sealed record InitialManifest(int SchemaVersion, TaskWorkspace Workspace, TaskAttemptInputs Inputs,
        WorkflowTaskDefinition Task, ImmutableArray<ManifestEntry> Entries);
    private sealed record FileChange(string Path, string? BeforeDigest, string? AfterDigest);
    private sealed record DependencyChanges(ImmutableArray<FileChange> Changes, string FilesRoot);
    private sealed record ComposedFile(string Digest, string? Source);
}
