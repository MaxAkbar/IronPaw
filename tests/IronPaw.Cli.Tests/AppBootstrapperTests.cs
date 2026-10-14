using IronPaw.AgentCore.Abstractions;
using IronPaw.Cli;

namespace IronPaw.Cli.Tests;

public sealed class AppBootstrapperTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "ironpaw-cli-tests", Guid.NewGuid().ToString("N"));

    private AppBootstrap Build(StoreBackend backend = StoreBackend.Json, string? workspace = null) =>
        AppBootstrapper.Build(backend, stateRoot: Path.Combine(_testRoot, "state"),
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

    [Theory]
    [InlineData(StoreBackend.Json)]
    [InlineData(StoreBackend.Sqlite)]
    public async Task Build_UsesCSharpDbWorkflowStoreForEveryConversationBackend(StoreBackend backend)
    {
        using var bootstrap = Build(backend);

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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
