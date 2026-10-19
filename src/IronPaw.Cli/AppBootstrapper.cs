using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Providers;
using IronPaw.AgentCore.Tools;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;

namespace IronPaw.Cli;

internal static class AppBootstrapper
{
    public static AppBootstrap Build(
        int messageTtlDays = 30,
        OpenAiClientOptionOverrides? llmOptions = null,
        string? stateRoot = null,
        string? workspaceRoot = null,
        IronPawSettings? settings = null,
        bool legacyEnvironment = false)
    {
        settings = (settings ?? (stateRoot is null ? IronPawSettingsLoader.Load() : new IronPawSettings())).RequireValid();
        var paths = ResolvePaths(stateRoot, workspaceRoot);
        Directory.CreateDirectory(paths.StateRoot);
        Directory.CreateDirectory(paths.AppSkillsDirectory);

        var services = new ServiceCollection();
        services.AddSingleton(settings);
        llmOptions ??= settings.Llm.ToOverrides();
        ConfigureServices(services, messageTtlDays, llmOptions, paths, legacyEnvironment);

        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        try
        {
            return new AppBootstrap(serviceProvider);
        }
        catch
        {
            serviceProvider.Dispose();
            throw;
        }
    }

    private static BootstrapPaths ResolvePaths(string? stateRoot, string? workspaceRoot)
    {
        workspaceRoot = Path.GetFullPath(workspaceRoot ?? Directory.GetCurrentDirectory());
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IronPaw");
        return new BootstrapPaths(
            WorkspaceRoot: workspaceRoot,
            AppSkillsDirectory: stateRoot is null ? Path.Combine(appDataRoot, "skills") : Path.Combine(Path.GetFullPath(stateRoot), "skills"),
            StateRoot: Path.GetFullPath(stateRoot ?? Path.Combine(appDataRoot, "state")));
    }

    private static void ConfigureServices(
        IServiceCollection services,
        int messageTtlDays,
        OpenAiClientOptionOverrides? llmOptions,
        BootstrapPaths paths, bool legacyEnvironment)
    {
        RegisterStores(services, paths.StateRoot, messageTtlDays);
        services.AddSingleton<IWorkflowStore>(_ => CSharpDbWorkflowStore.OpenAsync(
            ResolveWorkflowDatabasePath(paths.StateRoot, paths.WorkspaceRoot)).GetAwaiter().GetResult());

        var skillLoadResult = SkillAddinLoader.LoadFromDirectories(
        [
            Path.Combine(paths.WorkspaceRoot, "addins", "skills"),
            paths.AppSkillsDirectory
        ]);
        services.AddSingleton(skillLoadResult);
        services.AddSingleton<IWebBrowserService, WebBrowserService>();

        services.AddSingleton<InMemoryToolRegistry>(sp =>
        {
            var builtInTools = BuiltInToolFactory.CreateDefault(
                paths.WorkspaceRoot,
                sp.GetRequiredService<SkillAddinLoadResult>().SkillTools,
                sp.GetRequiredService<IMemoryStore>(),
                sp.GetRequiredService<IRunHistoryStore>(),
                sp.GetRequiredService<IWebBrowserService>());
            return new InMemoryToolRegistry(builtInTools);
        });
        services.AddSingleton<IToolRegistry>(sp => sp.GetRequiredService<InMemoryToolRegistry>());
        services.AddSingleton<IToolRegistrar>(sp => sp.GetRequiredService<InMemoryToolRegistry>());

        services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
        services.AddSingleton<ISessionLaneScheduler, SessionLaneScheduler>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<IToolSchemaNormalizer, ToolSchemaNormalizer>();
        services.AddSingleton<ILlmClient>(_ => new LazyLlmClient(() => LlmClientFactory.Build(llmOptions, readEnvironmentOverrides: legacyEnvironment)));
        services.AddSingleton(sp => new TaskGraphHostController(
            (IAgentTaskGraphStore)sp.GetRequiredService<IWorkflowStore>(),
            sp.GetRequiredService<IWorkflowStore>(), sp.GetRequiredService<ILlmClient>(), paths.WorkspaceRoot,
            Path.Combine(Path.GetDirectoryName(ResolveWorkflowDatabasePath(paths.StateRoot, paths.WorkspaceRoot))!, "task-workspaces")));
        services.AddSingleton<ITranscriptSanitizer, TranscriptSanitizer>();
        services.AddSingleton<IToolResultTruncator, ToolResultTruncator>();
        services.AddSingleton<ISystemPromptBuilder, DefaultSystemPromptBuilder>();

        services.AddSingleton<IResumableAgentOrchestrator>(sp =>
        {
            var orchestrator = new AgentOrchestrator(
                sp.GetRequiredService<ISessionLaneScheduler>(),
                sp.GetRequiredService<ISessionStore>(),
                sp.GetRequiredService<ITranscriptStore>(),
                sp.GetRequiredService<IToolRegistry>(),
                sp.GetRequiredService<IToolExecutor>(),
                sp.GetRequiredService<IToolSchemaNormalizer>(),
                sp.GetRequiredService<ILlmClient>(),
                sp.GetRequiredService<ITranscriptSanitizer>(),
                sp.GetRequiredService<IToolResultTruncator>(),
                sp.GetRequiredService<ISystemPromptBuilder>(),
                sp.GetRequiredService<IMemoryStore>(),
                sp.GetRequiredService<IRunHistoryStore>(),
                workflowStore: sp.GetRequiredService<IWorkflowStore>());

            RegisterRuntimeTools(sp, paths.AppSkillsDirectory, orchestrator, legacyEnvironment);
            return orchestrator;
        });
        services.AddSingleton<IAgentOrchestrator>(sp => sp.GetRequiredService<IResumableAgentOrchestrator>());
    }

