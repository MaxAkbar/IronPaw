using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

public sealed class TaskGraphHostControllerTests
{
    private static readonly CallerIdentity Owner = CallerIdentity.LocalOwner();
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task StrictLaunchRoundTrip_StartInspectResume_PreservesOneExecutionAndItsEvidence()
    {
        await using var scope = await Scope.OpenAsync();
        var document = scope.Document();
        var parsed = TaskGraphDocumentJson.Parse<TaskGraphLaunchDocument>(TaskGraphDocumentJson.Serialize(document));
        var host = scope.Host();

        var saved = await host.StartAsync(parsed, Owner, false, Ct);
        var detail = await host.InspectAsync(saved.Graph.GraphId, Owner, Ct);
        var resumed = await host.ResumeAsync(saved.Graph.GraphId, Owner, false, Ct);

        Assert.Equal(TaskGraphRunStatus.Succeeded, detail.Progress.Status);
        Assert.Equal(saved.DefinitionFingerprint, resumed.DefinitionFingerprint);
        Assert.Equal(2, scope.Client.Calls);
        Assert.Equal(2, detail.Usage.ModelCalls);
        Assert.Equal(1, detail.Usage.ToolCalls);
        Assert.Equal(20, detail.Budget.ChargedTokens);
        Assert.Equal(2, detail.Invocations.Count);
        Assert.NotEmpty(detail.InvocationJournal);
        Assert.All(detail.InvocationJournal, entry => Assert.Contains(detail.Invocations, run => run.RunId == entry.RunId));
        Assert.Single(detail.Journal, entry => entry.EventType == "task_acceptance");
        Assert.Single(await host.ListAsync(Owner, Ct));
        Assert.Null(await host.GetRequiredProviderAsync(saved.Graph.GraphId, Owner, Ct));
        Assert.Equal("candidate", await File.ReadAllTextAsync(Assert.Single(saved.Tasks[0].Result!.Artifacts).Location));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("store")]
    public async Task LaunchCannotSwitchConfiguredWorkspaceOrManagedStore(string changed)
    {
        await using var scope = await Scope.OpenAsync();
        var document = scope.Document();
        var alternate = Path.Combine(scope.Root, "other");
        var options = changed == "source" ? document.Options with { SourceWorkspaceRoot = alternate }
            : document.Options with { WorkspaceStoreRoot = alternate };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.Host().StartAsync(document with { Options = options }, Owner, false, Ct));

