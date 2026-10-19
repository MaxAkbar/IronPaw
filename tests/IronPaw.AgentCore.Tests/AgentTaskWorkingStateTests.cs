using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class AgentTaskWorkingStateTests
{
    private static readonly CallerIdentity Owner = new("working-state-owner", "Owner", true);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [TestMethod]
    public async Task NewExecutionChecksPreparedBytesBeforeFirstProviderAdmission()
    {
        await using var fixture = await Fixture.OpenAsync();
        var client = new Writer();
        var manager = new ObservedWorkspaces(fixture.Manager)
        {
            AfterPrepare = workspace => File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "source.txt"), "Changed after preparation")
        };
        var stopped = await fixture.Runner(client, manager).StartAsync(fixture.Graph, Owner, 1, fixture.Options(), Ct);
        Assert.AreEqual(TaskGraphTaskStatus.Failed, Assert.ContainsSingle(stopped.Tasks).Status);
        Assert.AreEqual(0, client.Calls);
        Assert.AreEqual(1, manager.Validations);
        Assert.IsEmpty(manager.LastTools);
        Assert.DoesNotContain(child => child.ParentRunId is not null, await fixture.Store.ListAsync(Ct));
    }

    [TestMethod]
    public async Task CompletedChildBytesMustMatchConfirmedWritesBeforeAnyCapture()
    {
        await using var fixture = await Fixture.OpenAsync();
        TaskWorkspace? prepared = null;
        var manager = new ObservedWorkspaces(fixture.Manager)
        {
            AfterPrepare = workspace => { prepared = workspace; return System.Threading.Tasks.Task.CompletedTask; }
        };
        var client = new Writer(afterWrite: () => File.WriteAllTextAsync(
            Path.Combine(prepared!.WorkspacePath, "output.txt"), "Unrecorded replacement before capture"));

        var stopped = await fixture.Runner(client, manager).StartAsync(fixture.Graph, Owner, 1, fixture.Options(), Ct);

        Assert.AreEqual(TaskGraphTaskStatus.Failed, Assert.ContainsSingle(stopped.Tasks).Status);
        Assert.AreEqual(2, client.Calls);
        Assert.IsTrue(manager.Validations >= 2);
        Assert.AreEqual(0, manager.Captures);
        Assert.IsEmpty(stopped.Tasks[0].Result!.Artifacts);
        var child = Assert.ContainsSingle(run => run.ParentRunId is not null, await fixture.Store.ListAsync(Ct));
        Assert.AreEqual(AgentRunStatus.Succeeded, child.Result!.Status);
        Assert.IsFalse(Assert.ContainsSingle(child.ToolExecutions!).Result!.IsError);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecoveryBeforeFirstProviderChecksPinnedBytesEvenWhenQueuedChildAlreadyExists(bool createQueuedChild)
    {
        await using var fixture = await Fixture.OpenAsync();
        var commit = await fixture.Manager.ResolveCommitAsync(fixture.Source, "HEAD", Ct);
        var options = fixture.Options();
        var binding = new TaskGraphAgentBinding(AgentTaskIdentity.RootRunId(fixture.Graph.GraphId).Value,
            fixture.Source, commit, fixture.Managed, options.Provider, options.Model, options.RootToolPolicy, options.SharedBudget)
        { ArtifactVerificationRules = options.ArtifactVerificationRules, DependencyMode = TaskDependencyMode.AcceptedChanges };
        var accepted = await fixture.Store.AcceptAgentTaskGraphAsync(fixture.Graph, Owner, 1, binding, Ct);
        var claim = (await fixture.Store.ClaimTaskAsync(fixture.Graph.GraphId, Owner, Ct))!;
        var workspace = await fixture.Manager.PrepareAsync(binding, claim, TaskDependencyResolver.Resolve(accepted, claim), Ct);
        if (createQueuedChild)
        {
            var request = AgentTaskBindingValidator.CreateChildRequest(fixture.Graph, Owner, binding, claim.Task, claim.AttemptId, claim.Inputs);
            await fixture.Store.CreateAsync(new(request.RequestedRunId!.Value.Value, binding.RootRunId, binding.RootRunId,
                claim.Task.TaskId, request, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Budget: request.Budget, Usage: new()), Ct);
        }
        await fixture.Store.AbandonTaskClaimAsync(claim, "Stopped before provider admission", Ct);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "source.txt"), "Changed while stopped");
        var client = new Writer();
        var manager = new ObservedWorkspaces(fixture.Manager);

        var stopped = await fixture.Runner(client, manager).RecoverTaskAsync(fixture.Graph.GraphId, "task", Owner, Ct);

        Assert.AreEqual(TaskGraphTaskStatus.Failed, Assert.ContainsSingle(stopped.Tasks).Status);
        Assert.AreEqual(0, client.Calls);
        Assert.AreEqual(1, manager.Validations);
        Assert.IsEmpty(manager.LastTools);
        Assert.AreEqual(claim.AttemptId, stopped.Tasks[0].AttemptId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecoveryUsesConfirmedToolReceiptsToValidateCompletedChildWorkspace(bool tamper)
    {
        await using var fixture = await Fixture.OpenAsync();
        var writer = new Writer();
        var interrupt = new ObservedWorkspaces(fixture.Manager) { InterruptCapture = true };
        var stopped = await fixture.Runner(writer, interrupt).StartAsync(fixture.Graph, Owner, 1, fixture.Options(), Ct);
        Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, Assert.ContainsSingle(stopped.Tasks).Status);
        var child = Assert.ContainsSingle(run => run.ParentRunId is not null, await fixture.Store.ListAsync(Ct));
        Assert.AreEqual(AgentRunStatus.Succeeded, child.Result!.Status);
        var tool = Assert.ContainsSingle(child.ToolExecutions!);
        Assert.AreEqual(WorkflowToolState.Completed, tool.State);
        Assert.IsFalse(tool.Result!.IsError);
        var workspace = JsonSerializer.Deserialize<TaskWorkspace>(await File.ReadAllTextAsync(
            Assert.ContainsSingle(Directory.EnumerateFiles(fixture.Managed, "workspace.json", SearchOption.AllDirectories))))!;
        if (tamper) await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "output.txt"), "Changed after confirmed write");
        var unused = new Writer();
        var manager = new ObservedWorkspaces(fixture.Manager);

        var recovered = await fixture.Runner(unused, manager).RecoverTaskAsync(fixture.Graph.GraphId, "task", Owner, Ct);

        Assert.AreEqual(tamper ? TaskGraphTaskStatus.Failed : TaskGraphTaskStatus.Succeeded, Assert.ContainsSingle(recovered.Tasks).Status);
        Assert.AreEqual(0, unused.Calls);
        Assert.IsTrue(manager.Validations >= 1);
        Assert.AreEqual(tool.ExecutionId, Assert.ContainsSingle(manager.LastTools).ExecutionId);
        Assert.AreEqual(tool.ExecutionId, Assert.ContainsSingle((await fixture.Store.GetAsync(new(child.RunId), Ct))!.ToolExecutions!).ExecutionId);
    }

    private sealed class ObservedWorkspaces(ITaskWorkspaceManager inner) : ITaskWorkspaceManager
    {
        public Func<TaskWorkspace, Task>? AfterPrepare { get; init; }
        public bool InterruptCapture { get; init; }
        public int Validations { get; private set; }
        public int Captures { get; private set; }
        public ImmutableArray<WorkflowToolExecution> LastTools { get; private set; } = [];
        public Task<string> ResolveCommitAsync(string source, string revision, CancellationToken ct) => inner.ResolveCommitAsync(source, revision, ct);
        public Task<TaskWorkspace> OpenOrCreateAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct) => inner.OpenOrCreateAsync(binding, claim, ct);
        public Task<TaskWorkspace> OpenExistingAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct) => inner.OpenExistingAsync(binding, claim, ct);
        public async Task<TaskWorkspace> PrepareAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, ImmutableArray<TaskDependencyInput> dependencies, CancellationToken ct)
        {
            var workspace = await inner.PrepareAsync(binding, claim, dependencies, ct);
            if (AfterPrepare is not null) await AfterPrepare(workspace);
            return workspace;
        }
        public Task ValidateWorkingStateAsync(TaskWorkspace workspace, ImmutableArray<WorkflowToolExecution> tools, CancellationToken ct)
        {
            Validations++;
            LastTools = tools;
            return inner.ValidateWorkingStateAsync(workspace, tools, ct);
        }
        public Task<TaskWorkspaceCapture> CaptureAsync(TaskWorkspace workspace, ImmutableArray<TaskArtifactRequirement> requirements, CancellationToken ct)
        {
            Captures++;
            return InterruptCapture ? throw new IOException("Stopped after child completion before candidate capture") : inner.CaptureAsync(workspace, requirements, ct);
        }
        public Task<bool> VerifyAsync(TaskWorkspace workspace, WorkflowTaskResult result, CancellationToken ct) => inner.VerifyAsync(workspace, result, ct);
    }

    [TestMethod]
    public async Task RecoveryPreservesSuccessfulWritesUsingExistingWindowsFileCasing()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.OpenAsync();
        var task = fixture.Graph.Tasks[0] with
        {
            Resources = new() { ReadPaths = ["."], WritePaths = ["source.txt"] },
            ExpectedArtifacts = [new("output", "Required output", TaskArtifactKind.File, "source.txt")]
        };
        var graph = fixture.Graph with { Tasks = [task] };
        var writer = new Writer("SOURCE.txt");
        var stopped = await fixture.Runner(writer, new ObservedWorkspaces(fixture.Manager) { InterruptCapture = true })
            .StartAsync(graph, Owner, 1, fixture.Options(), Ct);
        Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, Assert.ContainsSingle(stopped.Tasks).Status);
        var unused = new Writer();

        var recovered = await fixture.Runner(unused, new ObservedWorkspaces(fixture.Manager))
            .RecoverTaskAsync(graph.GraphId, "task", Owner, Ct);

        Assert.AreEqual(TaskGraphTaskStatus.Succeeded, Assert.ContainsSingle(recovered.Tasks).Status);
        Assert.AreEqual(0, unused.Calls);
        Assert.AreEqual("Confirmed output", await File.ReadAllTextAsync(Assert.ContainsSingle(recovered.Tasks[0].Result!.Artifacts).Location));
    }

    private sealed class Writer(string path = "output.txt", Func<Task>? afterWrite = null) : ILlmClient
    {
        public int Calls;
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref Calls) == 1)
                yield return new ToolCallDelta("write-output", "write_file", JsonSerializer.SerializeToElement(new { path, content = "Confirmed output" }));
            else
            {
                if (afterWrite is not null) await afterWrite();
                yield return new AssistantTextDelta("Completed");
            }
            yield return new UsageDelta(2, 2, 0, 0);
            yield return new CompletedDelta();
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "IronPawWorkingState", Guid.NewGuid().ToString("n"));
        public string Source => Path.Combine(root, "source");
        public string Managed => Path.Combine(root, "managed");
        public GitTaskWorkspaceManager Manager { get; } = new();
        public CSharpDbWorkflowStore Store { get; private set; } = null!;
        public WorkflowTaskGraph Graph { get; } = new("working-state-" + Guid.NewGuid().ToString("n"), "Verify task admission bytes",
            [new("task", "Produce output")
            {
                ToolPolicy = new(ToolProfile.Coding), Resources = new() { ReadPaths = ["."], WritePaths = ["output.txt"] },
                ExpectedArtifacts = [new("output", "Required output", TaskArtifactKind.File, "output.txt")],
                AcceptanceCriteria = [new("integrity", "Verify exact declared output") { ArtifactIds = ["output"] }]
            }]);
        public static async Task<Fixture> OpenAsync()
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Source);
            await GitAsync(fixture.Source, "init");
            await GitAsync(fixture.Source, "config", "user.name", "IronPaw Tests");
            await GitAsync(fixture.Source, "config", "user.email", "tests@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(fixture.Source, "source.txt"), "Committed source");
            await GitAsync(fixture.Source, "add", "source.txt");
            await GitAsync(fixture.Source, "commit", "-m", "Initial task input");
            fixture.Store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(fixture.root, "workflows.cdb"), Ct);
            return fixture;
        }
        public TaskGraphAgentOptions Options() => new(Source, Managed, "HEAD", "mock", "model", new(ToolProfile.Coding),
            new(20, 20, 100_000), MaxRounds: 4, MaxOutputTokens: 256)
        { ArtifactVerificationRules = [new("task", "integrity")] };
        public AgentTaskGraphRunner Runner(ILlmClient client, ITaskWorkspaceManager manager) => new(Store, Store, manager, client);
        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IronPawWorkingState")), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test directory escaped its intended temporary root.");
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
        private static async Task GitAsync(string directory, params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-C"); start.ArgumentList.Add(directory);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await output;
            Assert.IsTrue(process.ExitCode == 0, await error);
        }
    }
}
