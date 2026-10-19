using IronPaw.AgentCore.Abstractions;
using IronPaw.Cli;

namespace IronPaw.Cli.Tests;

[TestClass]
public sealed class AppBootstrapperTests
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "ironpaw-cli-tests", Guid.NewGuid().ToString("N"));

    private AppBootstrap Build(string? workspace = null) =>
        AppBootstrapper.Build(stateRoot: Path.Combine(_testRoot, "state"),
            workspaceRoot: workspace ?? _testRoot);

    [TestMethod]
    public void Build_RegistersWebBrowserSpecialistAgent()
    {
        using var bootstrap = Build();

        var webBrowser = bootstrap.AgentRegistry.Get("web_browser");

        Assert.IsNotNull(webBrowser);
        Assert.AreEqual(ToolProfile.Web, webBrowser!.ToolPolicy.Profile);
        Assert.Contains("web pages", webBrowser.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("web_navigate", webBrowser.ToolPolicy.AllowList ?? []);
        Assert.Contains("web_extract_structured", webBrowser.ToolPolicy.AllowList ?? []);
        Assert.IsNotNull(bootstrap.WebBrowserService);
    }

    [TestMethod]
    public void Build_RestrictsCodegenAndReviewerFromDirectWebTools()
    {
        using var bootstrap = Build();

        var codegen = bootstrap.AgentRegistry.Get("codegen");
        var reviewer = bootstrap.AgentRegistry.Get("reviewer");

        Assert.IsNotNull(codegen);
        Assert.IsNotNull(reviewer);
        Assert.Contains("web_navigate", codegen!.ToolPolicy.DenyList ?? []);
        Assert.Contains("web_extract_structured", codegen.ToolPolicy.DenyList ?? []);
        Assert.Contains("web_navigate", reviewer!.ToolPolicy.DenyList ?? []);
        Assert.Contains("web_extract_structured", reviewer.ToolPolicy.DenyList ?? []);
        Assert.Contains("web_browser", codegen.AllowedMessageSenders ?? []);
        Assert.Contains("web_browser", reviewer.AllowedMessageSenders ?? []);
        var tools = IronPaw.AgentCore.Tools.BuiltInToolFactory.CreateDefault(Path.GetTempPath());
        var available = new IronPaw.AgentCore.Tools.InMemoryToolRegistry(tools).ResolveTools(
            reviewer.ToolPolicy with { SenderIsOwner = true });
        Assert.Contains(tool => tool.Descriptor.Name == "read_file", available);
        Assert.DoesNotContain(tool => tool.Descriptor.Name == "write_file", available);
    }

    [TestMethod]
    public async Task Build_UsesCSharpDbWorkflowStore()
    {
        using var bootstrap = Build();

        Assert.IsExactInstanceOfType<IronPaw.AgentCore.Persistence.CSharpDb.CSharpDbWorkflowStore>(bootstrap.WorkflowStore);
        Assert.IsInstanceOfType<IResumableAgentOrchestrator>(bootstrap.Orchestrator);
        Assert.IsEmpty(await bootstrap.WorkflowStore.ListAsync(CancellationToken.None));
        Assert.IsTrue(File.Exists(AppBootstrapper.ResolveWorkflowDatabasePath(Path.Combine(_testRoot, "state"), _testRoot)));
    }

    [TestMethod]
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
            Assert.IsNull(await second.WorkflowStore.GetAsync(runId, CancellationToken.None));
        }

        using var reopened = Build(workspace: firstWorkspace);
        Assert.AreEqual(runId.Value, (await reopened.WorkflowStore.GetAsync(runId, CancellationToken.None))!.RunId);
    }

    [TestMethod]
    public async Task Build_TaskGraphsShareTheWorkflowLedgerAndUseManagedWorkspaceRoot()
    {
        var workspace = Path.Combine(_testRoot, "workspace");
        Directory.CreateDirectory(workspace);
        using var bootstrap = Build(workspace: workspace);
        Assert.AreSame<object>(bootstrap.WorkflowStore, bootstrap.TaskGraphStore);
        Assert.IsExactInstanceOfType<LazyLlmClient>(bootstrap.LlmClient);
        Assert.AreEqual(workspace, bootstrap.GraphController.WorkspaceRoot);
        var database = AppBootstrapper.ResolveWorkflowDatabasePath(Path.Combine(_testRoot, "state"), workspace);
        Assert.AreEqual(Path.Combine(Path.GetDirectoryName(database)!, "task-workspaces"), bootstrap.GraphController.WorkspaceStoreRoot);
        Assert.IsEmpty(await bootstrap.GraphController.ListAsync(CallerIdentity.LocalOwner(), CancellationToken.None));
    }

    [TestMethod]
    public async Task ApplicationStoresPersistAcrossHostsWithoutChangingToolCatalog()
    {
        var key = new SessionKey("host-storage");
        using (var first = Build(workspace: Path.Combine(_testRoot, "first")))
        using (var second = Build(workspace: Path.Combine(_testRoot, "second")))
        {
            Assert.IsExactInstanceOfType<IronPaw.AgentCore.Persistence.CSharpDb.CSharpDbApplicationStore>(first.SessionStore);
            Assert.AreSame<object>(first.SessionStore, first.TranscriptStore);
            Assert.AreSame<object>(first.SessionStore, first.MemoryStore);
            Assert.AreSame<object>(first.SessionStore, first.RunHistoryStore);
            Assert.IsExactInstanceOfType<IronPaw.AgentCore.Persistence.CSharpDb.CSharpDbAgentMessageBus>(first.MessageBus);
            await first.SessionStore.UpsertAsync(key, _ => new(key.Value, DateTimeOffset.UtcNow), default);
            await first.TranscriptStore.AppendAsync(key, [new(ChatRole.User, "saved")], default);
            Assert.IsNotNull(await second.SessionStore.GetAsync(key, default));
            Assert.AreEqual("saved", Assert.ContainsSingle(await second.TranscriptStore.LoadAsync(key, default)).Content);
            var names = first.ToolRegistry.ResolveTools(new(ToolProfile.Full, SenderIsOwner: true)).Select(t => t.Descriptor.Name).Order().ToArray();
            Assert.AreSequenceEqual(new[] { "current_time", "request_input", "list_files", "read_file", "write_file", "web_navigate", "web_extract_structured",
                "save_memory", "recall_memory", "forget_memory", "review_runs", "review_run_detail", "create_skill", "send_agent_message", "read_agent_messages", "invoke_agent", "list_agents" }.Order(), names);
        }
        using var reopened = Build();
        Assert.IsNotNull(await reopened.SessionStore.GetAsync(key, default));
    }

    [TestMethod]
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
            Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
            runId = result.RunId;
            Assert.IsNotEmpty(await host.TranscriptStore.LoadAsync(key, default));
            Assert.IsNotNull(await host.RunHistoryStore.GetAsync(runId.Value, default));
        }
        using var reopened = Build();
        var saved = (await reopened.WorkflowStore.GetAsync(runId, default))!;
        Assert.AreEqual(ToolProfile.Minimal, saved.Request.ToolPolicy.Profile);
        Assert.AreEqual(caller, saved.Request.Caller);
        Assert.IsNotNull(await reopened.SessionStore.GetAsync(key, default));
    }

    [TestMethod]
    public async Task WorkflowInspectionAndControlsRemainIndependentOfFailedJsonImport()
    {
        var state = Path.Combine(_testRoot, "state"); Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "session-store.json"), "malformed");
        using var host = Build();
        Assert.IsEmpty(await host.WorkflowStore.ListAsync(default));
        Assert.IsFalse(File.Exists(Path.Combine(state, "application.cdb")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => host.SessionStore.LoadAllAsync(default));
        Assert.IsEmpty(await host.WorkflowStore.ListAsync(default));
    }

    [TestMethod]
    public void ResolveWorkflowDatabasePath_NormalizesWorkspaceIdentity()
    {
        var stateRoot = Path.Combine(_testRoot, "state");
        var path = AppBootstrapper.ResolveWorkflowDatabasePath(stateRoot, _testRoot);
        Assert.AreEqual(path, AppBootstrapper.ResolveWorkflowDatabasePath(stateRoot,
            _testRoot + Path.DirectorySeparatorChar));
        Assert.AreEqual(path, AppBootstrapper.ResolveWorkflowDatabasePath(stateRoot,
            Path.Combine(_testRoot, ".")));
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(path, AppBootstrapper.ResolveWorkflowDatabasePath(stateRoot, _testRoot.ToUpperInvariant()));
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