    internal static string ResolveWorkflowDatabasePath(string stateRoot, string workspaceRoot)
    {
        var normalizedWorkspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        if (OperatingSystem.IsWindows())
        {
            normalizedWorkspace = normalizedWorkspace.ToUpperInvariant();
        }

        var workspaceId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedWorkspace)));
        return Path.Combine(Path.GetFullPath(stateRoot), "workflows", workspaceId, "workflows.cdb");
    }

    private static void RegisterStores(
        IServiceCollection services,
        string stateRoot,
        int messageTtlDays)
    {
        var store = new CSharpDbApplicationStore(new CSharpDbApplicationStoreOptions(
            Path.Combine(stateRoot, "application.cdb"), stateRoot, MessageTtlDays: messageTtlDays));
        services.AddSingleton<ISessionStore>(store);
        services.AddSingleton<ITranscriptStore>(store);
        services.AddSingleton<IMemoryStore>(store);
        services.AddSingleton<IRunHistoryStore>(store);
        services.AddSingleton<IAgentMessageBus>(sp => new CSharpDbAgentMessageBus(store, sp.GetRequiredService<IAgentRegistry>()));
    }

    private static void RegisterRuntimeTools(IServiceProvider services, string appSkillsDirectory, IAgentOrchestrator orchestrator, bool legacyEnvironment)
    {
        var toolRegistrar = services.GetRequiredService<IToolRegistrar>();
        var agentRegistry = services.GetRequiredService<IAgentRegistry>();
        var messageBus = services.GetRequiredService<IAgentMessageBus>();

        toolRegistrar.RegisterTools([new CreateSkillTool(toolRegistrar, appSkillsDirectory)]);
        DefaultAgentCatalog.Register(agentRegistry, services.GetRequiredService<IronPawSettings>());
        if (legacyEnvironment) ApplyLegacyAgentRouting(agentRegistry, Environment.GetEnvironmentVariable);
        toolRegistrar.RegisterTools([
            new SendAgentMessageTool(messageBus, agentRegistry),
            new ReadAgentMessagesTool(messageBus),
            new InvokeAgentTool(agentRegistry, orchestrator),
            new ListAgentsTool(agentRegistry)
        ]);
    }

    // Saved workflows predate user-file defaults. Preserve their existing environment routing
    // without validating provider configuration while the user is only inspecting saved state.
    internal static void ApplyLegacyAgentRouting(IAgentRegistry registry, Func<string, string?> environment)
    {
        foreach (var (agentId, prefix) in new[] { ("codegen", "IRONPAW_CODEGEN"), ("reviewer", "IRONPAW_REVIEWER"), ("web_browser", "IRONPAW_WEB_BROWSER") })
        {
            string? Value(string name) { var value = environment(name); return string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }
            var agent = registry.Get(agentId) ?? throw new InvalidOperationException("A built-in specialist is missing.");
            registry.Register(agent with { Provider = Value(prefix + "_PROVIDER") ?? agent.Provider, Model = Value(prefix + "_MODEL") ?? agent.Model });
        }
    }

    private sealed record BootstrapPaths(
        string WorkspaceRoot,
        string AppSkillsDirectory,
        string StateRoot);
}

internal sealed class AppBootstrap : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Lazy<TaskGraphHostController> _graphController;

    public AppBootstrap(ServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _graphController = new(() => _serviceProvider.GetRequiredService<TaskGraphHostController>());
        // Force creation during startup so tool registrations run once.
        Orchestrator = _serviceProvider.GetRequiredService<IResumableAgentOrchestrator>();
        WorkflowStore = _serviceProvider.GetRequiredService<IWorkflowStore>();
        LlmClient = _serviceProvider.GetRequiredService<ILlmClient>();
        SkillLoadResult = _serviceProvider.GetRequiredService<SkillAddinLoadResult>();
        MemoryStore = _serviceProvider.GetRequiredService<IMemoryStore>();
        RunHistoryStore = _serviceProvider.GetRequiredService<IRunHistoryStore>();
        SessionStore = _serviceProvider.GetRequiredService<ISessionStore>();
        TranscriptStore = _serviceProvider.GetRequiredService<ITranscriptStore>();
        AgentRegistry = _serviceProvider.GetRequiredService<IAgentRegistry>();
        MessageBus = _serviceProvider.GetRequiredService<IAgentMessageBus>();
        WebBrowserService = _serviceProvider.GetRequiredService<IWebBrowserService>();
    }

    public IResumableAgentOrchestrator Orchestrator { get; }

    public IWorkflowStore WorkflowStore { get; }

    public IronPawSettings Settings => _serviceProvider.GetRequiredService<IronPawSettings>();

    public IAgentTaskGraphStore TaskGraphStore => (IAgentTaskGraphStore)WorkflowStore;

    public ILlmClient LlmClient { get; }

    public TaskGraphHostController GraphController => _graphController.Value;

    public SkillAddinLoadResult SkillLoadResult { get; }

    public IMemoryStore MemoryStore { get; }

    public IRunHistoryStore RunHistoryStore { get; }

    public ISessionStore SessionStore { get; }

    public ITranscriptStore TranscriptStore { get; }

    public IAgentRegistry AgentRegistry { get; }

    public IToolRegistry ToolRegistry => _serviceProvider.GetRequiredService<IToolRegistry>();

    public IAgentMessageBus MessageBus { get; }

    public IWebBrowserService WebBrowserService { get; }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }
}
