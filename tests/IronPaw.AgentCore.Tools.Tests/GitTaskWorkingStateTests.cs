using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools.Tests;

[TestClass]
public sealed class GitTaskWorkingStateTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "IronPawTaskStateTests", Guid.NewGuid().ToString("n"));
    private readonly GitTaskWorkspaceManager manager = new();

    [TestMethod]
    public async Task PristinePreparedWorkspacePassesButUnreceiptedEditCannotBeAdmitted()
    {
        var workspace = await PrepareAsync();
        await manager.ValidateWorkingStateAsync(workspace, [], CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "base.txt"), "unreceipted edit");
        await Assert.ThrowsExactlyAsync<TaskInputConflictException>(() => manager.ValidateWorkingStateAsync(workspace, [], CancellationToken.None));
        Assert.AreEqual("unreceipted edit", await File.ReadAllTextAsync(Path.Combine(workspace.WorkspacePath, "base.txt")));
    }

    [TestMethod]
    public async Task ConfirmedWritesMatchExactUtf8BytesAndOrderWithoutReapplying()
    {
        var workspace = await PrepareAsync();
        var first = Write("first", "base.txt", "previous");
        var second = Write("second", "base.txt", "final \u2603");
        var third = Write("third", "nested/output.txt", "new output");
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "base.txt"), "final \u2603");
        Directory.CreateDirectory(Path.Combine(workspace.WorkspacePath, "nested"));
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "nested", "output.txt"), "new output");
        var before = Snapshot(workspace.AttemptPath);
        await manager.ValidateWorkingStateAsync(workspace, [first, second, third], CancellationToken.None);
        Assert.AreSequenceEqual(before, Snapshot(workspace.AttemptPath));
        await File.AppendAllTextAsync(Path.Combine(workspace.WorkspacePath, "nested", "output.txt"), "tampered");
        await Assert.ThrowsExactlyAsync<TaskInputConflictException>(() => manager.ValidateWorkingStateAsync(workspace, [first, second, third], CancellationToken.None));
    }

    [TestMethod]
    public async Task PendingPlannedCallsAndReadOnlyReceiptsDoNotChangeExpectedFiles()
    {
        var workspace = await PrepareAsync();
        var planned = Write("planned", "base.txt", "not executed") with { State = WorkflowToolState.Planned, Result = null };
        var read = Execution("read", "read_file", new { path = "base.txt" });
        var list = Execution("list", "list_files", new { path = "." });
        var denied = Write("denied", "base.txt", "not written") with { Result = ToolResult.Error("Denied") };
        var before = Snapshot(workspace.AttemptPath);
        await manager.ValidateWorkingStateAsync(workspace, [planned, read, list, denied], CancellationToken.None);
        Assert.AreSequenceEqual(before, Snapshot(workspace.AttemptPath));
    }

    [TestMethod]
    public async Task WindowsReceiptsRespectExistingFileDirectoryAndEstablishedNewDirectoryCasing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var workspace = await PrepareAsync(artifacts: [new("base", "base output", TaskArtifactKind.File, "base.txt")]);
        var writes = ImmutableArray.Create(
            Write("overwrite", "BASE.TXT", "updated"),
            Write("existing-dir", "src/New.cs", "new source"),
            Write("first-dir", "NewDir/First.txt", "first"),
            Write("second-dir", "NEWDIR/Second.txt", "second"));
        foreach (var write in writes)
        {
            var path = Path.Combine(workspace.WorkspacePath, write.Call.Arguments.GetProperty("path").GetString()!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, write.Call.Arguments.GetProperty("content").GetString());
        }
        await manager.ValidateWorkingStateAsync(workspace, writes, CancellationToken.None);
        var capture = await manager.CaptureAsync(workspace, [new("base", "base output", TaskArtifactKind.File, "base.txt")], CancellationToken.None);
        Assert.ContainsSingle(capture.Artifacts);
        Assert.EndsWith("base.txt", capture.Artifacts[0].Location, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WindowsArtifactAliasUsesActualSealedEntryCasing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var requirements = ImmutableArray.Create(new TaskArtifactRequirement("base", "base output", TaskArtifactKind.File, "BASE.TXT"));
        var workspace = await PrepareAsync(artifacts: requirements);
        var capture = await manager.CaptureAsync(workspace, requirements, CancellationToken.None);
        Assert.IsEmpty(capture.MissingRequiredArtifacts);
        Assert.EndsWith("base.txt", Assert.ContainsSingle(capture.Artifacts).Location, StringComparison.Ordinal);
        var result = new WorkflowTaskResult(workspace.GraphId, workspace.GraphFingerprint, workspace.TaskId,
            WorkflowTaskStatus.AwaitingVerification, capture.Revision, "candidate")
        { Artifacts = capture.Artifacts };
        Assert.IsTrue(await manager.VerifyAsync(workspace, result, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(WorkflowToolState.Started)]
    [DataRow(WorkflowToolState.WaitingForChild)]
    [DataRow(WorkflowToolState.Completed)]
    public async Task UncertainToolEffectsAreRejected(WorkflowToolState state)
    {
        var workspace = await PrepareAsync();
        var uncertain = Write("uncertain", "base.txt", "unknown") with { State = state, Result = null };
        await Assert.ThrowsExactlyAsync<TaskInputConflictException>(() => manager.ValidateWorkingStateAsync(workspace, [uncertain], CancellationToken.None));
    }

    [TestMethod]
    public async Task UnknownSuccessfulToolsAndWritesOutsideScopeCannotEstablishState()
    {
        var workspace = await PrepareAsync(["allowed.txt"]);
        await Assert.ThrowsExactlyAsync<TaskInputConflictException>(() => manager.ValidateWorkingStateAsync(workspace,
            [Execution("unknown", "run_process", new { command = "anything" })], CancellationToken.None));
        await Assert.ThrowsExactlyAsync<TaskInputConflictException>(() => manager.ValidateWorkingStateAsync(workspace,
            [Write("outside", "base.txt", "outside scope")], CancellationToken.None));
        await Assert.ThrowsExactlyAsync<TaskInputConflictException>(() => manager.ValidateWorkingStateAsync(workspace,
            [Write("escape", "../outside.txt", "outside root")], CancellationToken.None));
    }

    [TestMethod]
    [DataRow("extra-file")]
    [DataRow("missing-file")]
    [DataRow("extra-directory")]
    public async Task CompleteFileAndDirectoryShapeIsValidated(string alteration)
    {
        var workspace = await PrepareAsync();
        if (alteration == "extra-file") await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "extra.txt"), "extra");
        if (alteration == "missing-file") File.Delete(Path.Combine(workspace.WorkspacePath, "base.txt"));
        if (alteration == "extra-directory") Directory.CreateDirectory(Path.Combine(workspace.WorkspacePath, "empty"));
        await Assert.ThrowsExactlyAsync<TaskInputConflictException>(() => manager.ValidateWorkingStateAsync(workspace, [], CancellationToken.None));
    }

    private static WorkflowToolExecution Write(string id, string path, string content) =>
        Execution(id, "write_file", new { path, content });
    private static WorkflowToolExecution Execution(string id, string name, object arguments) =>
        new(id, new(id, name, JsonSerializer.SerializeToElement(arguments)), ToolResult.Success("confirmed"), State: WorkflowToolState.Completed);

    private async Task<TaskWorkspace> PrepareAsync(ImmutableArray<string>? writePaths = null,
        ImmutableArray<TaskArtifactRequirement>? artifacts = null)
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        await GitAsync(source, ["init"]);
        await GitAsync(source, ["config", "user.name", "IronPaw Tests"]);
        await GitAsync(source, ["config", "user.email", "tests@example.invalid"]);
        await File.WriteAllTextAsync(Path.Combine(source, "base.txt"), "baseline");
        Directory.CreateDirectory(Path.Combine(source, "Src"));
        await File.WriteAllTextAsync(Path.Combine(source, "Src", "existing.cs"), "existing source");
        await GitAsync(source, ["add", "base.txt", "Src/existing.cs"]);
        await GitAsync(source, ["commit", "-m", "Initial"]);
        var commit = await manager.ResolveCommitAsync(source, "HEAD", CancellationToken.None);
        var binding = new TaskGraphAgentBinding("root", source, commit, Path.Combine(root, "store"), "test", "test", new(ToolProfile.Coding), new())
        { DependencyMode = TaskDependencyMode.AcceptedChanges };
        var task = new WorkflowTaskDefinition("task", "objective")
        { Resources = new() { ReadPaths = ["."], WritePaths = writePaths ?? ["."] }, ExpectedArtifacts = artifacts ?? [] };
        var inputs = new TaskAttemptInputs("sha256:graph", task.TaskId, "attempt", commit, [], "");
        inputs = inputs with { Digest = TaskInputIdentity.Digest(inputs) };
        return await manager.PrepareAsync(binding, new("graph", "sha256:graph", task, "attempt", "lease", Inputs: inputs), [], CancellationToken.None);
    }

    private static string[] Snapshot(string path) => Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(entry => entry + ":" + (File.Exists(entry) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(entry))) : "directory")).ToArray();

    private static async Task GitAsync(string source, string[] arguments)
    {
        var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-C"); info.ArgumentList.Add(source);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await output;
        Assert.IsTrue(process.ExitCode == 0, await error);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}
