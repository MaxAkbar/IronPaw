using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class AgentTaskDependencyIntegrationTests
{
    private static readonly CallerIdentity Owner = new("dependency-owner", "Dependency owner", true);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [TestMethod]
    public async Task Chain_ConsumerReadsAcceptedProducer_AndPersistsPinnedInputsInChildRequest()
    {
        await using var scope = await Scope.OpenAsync();
        await File.WriteAllTextAsync(Path.Combine(scope.Source, "source.txt"), "uncommitted local work");
        var graph = Graph(MakeTask("a", "a.txt"), MakeTask("b", "b.txt", "a"));
        var client = new ScriptedClient((request, turn) => (TaskId(request), turn) switch
        {
            ("a", 1) => Write("a.txt", "alpha"),
            ("b", 1) => Read("a.txt"),
            ("b", 2) => Write("b.txt", ReadResult(request, "a.txt") + " -> beta"),
            _ => Final()
        });

        var completed = await scope.Runner(client).StartAsync(graph, Owner, 2, scope.Options(graph), Ct);

        AssertSucceeded(completed);
        Assert.AreEqual("alpha -> beta", await ArtifactTextAsync(completed, "b"));
        Assert.AreEqual(TaskDependencyMode.AcceptedChanges, completed.AgentBinding!.DependencyMode);
        Assert.AreEqual(3, completed.SchemaVersion);
        var consumer = State(completed, "b");
        var dependency = Assert.ContainsSingle(consumer.Inputs!.Dependencies);
        Assert.AreEqual("a", dependency.TaskId);
        Assert.AreEqual(State(completed, "a").Result!.Revision, dependency.Revision);
        Assert.AreEqual(State(completed, "a").Acceptance, dependency.Acceptance);
        var child = Assert.ContainsSingle(run =>
            run.RunId == AgentTaskIdentity.ChildRunId(completed.DefinitionFingerprint, "b", consumer.AttemptId!).Value, await scope.Store.ListAsync(Ct));
        Assert.StartsWith("b", child.Request.UserInput);
        Assert.Contains(consumer.Inputs.Digest, child.Request.UserInput, StringComparison.Ordinal);
        Assert.AreEqual("uncommitted local work", await File.ReadAllTextAsync(Path.Combine(scope.Source, "source.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(scope.Source, "a.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(scope.Source, "b.txt")));
    }

    [TestMethod]
    public async Task Diamond_ComposesEachAcceptedDeltaOnce_AndRetainsDisjointBranchChanges()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph(MakeTask("a", "a.txt"), MakeTask("b", "b.txt", "a"),
            MakeTask("c", "c.txt", "a"), MakeTask("d", "d.txt", "b", "c"));
        var client = new ScriptedClient((request, turn) => (TaskId(request), turn) switch
        {
            ("a", 1) => Write("a.txt", "alpha"),
            ("b", 1) or ("c", 1) => Read("a.txt"),
            ("b", 2) => Write("b.txt", ReadResult(request, "a.txt") + "-beta"),
            ("c", 2) => Write("c.txt", ReadResult(request, "a.txt") + "-gamma"),
            ("d", 1) => [Call("a.txt", "read_file", new { path = "a.txt" }),
                Call("b.txt", "read_file", new { path = "b.txt" }),
                Call("c.txt", "read_file", new { path = "c.txt" }), Usage(), new CompletedDelta()],
            ("d", 2) => Write("d.txt", ReadResult(request, "a.txt") + "/" +
                ReadResult(request, "b.txt") + "/" + ReadResult(request, "c.txt")),
            _ => Final()
        });

        var completed = await scope.Runner(client).StartAsync(graph, Owner, 3, scope.Options(graph), Ct);

        AssertSucceeded(completed);
        Assert.AreEqual("alpha/alpha-beta/alpha-gamma", await ArtifactTextAsync(completed, "d"));
        Assert.AreSequenceEqual(["a", "b", "c"], State(completed, "d").Inputs!.Dependencies.Select(input => input.TaskId).Order().ToArray());
        Assert.AreEqual(3, State(completed, "d").Inputs!.Dependencies.Select(input => input.TaskId).Distinct().Count());
        Assert.IsFalse(File.Exists(Path.Combine(scope.Source, "d.txt")));
    }

    [TestMethod]
    public async Task DivergentBranchWrites_FailConsumerBeforeProviderAdmission_AndPreserveSource()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph(MakeTask("left", "shared.txt"), MakeTask("right", "shared.txt"),
            MakeTask("join", "join.txt", "left", "right"));
        var client = new ScriptedClient((request, turn) => turn == 1
            ? Write("shared.txt", TaskId(request)) : Final());

        var stopped = await scope.Runner(client).StartAsync(graph, Owner, 2, scope.Options(graph), Ct);

        Assert.AreEqual(TaskGraphTaskStatus.Succeeded, State(stopped, "left").Status);
        Assert.AreEqual(TaskGraphTaskStatus.Succeeded, State(stopped, "right").Status);
        Assert.AreEqual(TaskGraphTaskStatus.Failed, State(stopped, "join").Status);
        Assert.AreEqual(WorkflowTaskStatus.Failed, State(stopped, "join").Result!.Status);
        Assert.DoesNotContain(request => TaskId(request) == "join", client.Requests);
        Assert.DoesNotContain(run =>
            run.Request.RequestedRunId == AgentTaskIdentity.ChildRunId(stopped.DefinitionFingerprint, "join", State(stopped, "join").AttemptId!), await scope.Store.ListAsync(Ct));
        Assert.IsFalse(File.Exists(Path.Combine(scope.Source, "shared.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(scope.Source, "join.txt")));
        Assert.AreEqual("committed source", await File.ReadAllTextAsync(Path.Combine(scope.Source, "source.txt")));
    }

    [TestMethod]
    public async Task TamperedAcceptedRevision_FailsConsumerBeforeProviderAdmission()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph(MakeTask("a", "a.txt"), MakeTask("b", "b.txt", "a"));
        var client = new ScriptedClient((request, turn) => turn == 1 ? Write(TaskId(request) + ".txt", TaskId(request)) : Final());
        var manager = new WrappedWorkspaces(scope.Manager)
        {
            BeforePrepare = async (claim, dependencies) =>
            {
                if (claim.Task.TaskId != "b") return;
                var artifact = Assert.ContainsSingle(Assert.ContainsSingle(dependencies).Result.Artifacts);
                File.SetAttributes(artifact.Location, FileAttributes.Normal);
                await File.WriteAllTextAsync(artifact.Location, "tampered after acceptance");
            }
        };

        var stopped = await scope.Runner(client, manager).StartAsync(graph, Owner, 1, scope.Options(graph), Ct);

        Assert.AreEqual(TaskGraphTaskStatus.Succeeded, State(stopped, "a").Status);
        Assert.AreEqual(TaskGraphTaskStatus.Failed, State(stopped, "b").Status);
        Assert.DoesNotContain(request => TaskId(request) == "b", client.Requests);
        Assert.IsFalse(File.Exists(Path.Combine(scope.Source, "a.txt")));
    }

    [TestMethod]
    public async Task RecoveryAfterCompletedChild_ReusesInputDigestAttemptAndChildWithoutProviderReplay()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph(MakeTask("a", "a.txt"), MakeTask("b", "b.txt", "a"));
        var manager = new WrappedWorkspaces(scope.Manager) { FailCaptureOnceForTask = "b" };
        var writer = new ScriptedClient((request, turn) => (TaskId(request), turn) switch
        {
            ("a", 1) => Write("a.txt", "alpha"),
            ("b", 1) => Read("a.txt"),
            ("b", 2) => Write("b.txt", ReadResult(request, "a.txt") + "-beta"),
            _ => Final()
        });
        var interrupted = await scope.Runner(writer, manager).StartAsync(graph, Owner, 1, scope.Options(graph), Ct);
        var before = State(interrupted, "b");
        Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, before.Status);
        var childId = AgentTaskIdentity.ChildRunId(interrupted.DefinitionFingerprint, "b", before.AttemptId!);
        var child = (await scope.Store.GetAsync(childId, Ct))!;
        Assert.AreEqual(AgentRunStatus.Succeeded, child.Result!.Status);
        var inputDigest = before.Inputs!.Digest;
        await scope.ReopenAsync();
        var unused = new ScriptedClient((_, _) => throw new InvalidOperationException("A completed child must not replay the provider."));

        var completed = await scope.Runner(unused).RecoverTaskAsync(graph.GraphId, "b", Owner, Ct);

        AssertSucceeded(completed);
        Assert.IsEmpty(unused.Requests);
        Assert.AreEqual(before.AttemptId, State(completed, "b").AttemptId);
        Assert.AreEqual(inputDigest, State(completed, "b").Inputs!.Digest);
        var recovered = (await scope.Store.GetAsync(childId, Ct))!;
        Assert.AreEqual(child.RunId, recovered.RunId);
        Assert.AreEqual(child.Request.UserInput, recovered.Request.UserInput);
        Assert.AreSequenceEqual(child.ToolExecutions!.Select(tool => tool.ExecutionId), recovered.ToolExecutions!.Select(tool => tool.ExecutionId));
        Assert.AreEqual("alpha-beta", await ArtifactTextAsync(completed, "b"));
    }

    [TestMethod]
    public async Task BaseCommitOnly_ExplicitLegacyModePreservesIndependentCommittedInputs()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph(MakeTask("a", "source.txt"), MakeTask("b", "b.txt", "a"));
        var client = new ScriptedClient((request, turn) => (TaskId(request), turn) switch
        {
            ("a", 1) => Write("source.txt", "producer changed input"),
            ("b", 1) => Read("source.txt"),
            ("b", 2) => Write("b.txt", ReadResult(request, "source.txt")),
            _ => Final()
        });

        var completed = await scope.Runner(client).StartAsync(graph, Owner, 1,
            scope.Options(graph) with { DependencyMode = TaskDependencyMode.BaseCommitOnly }, Ct);

        AssertSucceeded(completed);
        Assert.AreEqual("committed source", await ArtifactTextAsync(completed, "b"));
        Assert.AreEqual(TaskDependencyMode.BaseCommitOnly, completed.AgentBinding!.DependencyMode);
        foreach (var state in completed.Tasks)
        {
            Assert.IsNull(state.Inputs);
        }
        Assert.AreEqual("committed source", await File.ReadAllTextAsync(Path.Combine(scope.Source, "source.txt")));
    }

    [TestMethod]
    public async Task ImportedFiles_DoNotBroadenConsumerReadPermissions()
    {
        await using var scope = await Scope.OpenAsync();
        var consumer = MakeTask("b", "b.txt", "a") with
        { Resources = new() { ReadPaths = ["source.txt"], WritePaths = ["b.txt"] } };
        var graph = Graph(MakeTask("a", "a.txt"), consumer);
        var client = new ScriptedClient((request, turn) => (TaskId(request), turn) switch
        {
            ("a", 1) => Write("a.txt", "restricted predecessor content"),
            ("b", 1) => Read("a.txt"),
            ("b", 2) => Write("b.txt", ReadResult(request, "a.txt")),
            _ => Final()
        });

        var completed = await scope.Runner(client).StartAsync(graph, Owner, 1, scope.Options(graph), Ct);

        AssertSucceeded(completed);
        var receipt = await ArtifactTextAsync(completed, "b");
        Assert.Contains("resource scope", receipt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("restricted predecessor content", receipt, StringComparison.Ordinal);
        var marker = Directory.EnumerateFiles(scope.Managed, "workspace.json", SearchOption.AllDirectories)
            .Select(path => JsonSerializer.Deserialize<TaskWorkspace>(File.ReadAllText(path))!)
            .Single(workspace => workspace.TaskId == "b");
        Assert.AreEqual("restricted predecessor content", await File.ReadAllTextAsync(Path.Combine(marker.WorkspacePath, "a.txt")));
    }

    private static WorkflowTaskDefinition MakeTask(string id, string output, params string[] dependencies) => new(id, id)
    {
        Dependencies = dependencies.ToImmutableArray(),
        ToolPolicy = new(ToolProfile.Coding),
        Resources = new() { ReadPaths = ["."], WritePaths = [output] },
        ExpectedArtifacts = [new("output", "Task output", TaskArtifactKind.File, output)],
        AcceptanceCriteria = [new("integrity", "Declared output is bound to this revision") { ArtifactIds = ["output"] }]
    };
    private static WorkflowTaskGraph Graph(params WorkflowTaskDefinition[] tasks) =>
        new("dependencies-" + Guid.NewGuid().ToString("n"), "Compose accepted predecessor changes", tasks.ToImmutableArray());
    private static TaskGraphTaskState State(TaskGraphCheckpoint checkpoint, string id) => checkpoint.Tasks.Single(state => state.TaskId == id);
    private static void AssertSucceeded(TaskGraphCheckpoint checkpoint) => Assert.IsTrue(
        TaskGraphProgress.Inspect(checkpoint).Status == TaskGraphRunStatus.Succeeded, string.Join(Environment.NewLine, checkpoint.Tasks.Select(state =>
            $"{state.TaskId}: {state.Status}; error={state.Error}; reconciliation={state.ReconciliationNote}; result={state.Result?.Explanation}")));
    private static async Task<string> ArtifactTextAsync(TaskGraphCheckpoint checkpoint, string id) =>
        await File.ReadAllTextAsync(Assert.ContainsSingle(State(checkpoint, id).Result!.Artifacts).Location);
    private static string TaskId(LlmRequest request) => request.Messages.First(message => message.Role == ChatRole.User).Content.Split('\n')[0].TrimEnd('\r');
    private static string ReadResult(LlmRequest request, string path) => request.Messages.Last(message => message.Role == ChatRole.Tool && message.ToolCallId == path).Content;
    private static ToolCallDelta Call(string id, string name, object arguments) => new(id, name, JsonSerializer.SerializeToElement(arguments));
    private static UsageDelta Usage() => new(2, 2, 0, 0);
    private static LlmDelta[] Write(string path, string content) => [Call("write", "write_file", new { path, content }), Usage(), new CompletedDelta()];
    private static LlmDelta[] Read(string path) => [Call(path, "read_file", new { path }), Usage(), new CompletedDelta()];
    private static LlmDelta[] Final() => [Usage(), new AssistantTextDelta("Completed."), new CompletedDelta()];

    private sealed class ScriptedClient(Func<LlmRequest, int, LlmDelta[]> script) : ILlmClient
    {
        private readonly ConcurrentDictionary<SessionKey, int> turns = new();
        public ConcurrentQueue<LlmRequest> Requests { get; } = new();
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            foreach (var delta in script(request, turns.AddOrUpdate(request.SessionKey, 1, (_, previous) => previous + 1)))
            { ct.ThrowIfCancellationRequested(); yield return delta; }
        }
    }

    private sealed class WrappedWorkspaces(ITaskWorkspaceManager inner) : ITaskWorkspaceManager
    {
        private int captureFailure;
        public Func<TaskGraphClaim, ImmutableArray<TaskDependencyInput>, Task>? BeforePrepare { get; init; }
        public string? FailCaptureOnceForTask { get; init; }
        public Task<string> ResolveCommitAsync(string source, string revision, CancellationToken ct) => inner.ResolveCommitAsync(source, revision, ct);
        public Task<TaskWorkspace> OpenOrCreateAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct) => inner.OpenOrCreateAsync(binding, claim, ct);
        public Task<TaskWorkspace> OpenExistingAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct) => inner.OpenExistingAsync(binding, claim, ct);
        public Task ValidateWorkingStateAsync(TaskWorkspace workspace, ImmutableArray<WorkflowToolExecution> tools, CancellationToken ct) => inner.ValidateWorkingStateAsync(workspace, tools, ct);
        public async Task<TaskWorkspace> PrepareAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, ImmutableArray<TaskDependencyInput> dependencies, CancellationToken ct)
        {
            if (BeforePrepare is not null) await BeforePrepare(claim, dependencies);
            return await inner.PrepareAsync(binding, claim, dependencies, ct);
        }
        public Task<TaskWorkspaceCapture> CaptureAsync(TaskWorkspace workspace, ImmutableArray<TaskArtifactRequirement> requirements, CancellationToken ct)
        {
            if (workspace.TaskId == FailCaptureOnceForTask && Interlocked.CompareExchange(ref captureFailure, 1, 0) == 0)
                throw new IOException("Interrupted between completed child and graph candidate capture.");
            return inner.CaptureAsync(workspace, requirements, ct);
        }
        public Task<bool> VerifyAsync(TaskWorkspace workspace, WorkflowTaskResult result, CancellationToken ct) => inner.VerifyAsync(workspace, result, ct);
    }

    private sealed class Scope : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "IronPawDependencyIntegration", Guid.NewGuid().ToString("n"));
        public string Source => Path.Combine(Root, "source");
        public string Managed => Path.Combine(Root, "managed");
        private string DatabasePath => Path.Combine(Root, "workflows.cdb");
        public CSharpDbWorkflowStore Store { get; private set; } = null!;
        public GitTaskWorkspaceManager Manager { get; } = new();
        public static async Task<Scope> OpenAsync()
        {
            var scope = new Scope();
            Directory.CreateDirectory(scope.Source);
            await GitAsync(scope.Source, "init");
            await GitAsync(scope.Source, "config", "user.name", "IronPaw Tests");
            await GitAsync(scope.Source, "config", "user.email", "tests@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(scope.Source, "source.txt"), "committed source");
            await GitAsync(scope.Source, "add", "source.txt");
            await GitAsync(scope.Source, "commit", "-m", "Initial dependency input");
            scope.Store = await CSharpDbWorkflowStore.OpenAsync(scope.DatabasePath, Ct);
            return scope;
        }
        public TaskGraphAgentOptions Options(WorkflowTaskGraph graph) => new(Source, Managed, "HEAD", "mock", "model",
            new(ToolProfile.Coding), new(100, 100, 1_000_000), MaxRounds: 5, MaxOutputTokens: 256)
        { ArtifactVerificationRules = graph.Tasks.Select(task => new TaskArtifactVerificationRule(task.TaskId, "integrity")).ToImmutableArray() };
        public AgentTaskGraphRunner Runner(ILlmClient client, ITaskWorkspaceManager? manager = null) => new(Store, Store, manager ?? Manager, client);
        public async Task ReopenAsync()
        {
            await Store.DisposeAsync();
            Store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath, Ct);
        }
        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            if (!Directory.Exists(Root)) return;
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        private static async Task GitAsync(string path, params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-C"); start.ArgumentList.Add(path);
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
