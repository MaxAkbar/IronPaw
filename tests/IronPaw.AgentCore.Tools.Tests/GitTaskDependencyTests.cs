using System.Collections.Immutable;
using System.Diagnostics;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class GitTaskDependencyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "IronPawTaskDependencyTests", Guid.NewGuid().ToString("n"));
    private readonly GitTaskWorkspaceManager manager = new();
    private TaskGraphAgentBinding binding = null!;

    [Fact]
    public async Task ChainAppliesEachTasksOwnDeltaAndPreservesDirtySource()
    {
        await RepositoryAsync();
        await File.WriteAllTextAsync(Path.Combine(binding.SourceWorkspaceRoot, "source.txt"), "dirty source");
        var first = await ProduceAsync("first", [], "source.txt", "first");
        var second = await ProduceAsync("second", [first], "source.txt", "second");
        var (workspace, _) = await PrepareAsync("third", [first, second]);
        Assert.Equal("second", await ReadAsync(workspace, "source.txt"));
        Assert.Equal("dirty source", await File.ReadAllTextAsync(Path.Combine(binding.SourceWorkspaceRoot, "source.txt")));
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspacePath, ".git")));
    }

    [Fact]
    public async Task DiamondDeduplicatesInheritedChangesAndComposesDisjointBranches()
    {
        await RepositoryAsync();
        var common = await ProduceAsync("common", [], "common.txt", "common");
        var left = await ProduceAsync("left", [common], "left.txt", "left");
        var right = await ProduceAsync("right", [common], "right.txt", "right");
        var (workspace, _) = await PrepareAsync("join", [common, left, right]);
        Assert.Equal("common", await ReadAsync(workspace, "common.txt"));
        Assert.Equal("left", await ReadAsync(workspace, "left.txt"));
        Assert.Equal("right", await ReadAsync(workspace, "right.txt"));
    }

    [Fact]
    public async Task IdenticalConcurrentWritesCoalesce()
    {
        await RepositoryAsync();
        var left = await ProduceAsync("left", [], "source.txt", "same");
        var right = await ProduceAsync("right", [], "source.txt", "same");
        var (workspace, _) = await PrepareAsync("join", [left, right]);
        Assert.Equal("same", await ReadAsync(workspace, "source.txt"));
    }

    [Fact]
    public async Task InputsReadSealedBytesRatherThanLaterProducerWorkspaceEdits()
    {
        await RepositoryAsync();
        var producer = await ProduceAsync("producer", [], "source.txt", "sealed output");
        var producerWorkspace = await manager.OpenExistingAsync(binding, Claim(producer.Task, producer.Inputs), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(producerWorkspace.WorkspacePath, "source.txt"), "later mutable edit");
        var (consumer, _) = await PrepareAsync("consumer", [producer]);
        Assert.Equal("sealed output", await ReadAsync(consumer, "source.txt"));
    }

    [Fact]
    public async Task DifferentConcurrentWritesFailBeforeInitializationReceipt()
    {
        await RepositoryAsync();
        var left = await ProduceAsync("left", [], "source.txt", "left");
        var right = await ProduceAsync("right", [], "source.txt", "right");
        await Assert.ThrowsAsync<TaskInputConflictException>(() => PrepareAsync("join", [left, right]));
        Assert.Equal(2, Directory.EnumerateFiles(binding.WorkspaceStoreRoot, "workspace.json", SearchOption.AllDirectories).Count());
        Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(binding.SourceWorkspaceRoot, "source.txt")));
    }

    [Fact]
    public async Task FileDirectoryCollisionsRejectDisjointBranchMerge()
    {
        await RepositoryAsync();
        var left = await ProduceAsync("left", [], "added", "file");
        var right = await ProduceAsync("right", [], "added/child.txt", "child");
        await Assert.ThrowsAsync<TaskInputConflictException>(() => PrepareAsync("join", [left, right]));
    }

    [Fact]
    public async Task DeletionAndCompetingEditConflict()
    {
        await RepositoryAsync();
        var left = await ProduceAsync("left", [], "source.txt", null);
        var right = await ProduceAsync("right", [], "source.txt", "changed");
        await Assert.ThrowsAsync<TaskInputConflictException>(() => PrepareAsync("join", [left, right]));
    }

    [Fact]
    public async Task FileToDirectoryAndDirectoryToFileTransitionsCompose()
    {
        await RepositoryAsync();
        var task = Task("transition", ["."]);
        var (workspace, claim) = await PrepareAsync(task, []);
        File.Delete(Path.Combine(workspace.WorkspacePath, "source.txt"));
        Directory.CreateDirectory(Path.Combine(workspace.WorkspacePath, "source.txt"));
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "source.txt", "child.txt"), "nested");
        var transition = await SealAsync(workspace, claim);
        var (consumer, _) = await PrepareAsync("consumer", [transition]);
        Assert.Equal("nested", await ReadAsync(consumer, "source.txt/child.txt"));
        var reverseTask = Task("reverse", ["."]);
        var (reverse, reverseClaim) = await PrepareAsync(reverseTask, [transition]);
        File.Delete(Path.Combine(reverse.WorkspacePath, "source.txt", "child.txt"));
        Directory.Delete(Path.Combine(reverse.WorkspacePath, "source.txt"));
        await File.WriteAllTextAsync(Path.Combine(reverse.WorkspacePath, "source.txt"), "flat");
        var reverseInput = await SealAsync(reverse, reverseClaim);
        var (joined, _) = await PrepareAsync("joined", [transition, reverseInput]);
        Assert.Equal("flat", await ReadAsync(joined, "source.txt"));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("deleted")]
    [InlineData("baseline")]
    public async Task AlteredSealedPredecessorIsRejectedBeforeConsumerCreation(string tamper)
    {
        await RepositoryAsync();
        var producer = await ProduceAsync("producer", [], "source.txt", "output");
        var sourceClaim = Claim(producer.Task, producer.Inputs);
        var workspace = await manager.OpenExistingAsync(binding, sourceClaim, CancellationToken.None);
        if (tamper == "changed") await File.WriteAllTextAsync(producer.Result.Artifacts.Single().Location, "tampered");
        if (tamper == "deleted") File.Delete(producer.Result.Artifacts.Single().Location);
        if (tamper == "baseline") await File.AppendAllTextAsync(Path.Combine(workspace.AttemptPath, "inputs.json"), " ");
        var before = Directory.EnumerateDirectories(binding.WorkspaceStoreRoot).Count();
        await Assert.ThrowsAsync<TaskInputConflictException>(() => PrepareAsync("consumer", [producer]));
        Assert.Equal(before, Directory.EnumerateDirectories(binding.WorkspaceStoreRoot).Count());
    }

    [Fact]
    public async Task CaptureRejectsOwnWritesAndDeletesOutsideScope()
    {
        await RepositoryAsync();
        var task = Task("restricted", ["allowed.txt"]);
        var (workspace, _) = await PrepareAsync(task, []);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "source.txt"), "out of scope");
        await Assert.ThrowsAsync<TaskInputConflictException>(() => manager.CaptureAsync(workspace, [], CancellationToken.None));
        File.Delete(Path.Combine(workspace.WorkspacePath, "source.txt"));
        await Assert.ThrowsAsync<TaskInputConflictException>(() => manager.CaptureAsync(workspace, [], CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(workspace.AttemptPath, "revisions")));
    }

    [Fact]
    public async Task AcceptedInitializationDoesNotExpandConsumerWritePermissions()
    {
        await RepositoryAsync();
        var producer = await ProduceAsync("producer", [], "source.txt", "accepted output");
        var task = Task("consumer", ["report.txt"]) with
        { ExpectedArtifacts = [new("report", "report", TaskArtifactKind.Report, "report.txt")] };
        var (workspace, _) = await PrepareAsync(task, [producer]);
        Assert.Equal("accepted output", await ReadAsync(workspace, "source.txt"));
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "report.txt"), "report");
        var capture = await manager.CaptureAsync(workspace, [new("report", "report", TaskArtifactKind.Report, "report.txt")], CancellationToken.None);
        Assert.Single(capture.Artifacts);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "source.txt"), "unauthorized consumer change");
        await Assert.ThrowsAsync<TaskInputConflictException>(() => manager.CaptureAsync(workspace, [], CancellationToken.None));
    }

    [Fact]
    public async Task ReusePreservesWorkingEditsAndRejectsChangedPinsOrPermissions()
    {
        await RepositoryAsync();
        var producer = await ProduceAsync("producer", [], "source.txt", "accepted");
        var task = Task("consumer", ["."]);
        var (workspace, claim) = await PrepareAsync(task, [producer]);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "source.txt"), "in progress");
        Assert.Equal(workspace, await manager.PrepareAsync(binding, claim, [producer], CancellationToken.None));
        Assert.Equal("in progress", await ReadAsync(workspace, "source.txt"));
        await Assert.ThrowsAsync<TaskInputConflictException>(() => manager.PrepareAsync(binding,
            claim with { Task = task with { Resources = new() { ReadPaths = ["."] } } }, [producer], CancellationToken.None));
        var changed = claim.Inputs! with { Dependencies = [] };
        changed = changed with { Digest = TaskInputIdentity.Digest(changed) };
        await Assert.ThrowsAsync<TaskInputConflictException>(() => manager.PrepareAsync(binding, claim with { Inputs = changed }, [], CancellationToken.None));
        Assert.Equal("in progress", await ReadAsync(workspace, "source.txt"));
    }

    [Fact]
    public async Task LookupIsReadOnlyAndMissingInitializationFailsClosed()
    {
        await RepositoryAsync();
        var (workspace, claim) = await PrepareAsync("task", []);
        var paths = Directory.EnumerateFileSystemEntries(binding.WorkspaceStoreRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(workspace, await manager.OpenExistingAsync(binding, claim, CancellationToken.None));
        Assert.Equal(paths, Directory.EnumerateFileSystemEntries(binding.WorkspaceStoreRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
        File.Delete(Path.Combine(workspace.AttemptPath, "inputs.json"));
        await Assert.ThrowsAsync<TaskInputConflictException>(() => manager.PrepareAsync(binding, claim, [], CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(workspace.AttemptPath, "inputs.json")));
        Assert.Equal("base", await ReadAsync(workspace, "source.txt"));
    }

    [Fact]
    public async Task ChangedBaselineCannotAuthorizeRecaptureOrReuse()
    {
        await RepositoryAsync();
        var (workspace, claim) = await PrepareAsync("task", []);
        var path = Path.Combine(workspace.AttemptPath, "inputs.json");
        var initial = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, initial.Replace("\"Entries\":[", "\"Entries\":[ ", StringComparison.Ordinal));
        await Assert.ThrowsAsync<TaskInputConflictException>(() => manager.PrepareAsync(binding, claim, [], CancellationToken.None));
        await Assert.ThrowsAsync<TaskInputConflictException>(() => manager.CaptureAsync(workspace, [], CancellationToken.None));
        Assert.Equal("base", await ReadAsync(workspace, "source.txt"));
    }

    [Fact]
    public async Task ReceiptOrDependencyListTamperingIsRejected()
    {
        await RepositoryAsync();
        var producer = await ProduceAsync("producer", [], "source.txt", "accepted");
        var task = Task("consumer", ["."]);
        var inputs = Inputs(task, [producer]);
        var claim = Claim(task, inputs);
        await Assert.ThrowsAsync<TaskInputConflictException>(() => manager.PrepareAsync(binding, claim,
            [producer with { Result = producer.Result with { Explanation = "changed" } }], CancellationToken.None));
        await Assert.ThrowsAsync<TaskInputConflictException>(() => manager.PrepareAsync(binding, claim, [], CancellationToken.None));
        await Assert.ThrowsAsync<TaskInputConflictException>(() => manager.PrepareAsync(binding,
            claim with { Inputs = inputs with { Digest = "sha256:wrong" } }, [producer], CancellationToken.None));
        await Assert.ThrowsAsync<TaskInputConflictException>(() => manager.OpenOrCreateAsync(binding, claim, CancellationToken.None));
    }

    private async Task<TaskDependencyInput> ProduceAsync(string id, ImmutableArray<TaskDependencyInput> dependencies, string path, string? contents)
    {
        var task = Task(id, ["."]) with
        {
            ExpectedArtifacts = contents is null ? [] : [new("output", "output", TaskArtifactKind.File, path)]
        };
        var (workspace, claim) = await PrepareAsync(task, dependencies);
        var target = Path.Combine(workspace.WorkspacePath, path.Replace('/', Path.DirectorySeparatorChar));
        if (contents is null) File.Delete(target);
        else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); await File.WriteAllTextAsync(target, contents); }
        return await SealAsync(workspace, claim);
    }

    private async Task<TaskDependencyInput> SealAsync(TaskWorkspace workspace, TaskGraphClaim claim)
    {
        var capture = await manager.CaptureAsync(workspace, claim.Task.ExpectedArtifacts, CancellationToken.None);
        var result = new WorkflowTaskResult("graph", "sha256:graph", claim.Task.TaskId, WorkflowTaskStatus.AwaitingVerification, capture.Revision, "candidate")
        { Artifacts = capture.Artifacts };
        var acceptance = new TaskAcceptanceReceipt(TaskAcceptanceOutcome.Accepted, "test", "accepted", "sha256:graph",
            claim.Task.TaskId, claim.AttemptId, TaskInputIdentity.ResultDigest(result), result.Revision, DateTimeOffset.UtcNow);
        Assert.True(await manager.VerifyAsync(workspace, result, CancellationToken.None));
        return new(claim.Task, claim.AttemptId, result, acceptance, claim.Inputs!);
    }

    private Task<(TaskWorkspace Workspace, TaskGraphClaim Claim)> PrepareAsync(string id, ImmutableArray<TaskDependencyInput> dependencies) =>
        PrepareAsync(Task(id, ["."]), dependencies);

    private async Task<(TaskWorkspace Workspace, TaskGraphClaim Claim)> PrepareAsync(WorkflowTaskDefinition task, ImmutableArray<TaskDependencyInput> dependencies)
    {
        var claim = Claim(task, Inputs(task, dependencies));
        return (await manager.PrepareAsync(binding, claim, dependencies, CancellationToken.None), claim);
    }

    private TaskAttemptInputs Inputs(WorkflowTaskDefinition task, ImmutableArray<TaskDependencyInput> dependencies)
    {
        var inputs = new TaskAttemptInputs("sha256:graph", task.TaskId, "attempt-" + task.TaskId, binding.BaseCommit,
            dependencies.Select(item => new TaskDependencyReceipt(item.Task.TaskId, item.AttemptId, TaskInputIdentity.ResultDigest(item.Result),
                item.Result.Revision, item.Acceptance)).ToImmutableArray(), "");
        return inputs with { Digest = TaskInputIdentity.Digest(inputs) };
    }

    private static TaskGraphClaim Claim(WorkflowTaskDefinition task, TaskAttemptInputs inputs) =>
        new("graph", "sha256:graph", task, inputs.AttemptId, "lease", Inputs: inputs);
    private static WorkflowTaskDefinition Task(string id, ImmutableArray<string> writes) =>
        new(id, "objective") { Resources = new() { ReadPaths = ["."], WritePaths = writes } };
    private static Task<string> ReadAsync(TaskWorkspace workspace, string path) => File.ReadAllTextAsync(Path.Combine(workspace.WorkspacePath, path.Replace('/', Path.DirectorySeparatorChar)));

    private async Task RepositoryAsync()
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        await GitAsync(source, ["init"]);
        await GitAsync(source, ["config", "user.name", "IronPaw Tests"]);
        await GitAsync(source, ["config", "user.email", "tests@example.invalid"]);
        await File.WriteAllTextAsync(Path.Combine(source, "source.txt"), "base");
        await GitAsync(source, ["add", "source.txt"]);
        await GitAsync(source, ["commit", "-m", "Initial"]);
        binding = new("root", source, await manager.ResolveCommitAsync(source, "HEAD", CancellationToken.None),
            Path.Combine(root, "store"), "test", "test", new(ToolProfile.Coding), new())
        { DependencyMode = TaskDependencyMode.AcceptedChanges };
    }

    private static async Task GitAsync(string source, string[] arguments)
    {
        var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-C"); info.ArgumentList.Add(source);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await output;
        Assert.True(process.ExitCode == 0, await error);
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}