        Assert.Empty(await scope.Store.ListTaskGraphsAsync(Ct));
        Assert.Empty(await scope.Store.ListAsync(Ct));
        Assert.Equal(0, scope.Client.Calls);
        Assert.False(Directory.Exists(scope.Managed));
    }

    [Fact]
    public async Task ProcessAuthorityInsideManifestCannotGrantHostPermission()
    {
        await using var scope = await Scope.OpenAsync();
        var document = scope.Document();
        var task = document.Graph.Tasks[0];
        var options = document.Options with {
            VerificationAuthority = TaskVerificationAuthority.TrustedLocalProcess,
            VerificationProfiles = [new("build", Environment.ProcessPath!, "sha256:" + new string('0', 64), ["--version"], TimeSpan.FromSeconds(5))],
            CommandVerificationRules = [new(task.TaskId, "build", "build")]
        };
        document = document with { Options = options, Graph = document.Graph with {
            Tasks = [task with { AcceptanceCriteria = task.AcceptanceCriteria.Add(new("build", "Build succeeds")) }]
        } };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.Host().StartAsync(document, Owner, false, Ct));

        Assert.Empty(await scope.Store.ListTaskGraphsAsync(Ct));
        Assert.Empty(await scope.Store.ListAsync(Ct));
        Assert.Equal(0, scope.Client.Calls);
        Assert.False(Directory.Exists(scope.Managed));
    }

    [Fact]
    public async Task OtherWorkspaceHostCannotInspectOrControlAnAcceptedGraph()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = await scope.AcceptAsync();
        var foreign = new TaskGraphHostController(scope.Store, scope.Store, scope.Client,
            Path.Combine(scope.Root, "other-source"), Path.Combine(scope.Root, "other-managed"));

        Assert.Empty(await foreign.ListAsync(Owner, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => foreign.InspectAsync(graph.Graph.GraphId, Owner, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => foreign.PauseAsync(graph.Graph.GraphId, Owner, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => foreign.ResumeAsync(graph.Graph.GraphId, Owner, false, Ct));
        Assert.Equal(WorkflowControl.None, (await scope.Store.GetTaskGraphAsync(graph.Graph.GraphId, Ct))!.Control);
        Assert.Equal(0, scope.Client.Calls);
    }

    [Fact]
    public async Task OriginalCallerIsRequiredForInspectionAndControl()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = await scope.AcceptAsync();
        var other = Owner with { Id = "other-owner" };
        var host = scope.Host();

        Assert.Empty(await host.ListAsync(other, Ct));
        Assert.Empty(await host.ListAsync(Owner with { IsOwner = false }, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.InspectAsync(graph.Graph.GraphId, other, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.CancelAsync(graph.Graph.GraphId, other, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.ResumeAsync(graph.Graph.GraphId, other, false, Ct));
        Assert.Equal(WorkflowControl.None, (await scope.Store.GetTaskGraphAsync(graph.Graph.GraphId, Ct))!.Control);
    }

    [Fact]
    public async Task QueuedPauseAndPermanentCancelNeedNoProvider()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = await scope.AcceptAsync();
        var host = scope.Host();

        Assert.Equal("mock", await host.GetRequiredProviderAsync(graph.Graph.GraphId, Owner, Ct));
        await host.PauseAsync(graph.Graph.GraphId, Owner, Ct);
        Assert.Equal(TaskGraphRunStatus.Paused, (await host.InspectAsync(graph.Graph.GraphId, Owner, Ct)).Progress.Status);
        await host.CancelAsync(graph.Graph.GraphId, Owner, Ct);

        Assert.Null(await host.GetRequiredProviderAsync(graph.Graph.GraphId, Owner, Ct));
        await Assert.ThrowsAsync<WorkflowCancelledException>(() => host.ResumeAsync(graph.Graph.GraphId, Owner, false, Ct));
        Assert.Equal(0, scope.Client.Calls);
    }

    [Fact]
    public async Task PendingArtifactAcceptanceDoesNotRequireProviderConfiguration()
    {
        await using var scope = await Scope.OpenAsync();
        var document = scope.Document();
        var incomplete = document with { Options = document.Options with { ArtifactVerificationRules = [] } };
        var candidate = await scope.Host().StartAsync(incomplete, Owner, false, Ct);

        Assert.Equal(TaskGraphRunStatus.AwaitingAcceptance, TaskGraphProgress.Inspect(candidate).Status);
        Assert.Null(await scope.Host().GetRequiredProviderAsync(candidate.Graph.GraphId, Owner, Ct));
        await scope.Host().ResumeAsync(candidate.Graph.GraphId, Owner, false, Ct);
        Assert.Equal(2, scope.Client.Calls);
    }

    [Theory]
    [InlineData("failed", false, false)]
    [InlineData("passed", false, true)]
    [InlineData("missing", false, false)]
    [InlineData("missing", true, true)]
    public async Task ProviderHintHonorsKnownCommandFailureAndMissingRuntimePermission(string receipt, bool allow, bool needsProvider)
    {
        await using var scope = await Scope.OpenAsync();
        var graph = new WorkflowTaskGraph("hint-" + Guid.NewGuid().ToString("n"), "Verify before dependent model work", [
            new("build", "Build the candidate") { AcceptanceCriteria = [new("build-ok", "Build passes")] },
            new("after", "Inspect accepted output") { Dependencies = ["build"], AcceptanceCriteria = [new("review", "Review accepted output")] }
        ]);
        var profile = new TaskVerificationProfile("build", Environment.ProcessPath!, "sha256:" + new string('a', 64), [], TimeSpan.FromSeconds(5));
        var commit = await new GitTaskWorkspaceManager().ResolveCommitAsync(scope.Source, "HEAD", Ct);
        await scope.Store.AcceptAgentTaskGraphAsync(graph, Owner, 1, new(AgentTaskIdentity.RootRunId(graph.GraphId).Value,
            scope.Source, commit, scope.Managed, "mock", "model", new(ToolProfile.Coding), new(10, 10)) {
                DependencyMode = TaskDependencyMode.AcceptedChanges, VerificationAuthority = TaskVerificationAuthority.TrustedLocalProcess,
                VerificationProfiles = [profile], CommandVerificationRules = [new("build", "build-ok", "build")]
            }, Ct);
        var claim = (await scope.Store.ClaimTaskAsync(graph.GraphId, Owner, Ct))!;
        await scope.Store.RecordTaskResultAsync(claim, new(graph.GraphId, claim.GraphFingerprint, "build",
            WorkflowTaskStatus.AwaitingVerification, "exact-candidate", "Await command verification"), Ct);
        if (receipt != "missing")
        {
            var admitted = await scope.Store.BeginTaskVerificationAsync(claim, "build", Ct);
            var log = new TaskVerificationLog("", TaskVerificationIdentity.EmptyLogDigest, 0, false);
            await scope.Store.CompleteTaskVerificationAsync(claim, admitted.Invocation.InvocationId,
                new(receipt == "passed" ? TaskCommandOutcome.Passed : TaskCommandOutcome.Failed,
                    "test-worker", "Known command outcome", receipt == "passed" ? 0 : 1, log, log), Ct);
        }
        await scope.Store.AbandonTaskClaimAsync(claim, "Verification paused before acceptance", Ct);
        var host = scope.Host();
        await host.PauseAsync(graph.GraphId, Owner, Ct);

        var required = await host.GetRequiredProviderAsync(graph.GraphId, Owner, Ct, allowLocalVerification: allow);

        Assert.Equal(needsProvider ? "mock" : null, required);
        Assert.Equal(0, scope.Client.Calls);
        Assert.Equal(TaskGraphTaskStatus.Pending, (await scope.Store.GetTaskGraphAsync(graph.GraphId, Ct))!.Tasks[1].Status);
    }

    [Theory]
    [InlineData(AgentRunStatus.Succeeded, "independent", true)]
    [InlineData(AgentRunStatus.Succeeded, "dependent", true)]
    [InlineData(AgentRunStatus.Failed, "independent", true)]
    [InlineData(AgentRunStatus.Failed, "dependent", false)]
    [InlineData(AgentRunStatus.Succeeded, "none", false)]
    public async Task RecoveryProviderHintIncludesWorkScheduledAfterReusingKnownChildResult(
        AgentRunStatus savedOutcome, string pending, bool needsProvider)
    {
        await using var scope = await Scope.OpenAsync();
        var (graph, options) = scope.RecoveryDocument(pending);
        var child = await scope.InterruptWithChildAsync(graph, options, savedOutcome);
        var host = scope.Host();
        await host.PauseAsync(graph.GraphId, Owner, Ct);

        Assert.Equal(needsProvider ? "mock" : null,
            await host.GetRequiredProviderAsync(graph.GraphId, Owner, Ct, recoverTaskId: "recovered"));
        Assert.Equal(0, scope.Client.Calls);

        var recovered = await host.RecoverTaskAsync(graph.GraphId, "recovered", Owner, false, Ct);

        Assert.Equal(needsProvider ? 2 : 0, scope.Client.Calls);
        Assert.Equal(savedOutcome == AgentRunStatus.Succeeded ? TaskGraphTaskStatus.Succeeded : TaskGraphTaskStatus.Failed,
            recovered.Tasks.Single(task => task.TaskId == "recovered").Status);
        Assert.Equal(child.Result!.Status, (await scope.Store.GetAsync(new(child.RunId), Ct))!.Result!.Status);
        if (pending != "none")
            Assert.Equal(needsProvider ? TaskGraphTaskStatus.Succeeded : TaskGraphTaskStatus.Pending,
                recovered.Tasks.Single(task => task.TaskId == "next").Status);
    }

    [Theory]
    [InlineData("unknown-tool")]
    [InlineData("active-child")]
    public async Task RecoveryProviderHintDoesNotPredictOtherWorkWhenReclaimMustFail(string blocker)
    {
        await using var scope = await Scope.OpenAsync();
        var (graph, options) = scope.RecoveryDocument("independent");
        await scope.InterruptWithChildAsync(graph, options, savedOutcome: null, blocker);
        var host = scope.Host();

        Assert.Null(await host.GetRequiredProviderAsync(graph.GraphId, Owner, Ct, recoverTaskId: "recovered"));
        await Assert.ThrowsAsync<WorkflowReconciliationRequiredException>(() => host.RecoverTaskAsync(graph.GraphId, "recovered", Owner, false, Ct));
        Assert.Equal(0, scope.Client.Calls);
        Assert.Equal(TaskGraphTaskStatus.Pending, (await scope.Store.GetTaskGraphAsync(graph.GraphId, Ct))!.Tasks.Single(task => task.TaskId == "next").Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedSuccessfulChildDoesNotUnblockDependentProviderWithoutRequiredVerificationAuthority(bool allow)
    {
        await using var scope = await Scope.OpenAsync();
        var (graph, options) = scope.RecoveryDocument("dependent");
        graph = graph with { Tasks = graph.Tasks.SetItem(0, graph.Tasks[0] with {
            AcceptanceCriteria = graph.Tasks[0].AcceptanceCriteria.Add(new("build-ok", "The recovered candidate builds"))
        }) };
        options = options with {
            VerificationAuthority = TaskVerificationAuthority.TrustedLocalProcess,
            VerificationProfiles = [new("build", Environment.ProcessPath!, "sha256:" + new string('a', 64), [], TimeSpan.FromSeconds(5))],
            CommandVerificationRules = [new("recovered", "build-ok", "build")]
        };
        await scope.InterruptWithChildAsync(graph, options, AgentRunStatus.Succeeded);

        Assert.Equal(allow ? "mock" : null, await scope.Host().GetRequiredProviderAsync(graph.GraphId, Owner, Ct,
            recoverTaskId: "recovered", allowLocalVerification: allow));
        Assert.Equal(0, scope.Client.Calls);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("numeric-enum")]
    [InlineData("null")]
    [InlineData("trailing-comma")]
    public async Task JsonRejectsAmbiguousOrUnsupportedDocuments(string defect)
    {
        await using var scope = await Scope.OpenAsync();
        var json = TaskGraphDocumentJson.Serialize(scope.Document());
        json = defect switch {
            "unknown" => json.Insert(1, "\"unexpected\": true,"),
            "duplicate" => json.Insert(1, "\"SCHEMAVERSION\": 1,"),
            "numeric-enum" => json.Replace("\"Coding\"", "2", StringComparison.Ordinal),
            "null" => "null",
            "trailing-comma" => json.Insert(json.LastIndexOf('}'), ","),
            _ => throw new InvalidOperationException()
        };
        Assert.ThrowsAny<JsonException>(() => TaskGraphDocumentJson.Parse<TaskGraphLaunchDocument>(json));
    }

    [Fact]
    public async Task JsonFileSupportsUtf8BomAndRejectsInvalidOrOversizedInput()
    {
        await using var scope = await Scope.OpenAsync();
        var path = Path.Combine(scope.Root, "launch.json");
        await File.WriteAllTextAsync(path, TaskGraphDocumentJson.Serialize(scope.Document()), new UTF8Encoding(true));
        Assert.Equal(1, (await TaskGraphDocumentJson.ReadAsync<TaskGraphLaunchDocument>(path, Ct)).SchemaVersion);
        await File.WriteAllBytesAsync(path, [0xff, 0xfe, 0xfd]);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => TaskGraphDocumentJson.ReadAsync<TaskGraphLaunchDocument>(path, Ct));
        await File.WriteAllBytesAsync(path, new byte[TaskGraphDocumentJson.MaxDocumentBytes + 1]);
        await Assert.ThrowsAsync<ArgumentException>(() => TaskGraphDocumentJson.ReadAsync<TaskGraphLaunchDocument>(path, Ct));
    }

    private sealed class Writer : ILlmClient
    {
        public int Calls;
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Calls);
            if (!request.Messages.Any(message => message.Role == ChatRole.Tool))
                yield return new ToolCallDelta("write", "write_file", JsonSerializer.SerializeToElement(new { path = "result.txt", content = "candidate" }));
            else yield return new AssistantTextDelta("Candidate complete; verify it.");
            yield return new UsageDelta(7, 3, 0, 0);
            yield return new CompletedDelta();
        }
    }

    private sealed class Scope : IAsyncDisposable
    {
        private static readonly string TestBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IronPawHostTests"));
        public string Root { get; } = Path.Combine(TestBase, Guid.NewGuid().ToString("n"));
        public string Source => Path.Combine(Root, "source");
        public string Managed => Path.Combine(Root, "managed");
        public CSharpDbWorkflowStore Store { get; private set; } = null!;
        public Writer Client { get; } = new();
        public static async Task<Scope> OpenAsync()
        {
            var scope = new Scope();
            Directory.CreateDirectory(scope.Source);
            await Git(scope.Source, "init");
            await Git(scope.Source, "config", "user.name", "IronPaw Tests");
            await Git(scope.Source, "config", "user.email", "tests@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(scope.Source, "source.txt"), "committed");
            await Git(scope.Source, "add", ".");
            await Git(scope.Source, "commit", "-m", "Fixture");
            scope.Store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(scope.Root, "workflows.cdb"), Ct);
            return scope;
        }
        public TaskGraphHostController Host() => new(Store, Store, Client, Source, Managed);
        public TaskGraphLaunchDocument Document()
        {
            var graph = new WorkflowTaskGraph("host-" + Guid.NewGuid().ToString("n"), "Prepare a verified output", [
                new("write", "Write result.txt") {
                    ToolPolicy = new(ToolProfile.Coding), Resources = new() { ReadPaths = ["."], WritePaths = ["result.txt"] },
                    ExpectedArtifacts = [new("output", "Output", TaskArtifactKind.File, "result.txt")],
                    AcceptanceCriteria = [new("integrity", "Exact output exists") { ArtifactIds = ["output"] }]
                }
            ]);
            return new(graph, new(Source, Managed, "HEAD", "mock", "model", new(ToolProfile.Coding), new(10, 10, 10_000), MaxRounds: 3, MaxOutputTokens: 64) {
                ArtifactVerificationRules = [new("write", "integrity")]
            }, 1);
        }
        public async Task<TaskGraphCheckpoint> AcceptAsync()
        {
            var document = Document();
            var commit = await new GitTaskWorkspaceManager().ResolveCommitAsync(Source, "HEAD", Ct);
            return await Store.AcceptAgentTaskGraphAsync(document.Graph, Owner, 1, new(AgentTaskIdentity.RootRunId(document.Graph.GraphId).Value,
                Source, commit, Managed, "mock", "model", new(ToolProfile.Coding), new(10, 10, 10_000), MaxRounds: 3, MaxOutputTokens: 64) {
                    DependencyMode = TaskDependencyMode.AcceptedChanges, ArtifactVerificationRules = document.Options.ArtifactVerificationRules
                }, Ct);
        }
        public (WorkflowTaskGraph Graph, TaskGraphAgentOptions Options) RecoveryDocument(string pending)
        {
            var document = Document();
            var recovered = document.Graph.Tasks[0] with { TaskId = "recovered", Objective = "Inspect the committed source",
                Resources = new() { ReadPaths = ["."] },
                ExpectedArtifacts = [new("output", "Committed source", TaskArtifactKind.File, "source.txt")]
            };
            var next = document.Graph.Tasks[0] with { TaskId = "next", Dependencies = pending == "dependent" ? ["recovered"] : [] };
            var graph = document.Graph with { Tasks = pending == "none" ? [recovered] : [recovered, next] };
            return (graph, document.Options with {
                ArtifactVerificationRules = graph.Tasks.Select(task => new TaskArtifactVerificationRule(task.TaskId, "integrity")).ToImmutableArray()
            });
        }
        public async Task<WorkflowCheckpoint> InterruptWithChildAsync(WorkflowTaskGraph graph, TaskGraphAgentOptions options,
            AgentRunStatus? savedOutcome, string? blocker = null)
        {
            var binding = new TaskGraphAgentBinding(AgentTaskIdentity.RootRunId(graph.GraphId).Value, Source,
                await new GitTaskWorkspaceManager().ResolveCommitAsync(Source, "HEAD", Ct), Managed, options.Provider,
                options.Model, options.RootToolPolicy, options.SharedBudget, MaxRounds: options.MaxRounds, MaxOutputTokens: options.MaxOutputTokens) {
                    DependencyMode = TaskDependencyMode.AcceptedChanges, ArtifactVerificationRules = options.ArtifactVerificationRules,
                    VerificationAuthority = options.VerificationAuthority, VerificationProfiles = options.VerificationProfiles,
                    CommandVerificationRules = options.CommandVerificationRules
                };
            var accepted = await Store.AcceptAgentTaskGraphAsync(graph, Owner, 1, binding, Ct);
            var claim = (await Store.ClaimTaskAsync(graph.GraphId, Owner, Ct))!;
            var request = AgentTaskBindingValidator.CreateChildRequest(graph, Owner, accepted.AgentBinding!, claim.Task, claim.AttemptId, claim.Inputs);
            var result = savedOutcome is { } outcome
                ? new AgentRunResult(request.RequestedRunId!.Value, request.SessionKey, "Saved child outcome", [], new Dictionary<string, object?>(), outcome)
                : null;
            var now = DateTimeOffset.UtcNow;
            var child = new WorkflowCheckpoint(request.RequestedRunId!.Value.Value, binding.RootRunId, binding.RootRunId,
                "saved-child", request, now, now, Status: savedOutcome == AgentRunStatus.Succeeded ? WorkflowStatus.Succeeded : WorkflowStatus.Interrupted,
                LeaseOwner: blocker == "active-child" ? "active-worker" : null, Budget: request.Budget, Usage: new(), Result: result,
                ToolExecutions: blocker == "unknown-tool" ? [new("unknown-read", new("read", "read_file", JsonSerializer.SerializeToElement(new { path = "source.txt" })))] : []);
            await Store.CreateAsync(child, Ct);
            await Store.AbandonTaskClaimAsync(claim, "Stopped after the child checkpoint, before graph result capture", Ct);
            return child;
        }
        private static async Task Git(string source, params string[] arguments)
        {
            var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-C"); info.ArgumentList.Add(source);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await output;
            Assert.True(process.ExitCode == 0, await error);
        }
        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(TestBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(resolved, true);
        }
    }
}
