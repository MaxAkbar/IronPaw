using IronPaw.AgentCore.Abstractions;
using IronPaw.Cli;

namespace IronPaw.Cli.Tests;

public sealed class AppBootstrapperTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "ironpaw-cli-tests", Guid.NewGuid().ToString("N"));

    private AppBootstrap Build(string? workspace = null) =>
        AppBootstrapper.Build(stateRoot: Path.Combine(_testRoot, "state"),
            workspaceRoot: workspace ?? _testRoot);

    [Fact]
    public void Build_RegistersWebBrowserSpecialistAgent()
    {
        using var bootstrap = Build();

        var webBrowser = bootstrap.AgentRegistry.Get("web_browser");

        Assert.NotNull(webBrowser);
        Assert.Equal(ToolProfile.Web, webBrowser!.ToolPolicy.Profile);
        Assert.Contains("web pages", webBrowser.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("web_navigate", webBrowser.ToolPolicy.AllowList ?? []);
        Assert.Contains("web_extract_structured", webBrowser.ToolPolicy.AllowList ?? []);
        Assert.NotNull(bootstrap.WebBrowserService);
    }

    [Fact]
    public void Build_RestrictsCodegenAndReviewerFromDirectWebTools()
    {
        using var bootstrap = Build();

        var codegen = bootstrap.AgentRegistry.Get("codegen");
        var reviewer = bootstrap.AgentRegistry.Get("reviewer");

        Assert.NotNull(codegen);
        Assert.NotNull(reviewer);
        Assert.Contains("web_navigate", codegen!.ToolPolicy.DenyList ?? []);
        Assert.Contains("web_extract_structured", codegen.ToolPolicy.DenyList ?? []);
        Assert.Contains("web_navigate", reviewer!.ToolPolicy.DenyList ?? []);
        Assert.Contains("web_extract_structured", reviewer.ToolPolicy.DenyList ?? []);
        Assert.Contains("web_browser", codegen.AllowedMessageSenders ?? []);
        Assert.Contains("web_browser", reviewer.AllowedMessageSenders ?? []);
        var tools = IronPaw.AgentCore.Tools.BuiltInToolFactory.CreateDefault(Path.GetTempPath());
        var available = new IronPaw.AgentCore.Tools.InMemoryToolRegistry(tools).ResolveTools(
            reviewer.ToolPolicy with { SenderIsOwner = true });
        Assert.Contains(available, tool => tool.Descriptor.Name == "read_file");
        Assert.DoesNotContain(available, tool => tool.Descriptor.Name == "write_file");
    }

    [Fact]
    public async Task Build_UsesCSharpDbWorkflowStore()
    {
        using var bootstrap = Build();

        Assert.IsType<IronPaw.AgentCore.Persistence.CSharpDb.CSharpDbWorkflowStore>(bootstrap.WorkflowStore);
        Assert.IsAssignableFrom<IResumableAgentOrchestrator>(bootstrap.Orchestrator);
        Assert.Empty(await bootstrap.WorkflowStore.ListAsync(CancellationToken.None));
        Assert.True(File.Exists(AppBootstrapper.ResolveWorkflowDatabasePath(Path.Combine(_testRoot, "state"), _testRoot)));
    }

    [Fact]
    public async Task Build_IsolatesWorkflowJournalsByWorkspaceAndPreservesThemAcrossReopen()
    {
        var firstWorkspace = Path.Combine(_testRoot, "first");
        var secondWorkspace = Path.Combine(_testRoot, "second");
        Directory.CreateDirectory(firstWorkspace);
        Directory.CreateDirectory(secondWorkspace);
        var runId = RunId.New();
        var now = DateTimeOffset.UtcNow;
        var checkpoint = new WorkflowCheckpoint(runId.Value, runId.Value, null, "task",
            new AgentRunRequest(new SessionKey("test"), "hello", "local", "local",
                new ToolSelectionPolicy(ToolProfile.Minimal), CallerIdentity.LocalOwner()), now, now);

        using (var first = Build(workspace: firstWorkspace))
        {
            await first.WorkflowStore.CreateAsync(checkpoint, CancellationToken.None);
            using var second = Build(workspace: secondWorkspace);
            Assert.Null(await second.WorkflowStore.GetAsync(runId, CancellationToken.None));
        }

        using var reopened = Build(workspace: firstWorkspace);
        Assert.Equal(runId.Value, (await reopened.WorkflowStore.GetAsync(runId, CancellationToken.None))!.RunId);
    }

    [Fact]
    public async Task Build_TaskGraphsShareTheWorkflowLedgerAndUseManagedWorkspaceRoot()
    {
        var workspace = Path.Combine(_testRoot, "workspace");
        Directory.CreateDirectory(workspace);
        using var bootstrap = Build(workspace: workspace);
        Assert.Same(bootstrap.WorkflowStore, bootstrap.TaskGraphStore);
        Assert.IsType<LazyLlmClient>(bootstrap.LlmClient);
        Assert.Equal(workspace, bootstrap.GraphController.WorkspaceRoot);
        var database = AppBootstrapper.ResolveWorkflowDatabasePath(Path.Combine(_testRoot, "state"), workspace);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(database)!, "task-workspaces"), bootstrap.GraphController.WorkspaceStoreRoot);
        Assert.Empty(await bootstrap.GraphController.ListAsync(CallerIdentity.LocalOwner(), CancellationToken.None));
    }

    [Fact]
    public async Task ApplicationStoresPersistAcrossHostsWithoutChangingToolCatalog()
    {
        var key = new SessionKey("host-storage");
        using (var first = Build(workspace: Path.Combine(_testRoot, "first")))
        using (var second = Build(workspace: Path.Combine(_testRoot, "second")))
        {
            Assert.IsType<IronPaw.AgentCore.Persistence.CSharpDb.CSharpDbApplicationStore>(first.SessionStore);
            Assert.Same(first.SessionStore, first.TranscriptStore);
            Assert.Same(first.SessionStore, first.MemoryStore);
            Assert.Same(first.SessionStore, first.RunHistoryStore);
            Assert.IsType<IronPaw.AgentCore.Persistence.CSharpDb.CSharpDbAgentMessageBus>(first.MessageBus);
            await first.SessionStore.UpsertAsync(key, _ => new(key.Value, DateTimeOffset.UtcNow), default);
            await first.TranscriptStore.AppendAsync(key, [new(ChatRole.User, "saved")], default);
            Assert.NotNull(await second.SessionStore.GetAsync(key, default));
            Assert.Equal("saved", Assert.Single(await second.TranscriptStore.LoadAsync(key, default)).Content);
            var names = first.ToolRegistry.ResolveTools(new(ToolProfile.Full, SenderIsOwner: true)).Select(t => t.Descriptor.Name).Order().ToArray();
            Assert.Equal(new[] { "current_time", "request_input", "list_files", "read_file", "write_file", "web_navigate", "web_extract_structured",
                "save_memory", "recall_memory", "forget_memory", "review_runs", "review_run_detail", "create_skill", "send_agent_message", "read_agent_messages", "invoke_agent", "list_agents" }.Order(), names);
        }
        using var reopened = Build();
        Assert.NotNull(await reopened.SessionStore.GetAsync(key, default));
    }

    [Fact]
    public async Task LocalRunPersistsDatabaseProjectionsAndReopensWithSavedPermissions()
    {
        var key = new SessionKey("local-run");
        var caller = CallerIdentity.LocalOwner();
        var policy = new ToolSelectionPolicy(ToolProfile.Minimal);
        RunId runId;
        using (var host = Build())
        {
            var result = await host.Orchestrator.RunAsync(new(key, "Say hello", "local", "rule-based-v1", policy, caller),
                NullAgentEventSink.Instance, default);
            Assert.Equal(AgentRunStatus.Succeeded, result.Status);
            runId = result.RunId;
            Assert.NotEmpty(await host.TranscriptStore.LoadAsync(key, default));
            Assert.NotNull(await host.RunHistoryStore.GetAsync(runId.Value, default));
        }
        using var reopened = Build();
        var saved = (await reopened.WorkflowStore.GetAsync(runId, default))!;
        Assert.Equal(ToolProfile.Minimal, saved.Request.ToolPolicy.Profile);
        Assert.Equal(caller, saved.Request.Caller);
        Assert.NotNull(await reopened.SessionStore.GetAsync(key, default));
    }

    [Fact]
    public async Task WorkflowInspectionAndControlsRemainIndependentOfFailedJsonImport()
    {
        var state = Path.Combine(_testRoot, "state"); Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "session-store.json"), "malformed");
        using var host = Build();
        Assert.Empty(await host.WorkflowStore.ListAsync(default));
        Assert.False(File.Exists(Path.Combine(state, "application.cdb")));
        await Assert.ThrowsAsync<InvalidDataException>(() => host.SessionStore.LoadAllAsync(default));
        Assert.Empty(await host.WorkflowStore.ListAsync(default));
    }

    [Fact]
    public void ResolveWorkflowDatabasePath_NormalizesWorkspaceIdentity()
    {
        var stateRoot = Path.Combine(_testRoot, "state");
        var path = AppBootstrapper.ResolveWorkflowDatabasePath(stateRoot, _testRoot);
        Assert.Equal(path, AppBootstrapper.ResolveWorkflowDatabasePath(stateRoot,
            _testRoot + Path.DirectorySeparatorChar));
        Assert.Equal(path, AppBootstrapper.ResolveWorkflowDatabasePath(stateRoot,
            Path.Combine(_testRoot, ".")));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(path, AppBootstrapper.ResolveWorkflowDatabasePath(stateRoot, _testRoot.ToUpperInvariant()));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
