using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed partial class GitTaskWorkspaceManager
{
    /// <summary>Copies a sealed revision without exposing its host metadata to the command workspace.</summary>
    public async Task<TaskVerificationCopy> CreateVerificationCopyAsync(TaskWorkspace workspace,
        WorkflowTaskResult result, string invocationId, CancellationToken ct)
    {
        RequireVerificationId(invocationId);
        if (!await VerifyAsync(workspace, result, ct).ConfigureAwait(false))
            throw new TaskInputConflictException("The sealed candidate changed before verification preparation.");
        // CreateProcess still constrains current-directory length on Windows. Avoid repeating
        // the full attempt and invocation digests in this disposable path; metadata binds both.
        var verificationRoot = Path.Combine(Path.GetDirectoryName(workspace.AttemptPath)!, "verification");
        TaskWorkspacePaths.NoLinks(verificationRoot);
        Directory.CreateDirectory(verificationRoot);
        var evidence = Path.Combine(verificationRoot, Guid.NewGuid().ToString("N"));
        using var owner = OpenLock(evidence + ".lock");
        TaskWorkspacePaths.NoLinks(evidence);
        if (Directory.Exists(evidence) || File.Exists(evidence))
            throw new TaskInputConflictException("This verification copy already exists; preserve it for reconciliation.");
        var copy = new TaskVerificationCopy(invocationId, result.Revision, Path.Combine(evidence, "workspace"), evidence);
        Directory.CreateDirectory(copy.WorkspacePath);
        var sourceManifest = await File.ReadAllBytesAsync(Path.Combine(RevisionRoot(workspace, result.Revision), "manifest.json"), ct);
        var manifest = JsonSerializer.Deserialize<RevisionManifest>(sourceManifest)
            ?? throw new TaskInputConflictException("The sealed revision manifest is missing.");
        var inputBytes = JsonSerializer.SerializeToUtf8Bytes(new VerificationInputManifest(invocationId, result.Revision, manifest.Entries));
        copy = copy with { InputDigest = "sha256:" + Digest(inputBytes) };
        var sourceFiles = Path.Combine(RevisionRoot(workspace, result.Revision), "files");
        foreach (var entry in manifest.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var destination = TaskWorkspacePaths.Resolve(copy.WorkspacePath, entry.Path);
            if (entry.IsDirectory) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(TaskWorkspacePaths.Resolve(sourceFiles, entry.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        if (!Equal(manifest.Entries, await ScanAsync(copy.WorkspacePath, ct).ConfigureAwait(false)) ||
            !await VerifyAsync(workspace, result, ct).ConfigureAwait(false))
            throw new TaskInputConflictException("The sealed candidate changed while preparing the verification copy.");
        await WriteNewDurableAsync(Path.Combine(evidence, "source-manifest.json"), sourceManifest, ct).ConfigureAwait(false);
        await WriteNewDurableAsync(Path.Combine(evidence, "input-manifest.json"), inputBytes, ct).ConfigureAwait(false);
        await WriteNewDurableAsync(Path.Combine(evidence, "copy.json"),
            JsonSerializer.SerializeToUtf8Bytes(new VerificationCopyManifest(copy, TaskInputIdentity.ResultDigest(result))), ct).ConfigureAwait(false);
        return copy;
    }

    internal static async Task ValidateVerificationCopyAsync(TaskVerificationJob job, bool checkCopy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        RequireVerificationId(job.Copy.InvocationId);
        if (job.Copy.InvocationId != TaskVerificationIdentity.InvocationId(job.Claim, job.Result, job.Profile) ||
            job.ProfileFingerprint != TaskVerificationIdentity.ProfileFingerprint(job.Profile) || job.Copy.Revision != job.Result.Revision)
            throw new TaskInputConflictException("The verification job identity does not match its accepted inputs.");
        TaskWorkspacePaths.NoLinks(job.Copy.EvidencePath);
        TaskWorkspacePaths.NoLinks(job.Copy.WorkspacePath);
        if (!TaskWorkspacePaths.Same(job.Copy.WorkspacePath, Path.Combine(job.Copy.EvidencePath, "workspace")))
            throw new TaskInputConflictException("The verification copy path is inconsistent.");
        var markerPath = Path.Combine(job.Copy.EvidencePath, "copy.json");
        var manifestPath = Path.Combine(job.Copy.EvidencePath, "source-manifest.json");
        var inputPath = Path.Combine(job.Copy.EvidencePath, "input-manifest.json");
        TaskWorkspacePaths.NoLinks(markerPath);
        TaskWorkspacePaths.NoLinks(manifestPath);
        TaskWorkspacePaths.NoLinks(inputPath);
        var marker = JsonSerializer.Deserialize<VerificationCopyManifest>(await File.ReadAllBytesAsync(markerPath, ct));
        if (marker is null || marker.Copy != job.Copy || marker.ResultDigest != TaskInputIdentity.ResultDigest(job.Result))
            throw new TaskInputConflictException("The verification copy marker is missing or changed.");
        var bytes = await File.ReadAllBytesAsync(manifestPath, ct);
        if ("sha256:" + Digest(bytes) != job.Result.Revision)
            throw new TaskInputConflictException("The verification input manifest does not match the sealed revision.");
        var manifest = JsonSerializer.Deserialize<RevisionManifest>(bytes)
            ?? throw new TaskInputConflictException("The verification input manifest is invalid.");
        var inputBytes = await File.ReadAllBytesAsync(inputPath, ct);
        var input = JsonSerializer.Deserialize<VerificationInputManifest>(inputBytes);
        if (job.Copy.InputDigest != "sha256:" + Digest(inputBytes) || input is null ||
            input.InvocationId != job.Copy.InvocationId || input.Revision != job.Result.Revision || !Equal(input.Entries, manifest.Entries))
            throw new TaskInputConflictException("The verification copy input proof is missing or changed.");
        if (manifest.Workspace.GraphId != job.Claim.GraphId || manifest.Workspace.GraphFingerprint != job.Claim.GraphFingerprint ||
            manifest.Workspace.TaskId != job.Claim.Task.TaskId || manifest.Workspace.AttemptId != job.Claim.AttemptId ||
            !Guid.TryParseExact(Path.GetFileName(job.Copy.EvidencePath), "N", out _) ||
            !TaskWorkspacePaths.Same(Path.GetDirectoryName(job.Copy.EvidencePath)!, Path.Combine(Path.GetDirectoryName(manifest.Workspace.AttemptPath)!, "verification")))
            throw new TaskInputConflictException("The verification copy belongs to another task attempt.");
        ValidateWorkspace(manifest.Workspace);
        if (!await VerifyManifestAsync(manifest.Workspace, job.Result.Revision, ct).ConfigureAwait(false) ||
            checkCopy && !Equal(manifest.Entries, await ScanAsync(job.Copy.WorkspacePath, ct).ConfigureAwait(false)))
            throw new TaskInputConflictException("The sealed candidate or prepared verification input changed.");
    }

    private static void RequireVerificationId(string value)
    {
        if (value is not { Length: 71 } || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            !value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Verification invocation identities must be lowercase SHA256 identities.", nameof(value));
    }

    private sealed record VerificationCopyManifest(TaskVerificationCopy Copy, string ResultDigest);
    private sealed record VerificationInputManifest(string InvocationId, string Revision,
        System.Collections.Immutable.ImmutableArray<ManifestEntry> Entries);
}
