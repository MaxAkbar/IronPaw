using System.Collections.Immutable;
using System.Diagnostics;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class GitTaskWorkspaceManagerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "IronPawTaskWorkspaceTests", Guid.NewGuid().ToString("n"));
    private readonly GitTaskWorkspaceManager manager = new();

    [Fact]
    public async Task SnapshotUsesAcceptedCommitAndLeavesDirtyCheckoutUntouched()
    {
        var (binding, claim) = await RepositoryAsync();
        await File.WriteAllTextAsync(Path.Combine(binding.SourceWorkspaceRoot, "source.txt"), "uncommitted");
        await File.WriteAllTextAsync(Path.Combine(binding.SourceWorkspaceRoot, "untracked.txt"), "untracked");
        var workspace = await manager.OpenOrCreateAsync(binding, claim, CancellationToken.None);
        Assert.Equal("committed", await File.ReadAllTextAsync(Path.Combine(workspace.WorkspacePath, "source.txt")));
        Assert.False(File.Exists(Path.Combine(workspace.WorkspacePath, "untracked.txt")));
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspacePath, ".git")));
        Assert.Equal("uncommitted", await File.ReadAllTextAsync(Path.Combine(binding.SourceWorkspaceRoot, "source.txt")));
        Assert.Equal(binding.BaseCommit, await manager.ResolveCommitAsync(binding.SourceWorkspaceRoot, "HEAD", CancellationToken.None));
        Assert.Equal(workspace, await manager.OpenOrCreateAsync(binding, claim, CancellationToken.None));
        Assert.NotEqual(workspace.WorkspacePath, (await manager.OpenOrCreateAsync(binding, claim with { AttemptId = "second" }, CancellationToken.None)).WorkspacePath);
    }

    [Fact]
    public async Task ReuseRejectsChangedIdentityAndInterruptedInitialization()
    {
        var (binding, claim) = await RepositoryAsync();
        var workspace = await manager.OpenOrCreateAsync(binding, claim, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenOrCreateAsync(binding with { BaseCommit = new string('a', 40) }, claim, CancellationToken.None));
        File.Delete(Path.Combine(workspace.AttemptPath, "workspace.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenOrCreateAsync(binding, claim, CancellationToken.None));
        Assert.Equal("committed", await File.ReadAllTextAsync(Path.Combine(workspace.WorkspacePath, "source.txt")));
    }

    [Fact]
    public async Task OpenExistingNeverCreatesMissingStoreAttemptOrMetadata()
    {
        var (binding, claim) = await RepositoryAsync();
        var before = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenExistingAsync(binding, claim, CancellationToken.None));
        Assert.False(Directory.Exists(binding.WorkspaceStoreRoot));
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
        var workspace = await manager.OpenOrCreateAsync(binding, claim, CancellationToken.None);
        before = Directory.EnumerateFileSystemEntries(binding.WorkspaceStoreRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(workspace, await manager.OpenExistingAsync(binding, claim, CancellationToken.None));
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(binding.WorkspaceStoreRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenExistingAsync(binding, claim with { AttemptId = "missing-attempt" }, CancellationToken.None));
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(binding.WorkspaceStoreRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenExistingAsync(binding with { BaseCommit = new string('a', 40) }, claim, CancellationToken.None));
        File.Delete(Path.Combine(workspace.AttemptPath, "workspace.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenExistingAsync(binding, claim, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(workspace.AttemptPath, "workspace.json")));
        Assert.True(Directory.Exists(workspace.WorkspacePath));
    }

    [Fact]
    public async Task SnapshotRejectsLinksWithoutWritingOutsideWorkspace()
    {
        var (binding, claim) = await RepositoryAsync();
        var blob = await GitAsync(binding.SourceWorkspaceRoot, ["hash-object", "-w", "--stdin"], "../../outside");
        await GitAsync(binding.SourceWorkspaceRoot, ["update-index", "--add", "--cacheinfo", $"120000,{blob.Trim()},link"]);
        await GitAsync(binding.SourceWorkspaceRoot, ["commit", "-m", "Add hostile link"]);
        binding = binding with { BaseCommit = await manager.ResolveCommitAsync(binding.SourceWorkspaceRoot, "HEAD", CancellationToken.None) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenOrCreateAsync(binding, claim, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(root, "outside")));
        Assert.NotEmpty(Directory.EnumerateDirectories(binding.WorkspaceStoreRoot));
    }

    [Fact]
    public async Task SnapshotRejectsPortableCaseCollisions()
    {
        var (binding, claim) = await RepositoryAsync();
        var blob = (await GitAsync(binding.SourceWorkspaceRoot, ["hash-object", "-w", "--stdin"], "content")).Trim();
        await GitAsync(binding.SourceWorkspaceRoot, ["update-index", "--add", "--cacheinfo", $"100644,{blob},Foo.txt"]);
        await GitAsync(binding.SourceWorkspaceRoot, ["update-index", "--add", "--cacheinfo", $"100644,{blob},foo.txt"]);
        await GitAsync(binding.SourceWorkspaceRoot, ["commit", "-m", "Add colliding paths"]);
        binding = binding with { BaseCommit = await manager.ResolveCommitAsync(binding.SourceWorkspaceRoot, "HEAD", CancellationToken.None) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenOrCreateAsync(binding, claim, CancellationToken.None));
    }

    [Fact]
    public async Task ReplacementObjectsCannotChangeTheAcceptedCommitSnapshot()
    {
        var (binding, claim) = await RepositoryAsync();
        await File.WriteAllTextAsync(Path.Combine(binding.SourceWorkspaceRoot, "source.txt"), "replacement commit");
        await GitAsync(binding.SourceWorkspaceRoot, ["add", "source.txt"]);
        await GitAsync(binding.SourceWorkspaceRoot, ["commit", "-m", "Replacement"]);
        var replacement = await manager.ResolveCommitAsync(binding.SourceWorkspaceRoot, "HEAD", CancellationToken.None);
        await GitAsync(binding.SourceWorkspaceRoot, ["replace", binding.BaseCommit, replacement]);
        var workspace = await manager.OpenOrCreateAsync(binding, claim, CancellationToken.None);
        Assert.Equal("committed", await File.ReadAllTextAsync(Path.Combine(workspace.WorkspacePath, "source.txt")));
    }

    [Theory]
    [InlineData("export-ignore")]
    [InlineData("export-subst")]
    public async Task ArchiveAttributesDoNotTransformTheRawCommitSnapshot(string attribute)
    {
        var (binding, claim) = await RepositoryAsync();
        await File.WriteAllTextAsync(Path.Combine(binding.SourceWorkspaceRoot, "source.txt"), "$Format:%H$");
        await File.WriteAllTextAsync(Path.Combine(binding.SourceWorkspaceRoot, ".gitattributes"), "source.txt " + attribute);
        await GitAsync(binding.SourceWorkspaceRoot, ["add", "source.txt", ".gitattributes"]);
        await GitAsync(binding.SourceWorkspaceRoot, ["commit", "-m", "Archive attributes"]);
        binding = binding with { BaseCommit = await manager.ResolveCommitAsync(binding.SourceWorkspaceRoot, "HEAD", CancellationToken.None) };
        var workspace = await manager.OpenOrCreateAsync(binding, claim, CancellationToken.None);
        Assert.Equal("$Format:%H$", await File.ReadAllTextAsync(Path.Combine(workspace.WorkspacePath, "source.txt")));
        Assert.Equal("source.txt " + attribute, await File.ReadAllTextAsync(Path.Combine(workspace.WorkspacePath, ".gitattributes")));
    }

    [Theory]
    [InlineData("smudge")]
    [InlineData("process")]
    public async Task SnapshotDoesNotExecuteRepositoryFilters(string filter)
    {
        var (binding, claim) = await RepositoryAsync();
        await File.WriteAllTextAsync(Path.Combine(binding.SourceWorkspaceRoot, ".gitattributes"), "source.txt filter=untrusted");
        await GitAsync(binding.SourceWorkspaceRoot, ["add", ".gitattributes"]);
        await GitAsync(binding.SourceWorkspaceRoot, ["commit", "-m", "Filter attribute"]);
        var marker = Path.Combine(root, "filter-invoked.txt");
        await GitAsync(binding.SourceWorkspaceRoot, ["config", "filter.untrusted." + filter, "echo invoked > '" + marker.Replace('\\', '/') + "'; cat"]);
        await GitAsync(binding.SourceWorkspaceRoot, ["config", "filter.untrusted.required", "true"]);
        binding = binding with { BaseCommit = await manager.ResolveCommitAsync(binding.SourceWorkspaceRoot, "HEAD", CancellationToken.None) };
        var workspace = await manager.OpenOrCreateAsync(binding, claim, CancellationToken.None);
        Assert.Equal("committed", await File.ReadAllTextAsync(Path.Combine(workspace.WorkspacePath, "source.txt")));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task RawObjectExtractionPreservesLargeBinaryAndEmptyFilesAndUnicodePaths()
    {
        var (binding, claim) = await RepositoryAsync();
        var bytes = new byte[250_000];
        new Random(123).NextBytes(bytes);
        bytes[0] = 0;
        var directory = Directory.CreateDirectory(Path.Combine(binding.SourceWorkspaceRoot, "nested space")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(directory, "café.bin"), bytes);
        await File.WriteAllBytesAsync(Path.Combine(directory, "empty.bin"), []);
        await GitAsync(binding.SourceWorkspaceRoot, ["add", "."]);
        await GitAsync(binding.SourceWorkspaceRoot, ["commit", "-m", "Binary objects"]);
        binding = binding with { BaseCommit = await manager.ResolveCommitAsync(binding.SourceWorkspaceRoot, "HEAD", CancellationToken.None) };

        var workspace = await manager.OpenOrCreateAsync(binding, claim, CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(workspace.WorkspacePath, "nested space", "café.bin")));
        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(workspace.WorkspacePath, "nested space", "empty.bin")));
    }

    [Fact]
    public async Task StoreCannotOverlapSourceAndRevisionCannotResolveOutsideRepositoryRoot()
    {
        var (binding, claim) = await RepositoryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenOrCreateAsync(binding with { WorkspaceStoreRoot = Path.Combine(binding.SourceWorkspaceRoot, "tasks") }, claim, CancellationToken.None));
        var child = Directory.CreateDirectory(Path.Combine(binding.SourceWorkspaceRoot, "subdir")).FullName;
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ResolveCommitAsync(child, "HEAD", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ResolveCommitAsync(binding.SourceWorkspaceRoot, "--help", CancellationToken.None));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("deleted")]
    [InlineData("extra")]
    [InlineData("manifest")]
    public async Task SealedCaptureRejectsTamperingWhileWorkingCopyMayContinueChanging(string tamper)
    {
        var (binding, claim) = await RepositoryAsync();
        var workspace = await manager.OpenOrCreateAsync(binding, claim, CancellationToken.None);
        var requirements = ImmutableArray.Create(new TaskArtifactRequirement("source", "source output", TaskArtifactKind.File, "source.txt"));
        var capture = await manager.CaptureAsync(workspace, requirements, CancellationToken.None);
        var result = Result(workspace, capture);
        Assert.True(await manager.VerifyAsync(workspace, result, CancellationToken.None));
        var repeated = await manager.CaptureAsync(workspace, requirements, CancellationToken.None);
        Assert.Equal(capture.Revision, repeated.Revision);
        Assert.Equal(capture.Artifacts.ToArray(), repeated.Artifacts.ToArray());
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "source.txt"), "later editable version");
        Assert.True(await manager.VerifyAsync(workspace, result, CancellationToken.None));
        var sealedFile = capture.Artifacts.Single().Location;
        Assert.False(sealedFile.StartsWith(workspace.WorkspacePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (tamper == "changed") await File.WriteAllTextAsync(sealedFile, "tampered");
        if (tamper == "deleted") File.Delete(sealedFile);
        if (tamper == "extra") await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(sealedFile)!, "extra.txt"), "extra");
        if (tamper == "manifest") await File.AppendAllTextAsync(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(sealedFile)!)!, "manifest.json"), " ");
        Assert.False(await manager.VerifyAsync(workspace, result, CancellationToken.None));
    }

    [Fact]
    public async Task ArtifactCaptureTracksDirectoriesMissingRequiredAndExactReceiptIdentities()
    {
        var (binding, claim) = await RepositoryAsync();
        var workspace = await manager.OpenOrCreateAsync(binding, claim, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(workspace.WorkspacePath, "reports"));
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "reports", "report.md"), "report");
        var capture = await manager.CaptureAsync(workspace, [
            new("report", "report", TaskArtifactKind.Report, "reports/report.md"),
            new("directory", "all reports", TaskArtifactKind.Directory, "reports"),
            new("missing", "required output", TaskArtifactKind.Patch, "missing.patch"),
            new("optional", "optional output", TaskArtifactKind.File, "optional.txt", false)
        ], CancellationToken.None);
        Assert.Equal(new[] { "missing" }, capture.MissingRequiredArtifacts.ToArray());
        Assert.Equal(2, capture.Artifacts.Length);
        Assert.False(await manager.VerifyAsync(workspace, Result(workspace, capture), CancellationToken.None));
        var complete = await manager.CaptureAsync(workspace, [new("report", "report", TaskArtifactKind.Report, "reports/report.md")], CancellationToken.None);
        var result = Result(workspace, complete);
        Assert.True(await manager.VerifyAsync(workspace, result, CancellationToken.None));
        Assert.False(await manager.VerifyAsync(workspace, result with { Artifacts = [complete.Artifacts[0] with { ContentDigest = "sha256:incorrect" }] }, CancellationToken.None));
        Assert.False(await manager.VerifyAsync(workspace, result with { Artifacts = [complete.Artifacts[0] with { Location = Path.Combine(workspace.WorkspacePath, "reports/report.md") }] }, CancellationToken.None));
        Assert.False(await manager.VerifyAsync(workspace, result with { TaskId = "different" }, CancellationToken.None));
    }

    [Fact]
    public async Task WorkspaceRootAndSealedContentRejectJunctions()
    {
        var (binding, claim) = await RepositoryAsync();
        var workspace = await manager.OpenOrCreateAsync(binding, claim, CancellationToken.None);
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        var link = Path.Combine(workspace.WorkspacePath, "link");
        await CreateLinkAsync(link, outside);
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CaptureAsync(workspace, [], CancellationToken.None)); }
        finally { Directory.Delete(link); }
        var alias = Path.Combine(root, "source-alias");
        await CreateLinkAsync(alias, binding.SourceWorkspaceRoot);
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ResolveCommitAsync(alias, "HEAD", CancellationToken.None)); }
        finally { Directory.Delete(alias); }
    }

    private async Task<(TaskGraphAgentBinding Binding, TaskGraphClaim Claim)> RepositoryAsync()
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        await GitAsync(source, ["init"]);
        await GitAsync(source, ["config", "user.name", "IronPaw Tests"]);
        await GitAsync(source, ["config", "user.email", "tests@example.invalid"]);
        await GitAsync(source, ["config", "core.ignorecase", "false"]);
        await File.WriteAllTextAsync(Path.Combine(source, "source.txt"), "committed");
        await GitAsync(source, ["add", "source.txt"]);
        await GitAsync(source, ["commit", "-m", "Initial"]);
        var commit = await manager.ResolveCommitAsync(source, "HEAD", CancellationToken.None);
        var binding = new TaskGraphAgentBinding("root", source, commit, Path.Combine(root, "store"), "test", "test", new(ToolProfile.Coding), new());
        var claim = new TaskGraphClaim("graph", "sha256:graph", new("task", "objective"), "attempt", "lease");
        return (binding, claim);
    }

    private static WorkflowTaskResult Result(TaskWorkspace workspace, TaskWorkspaceCapture capture) =>
        new(workspace.GraphId, workspace.GraphFingerprint, workspace.TaskId, WorkflowTaskStatus.Succeeded, capture.Revision, "captured") { Artifacts = capture.Artifacts };

    internal static async Task CreateLinkAsync(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task<string> GitAsync(string root, string[] arguments, string? input = null)
    {
        var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null };
        info.ArgumentList.Add("-C"); info.ArgumentList.Add(root);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input is not null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}
