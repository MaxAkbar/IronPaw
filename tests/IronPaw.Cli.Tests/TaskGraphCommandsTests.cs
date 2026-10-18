using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.Cli;
using Spectre.Console;

namespace IronPaw.Cli.Tests;

public sealed class TaskGraphCommandsTests
{
    private static readonly CallerIdentity Owner = CallerIdentity.LocalOwner();
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static (bool, bool) NoProvider(string _) => throw new InvalidOperationException("Provider configuration was requested.");

    [Fact]
    public async Task List_EmptyStoreShowsConfiguredRoots_WithoutProviderWork()
    {
        await using var scope = await Scope.OpenAsync();
        var result = await scope.ExecuteAsync(["--list-graphs"], NoProvider);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(scope.Source, result.Text.Replace("\r", "").Replace("\n", ""));
        Assert.Contains(scope.Managed, result.Text.Replace("\r", "").Replace("\n", ""));
        Assert.Contains("No saved task graphs", result.Text);
        Assert.Equal(0, scope.Client.Calls);
    }

    [Fact]
    public async Task List_OmitsGraphsOwnedByAnotherCaller()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = scope.Graph();
        var options = scope.Options();
        var caller = new CallerIdentity("foreign-owner", "Other owner", true);
        var binding = new TaskGraphAgentBinding(AgentTaskIdentity.RootRunId(graph.GraphId).Value, scope.Source,
            new string('a', 40), scope.Managed, options.Provider, options.Model, options.RootToolPolicy, options.SharedBudget)
        { ArtifactVerificationRules = options.ArtifactVerificationRules, DependencyMode = options.DependencyMode };
        await scope.Store.AcceptAgentTaskGraphAsync(graph, caller, 1, binding, Ct);
        var result = await scope.ExecuteAsync(["--list-graphs"], NoProvider);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No saved task graphs", result.Text);
        Assert.DoesNotContain(graph.GraphId, result.Text);
        Assert.Equal(0, scope.Client.Calls);
    }

    [Fact]
    public async Task Show_ListsDependencyBlockersAndJournal_WithoutInterpretingMarkup()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = scope.Graph() with { Objective = "Inspect [red]literal[/] objective", Tasks = [scope.Task(), scope.Task("dependent") with { Dependencies = ["task"] }] };
        await scope.AcceptAsync(graph);
        var result = await scope.ExecuteAsync(["--show-graph", graph.GraphId], NoProvider);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Inspect [red]literal[/] objective", result.Text);
        Assert.Contains("Blocked by task: Pending", result.Text);
        Assert.Contains("Graph journal", result.Text);
        Assert.Contains("accepted", result.Text);
        Assert.Contains("model: accepted-model", result.Text);
        Assert.Equal(0, scope.Client.Calls);
    }

    [Theory]
    [InlineData("--pause-graph", WorkflowControl.Pause)]
    [InlineData("--cancel-graph", WorkflowControl.Cancel)]
    public async Task Controls_PersistGraphAndRootControlsWithoutProvider(string command, WorkflowControl control)
    {
        await using var scope = await Scope.OpenAsync();
        var graph = scope.Graph();
        await scope.AcceptAsync(graph);
        Assert.Equal(0, (await scope.ExecuteAsync([command, graph.GraphId], NoProvider)).ExitCode);
        var saved = (await scope.Store.GetTaskGraphAsync(graph.GraphId, Ct))!;
        Assert.Equal(control, saved.Control);
        Assert.Equal(control, (await scope.Store.GetAsync(new(saved.AgentBinding!.RootRunId), Ct))!.Control);
        Assert.Equal(0, scope.Client.Calls);
    }

    [Fact]
    public async Task MissingGraph_IsRejectedBeforeProviderOrReconciliationFileAccess()
    {
        await using var scope = await Scope.OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => scope.ExecuteAsync(["--reconcile-graph-task", "missing", "--task", "task",
            "--result-file", "missing.json", "--reconcile-note", "Verified"], NoProvider));
        Assert.Equal(0, scope.Client.Calls);
    }

    [Fact]
    public async Task Start_UsesManifestAndDurableReceipts_CompletedResumeDoesNotConfigureProvider()
    {
        await using var scope = await Scope.OpenAsync();
        var document = scope.Document();
        var path = await scope.WriteAsync(document);
        string? provider = null;
        var started = await scope.ExecuteAsync(["--start-graph", path], name => { provider = name; return (true, false); });
        Assert.Equal(0, started.ExitCode);
        Assert.Equal("accepted-provider", provider);
        Assert.Contains("Succeeded", started.Text);
        Assert.Contains("Artifact output:", started.Text);
        Assert.Contains("Child ", started.Text);
        Assert.Equal(2, scope.Client.Calls);
        var graph = (await scope.Store.GetTaskGraphAsync(document.Graph.GraphId, Ct))!;
        var artifact = Assert.Single(graph.Tasks[0].Result!.Artifacts);
        Assert.Equal("candidate from controlled CLI test", await File.ReadAllTextAsync(artifact.Location));
        Assert.False(File.Exists(Path.Combine(scope.Source, "result.txt")));

        await scope.ReopenAsync();
        var replay = await scope.ExecuteAsync(["--resume-graph", document.Graph.GraphId], NoProvider);
        Assert.Equal(0, replay.ExitCode);
        Assert.Contains("Succeeded", replay.Text);
        Assert.Equal(2, scope.Client.Calls);
    }

    [Fact]
    public async Task Resume_QueuedGraphChecksOnlySavedProviderAndHonorsDeclinedConfiguration()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = scope.Graph();
        await scope.AcceptAsync(graph);
        string? provider = null;
        var result = await scope.ExecuteAsync(["--resume-graph", graph.GraphId], name => { provider = name; return (false, false); });
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("accepted-provider", provider);
        Assert.Equal(0, scope.Client.Calls);
        Assert.Equal(TaskGraphTaskStatus.Pending, (await scope.Store.GetTaskGraphAsync(graph.GraphId, Ct))!.Tasks[0].Status);
    }

    [Fact]
    public async Task Resume_AwaitingAcceptanceNeedsNoProviderAndDoesNotReplayModel()
    {
        await using var scope = await Scope.OpenAsync();
        var document = scope.Document() with { Options = scope.Options() with { ArtifactVerificationRules = [] } };
        var path = await scope.WriteAsync(document);
        var started = await scope.ExecuteAsync(["--start-graph", path], _ => (true, false));
        Assert.Contains("AwaitingAcceptance", started.Text);
        Assert.Equal(2, scope.Client.Calls);
        var result = await scope.ExecuteAsync(["--resume-graph", document.Graph.GraphId], NoProvider);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("AwaitingAcceptance", result.Text);
        Assert.Equal(2, scope.Client.Calls);
    }

    [Fact]
    public async Task Start_RejectsForeignWorkspaceBeforeProviderConfigurationOrAcceptance()
    {
        await using var scope = await Scope.OpenAsync();
        var document = scope.Document() with { Options = scope.Options() with { SourceWorkspaceRoot = Path.Combine(scope.Root, "other") } };
        var path = await scope.WriteAsync(document);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.ExecuteAsync(["--start-graph", path], NoProvider));
        Assert.Empty(await scope.Store.ListTaskGraphsAsync(Ct));
    }

    [Fact]
    public async Task Start_CommandManifestCannotGrantItselfLocalExecutionAuthority()
    {
        await using var scope = await Scope.OpenAsync();
        var document = scope.Document(commands: true);
        var path = await scope.WriteAsync(document);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.ExecuteAsync(["--start-graph", path], NoProvider));
        Assert.Empty(await scope.Store.ListTaskGraphsAsync(Ct));
        Assert.Equal(0, scope.Client.Calls);
    }

    [Fact]
    public async Task ReconcileTask_StoresKnownOutcomeWithoutResumingOrCallingProvider()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = scope.Graph();
        var saved = await scope.AcceptAsync(graph);
        var claim = (await scope.Store.ClaimTaskAsync(graph.GraphId, Owner, Ct))!;
        await scope.Store.AbandonTaskClaimAsync(claim, "Interrupted controlled attempt", Ct);
        var result = new WorkflowTaskResult(graph.GraphId, saved.DefinitionFingerprint, "task", WorkflowTaskStatus.Failed,
            "verified-failure", "Externally established failure");
        var path = await scope.WriteAsync(result);
        var command = await scope.ExecuteAsync(["--reconcile-graph-task", graph.GraphId, "--task", "task", "--result-file", path,
            "--reconcile-note", "Inspected preserved attempt"], NoProvider);
        Assert.Equal(0, command.ExitCode);
        var task = (await scope.Store.GetTaskGraphAsync(graph.GraphId, Ct))!.Tasks[0];
        Assert.Equal(TaskGraphTaskStatus.Failed, task.Status);
        Assert.Equal("Inspected preserved attempt", task.ReconciliationNote);
        Assert.Contains("Externally established failure", command.Text);
        Assert.Equal(0, scope.Client.Calls);
    }

    [Fact]
    public async Task ReconcileCommand_StoresKnownReceiptWithoutExecutingOrAcceptingCandidate()
    {
        await using var scope = await Scope.OpenAsync();
        var document = scope.Document(commands: true);
        var saved = await scope.AcceptAsync(document.Graph, document.Options);
        var claim = (await scope.Store.ClaimTaskAsync(document.Graph.GraphId, Owner, Ct))!;
        var result = new WorkflowTaskResult(document.Graph.GraphId, saved.DefinitionFingerprint, "task", WorkflowTaskStatus.AwaitingVerification,
            "sealed-candidate", "Candidate remains unaccepted");
        await scope.Store.RecordTaskResultAsync(claim, result, Ct);
        var admission = await scope.Store.BeginTaskVerificationAsync(claim, "build", Ct);
        await scope.Store.CompleteTaskVerificationAsync(claim, admission.Invocation.InvocationId,
            Completion(TaskCommandOutcome.NeedsReconciliation, null), Ct);
        await scope.Store.AbandonTaskClaimAsync(claim, "Interrupted command", Ct);
        var path = await scope.WriteAsync(Completion(TaskCommandOutcome.Passed, 0));
        var command = await scope.ExecuteAsync(["--reconcile-graph-command", document.Graph.GraphId, "--task", "task", "--invocation",
            admission.Invocation.InvocationId, "--result-file", path, "--reconcile-note", "Inspected exit and command output"], NoProvider);
        Assert.Equal(0, command.ExitCode);
        var task = (await scope.Store.GetTaskGraphAsync(document.Graph.GraphId, Ct))!.Tasks[0];
        Assert.Equal(TaskGraphTaskStatus.AwaitingAcceptance, task.Status);
        Assert.Null(task.Acceptance);
        Assert.Equal(TaskCommandOutcome.Passed, Assert.Single(task.Verifications).Completion!.Outcome);
        Assert.Contains("worker=cli-reconciliation/v1", command.Text);
        Assert.Contains("stdout: bytes=0", command.Text);
        Assert.Equal(0, scope.Client.Calls);
    }

    [Theory]
    [InlineData("--resume")]
    [InlineData("--pause")]
    [InlineData("--cancel")]
    public async Task LegacyWorkflowControls_RejectGraphAnchorsBeforeProvider(string command)
    {
        await using var scope = await Scope.OpenAsync();
        var graph = await scope.AcceptAsync(scope.Graph());
        using var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No });
        var code = await WorkflowCommands.ExecuteAsync(CliOptions.Parse([command, graph.AgentBinding!.RootRunId]), scope.Store,
            null!, NullAgentEventSink.Instance, NoProvider, Ct, console);
        Assert.Equal(2, code);
        Assert.Contains("belongs to task graph", output.ToString());
        Assert.Contains("--show-graph", output.ToString());
        Assert.Equal(WorkflowControl.None, (await scope.Store.GetTaskGraphAsync(graph.Graph.GraphId, Ct))!.Control);
    }

    [Fact]
    public async Task LegacyWorkflowList_DoesNotExposeBoundGraphAnchor()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = await scope.AcceptAsync(scope.Graph());
        using var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No });
        var code = await WorkflowCommands.ExecuteAsync(CliOptions.Parse(["--list-workflows"]), scope.Store,
            null!, NullAgentEventSink.Instance, NoProvider, Ct, console);
        Assert.Equal(0, code);
        Assert.Contains("No saved workflows", output.ToString());
        Assert.DoesNotContain(graph.AgentBinding!.RootRunId, output.ToString());
    }

    [Fact]
    public void LazyLlmClient_DoesNotLoadProviderConfigurationUntilModelWork()
    {
        var calls = 0;
        var client = new LazyLlmClient(() => { calls++; throw new InvalidOperationException("Invalid provider environment"); });
        Assert.Equal(0, calls);
        var request = new LlmRequest(RunId.New(), new SessionKey("test"), "local", "local", "test", [], [], TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => client.StreamAsync(request, Ct));
        Assert.Equal(1, calls);
    }

    private static TaskVerificationCompletion Completion(TaskCommandOutcome outcome, int? exitCode) =>
        new(outcome, "cli-reconciliation/v1", "Externally established command result", exitCode,
            new("", TaskVerificationIdentity.EmptyLogDigest, 0, false), new("", TaskVerificationIdentity.EmptyLogDigest, 0, false));

    private sealed class WriterClient : ILlmClient
    {
        private readonly ConcurrentDictionary<SessionKey, int> turns = new();
        public int Calls;
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Calls);
            if (turns.AddOrUpdate(request.SessionKey, 1, (_, count) => count + 1) == 1)
                yield return new ToolCallDelta("write", "write_file", JsonSerializer.SerializeToElement(new { path = "result.txt", content = "candidate from controlled CLI test" }));
            else yield return new AssistantTextDelta("Produced candidate output");
            yield return new UsageDelta(7, 3, 0, 0);
            yield return new CompletedDelta();
        }
    }

    private sealed class Scope : IAsyncDisposable
    {
        private static readonly string TestBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IronPawCliGraphTests"));
        public string Root { get; } = Path.Combine(TestBase, Guid.NewGuid().ToString("n"));
        public string Source => Path.Combine(Root, "source");
        public string Managed => Path.Combine(Root, "managed");
        public WriterClient Client { get; } = new();
        public CSharpDbWorkflowStore Store { get; private set; } = null!;
        public TaskGraphHostController Controller { get; private set; } = null!;
        private string Commit = "";
        public static async Task<Scope> OpenAsync()
        {
            var scope = new Scope();
            Directory.CreateDirectory(scope.Source);
            await scope.GitAsync("init");
            await scope.GitAsync("config", "user.name", "IronPaw CLI Tests");
            await scope.GitAsync("config", "user.email", "tests@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(scope.Source, "source.txt"), "Committed fixture");
            await scope.GitAsync("add", ".");
            await scope.GitAsync("commit", "-m", "Controlled fixture");
            scope.Commit = (await scope.GitAsync("rev-parse", "HEAD")).Trim();
            await scope.ReopenAsync();
            return scope;
        }
        public async Task ReopenAsync()
        {
            if (Store is not null) await Store.DisposeAsync();
            Store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(Root, "workflows.cdb"));
            Controller = new(Store, Store, Client, Source, Managed);
        }
        public WorkflowTaskDefinition Task(string id = "task") => new(id, "Produce controlled CLI output")
        {
            ToolPolicy = new(ToolProfile.Coding), Resources = new() { ReadPaths = ["."], WritePaths = ["result.txt"] },
            ExpectedArtifacts = [new("output", "Controlled result", TaskArtifactKind.File, "result.txt")],
            AcceptanceCriteria = [new("integrity", "Output integrity") { ArtifactIds = ["output"] }]
        };
        public WorkflowTaskGraph Graph() => new("cli-graph-" + Guid.NewGuid().ToString("n"), "Controlled CLI graph", [Task()]);
        public TaskGraphAgentOptions Options() => new(Source, Managed, Commit, "accepted-provider", "accepted-model",
            new(ToolProfile.Coding), new(20, 20, 100_000, TimeSpan.FromMinutes(2)))
        { ArtifactVerificationRules = [new("task", "integrity")] };
        public TaskGraphLaunchDocument Document(bool commands = false)
        {
            var graph = Graph();
            var options = Options();
            if (commands)
            {
                graph = graph with { Tasks = [Task() with { ExpectedArtifacts = [], AcceptanceCriteria = [new("build", "Build passes")] }] };
                options = options with { ArtifactVerificationRules = [], VerificationAuthority = TaskVerificationAuthority.TrustedLocalProcess,
                    VerificationProfiles = [new("build", Path.GetFullPath(Environment.ProcessPath!), "sha256:" + new string('a', 64), [], TimeSpan.FromSeconds(5))],
                    CommandVerificationRules = [new("task", "build", "build")] };
            }
            return new(graph, options, 1);
        }
        public Task<TaskGraphCheckpoint> AcceptAsync(WorkflowTaskGraph graph, TaskGraphAgentOptions? options = null)
        {
            options ??= Options();
            var binding = new TaskGraphAgentBinding(AgentTaskIdentity.RootRunId(graph.GraphId).Value, Source, Commit, Managed,
                options.Provider, options.Model, options.RootToolPolicy, options.SharedBudget)
            { ArtifactVerificationRules = options.ArtifactVerificationRules, DependencyMode = options.DependencyMode,
                VerificationAuthority = options.VerificationAuthority, VerificationProfiles = options.VerificationProfiles,
                CommandVerificationRules = options.CommandVerificationRules };
            return Store.AcceptAgentTaskGraphAsync(graph, Owner, 1, binding, Ct);
        }
        public async Task<string> WriteAsync<T>(T value)
        {
            var path = Path.Combine(Root, Guid.NewGuid().ToString("n") + ".json");
            await File.WriteAllTextAsync(path, TaskGraphDocumentJson.Serialize(value));
            return path;
        }
        public async Task<(int ExitCode, string Text)> ExecuteAsync(string[] args, Func<string, (bool, bool)> provider)
        {
            using var output = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No });
            console.Profile.Width = 240;
            var code = await TaskGraphCommands.ExecuteAsync(CliOptions.Parse(args), Controller, provider, Ct, console);
            return (code, output.ToString());
        }
        private async Task<string> GitAsync(params string[] arguments)
        {
            var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-C"); info.ArgumentList.Add(Source);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }
        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(TestBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("CLI test cleanup escaped its isolated root.");
            if (!Directory.Exists(resolved)) return;
            foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(resolved, true);
        }
    }
}
