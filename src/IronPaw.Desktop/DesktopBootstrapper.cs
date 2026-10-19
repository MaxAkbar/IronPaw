using System.IO;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Providers;
using IronPaw.AgentCore.Tools;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;

namespace IronPaw.Desktop;

internal static class DesktopBootstrapper
{
    public static DesktopBootstrap Build(string? stateRoot = null, string? workspaceRoot = null, IronPawSettings? settings = null)
    {
        settings = (settings ?? (stateRoot is null ? IronPawSettingsLoader.Load() : new IronPawSettings())).RequireValid();
        var paths = ResolvePaths(stateRoot, workspaceRoot);
        Directory.CreateDirectory(paths.StateRoot);
        Directory.CreateDirectory(paths.AppSkillsDirectory);

        var services = new ServiceCollection();
        services.AddSingleton(settings);
        ConfigureServices(services, paths);

        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        try
        {
            return new DesktopBootstrap(serviceProvider);
        }
        catch
        {
            serviceProvider.Dispose();
            throw;
        }
    }

    private static DesktopPaths ResolvePaths(string? stateRoot, string? workspaceRoot)
    {
        workspaceRoot = Path.GetFullPath(workspaceRoot ?? Directory.GetCurrentDirectory());
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IronPaw");
        var appSkillsDirectory = stateRoot is null ? Path.Combine(appDataRoot, "skills") : Path.Combine(Path.GetFullPath(stateRoot), "skills");
        stateRoot = Path.GetFullPath(stateRoot ?? Path.Combine(appDataRoot, "state"));
        return new DesktopPaths(
            WorkspaceRoot: workspaceRoot,
            AppSkillsDirectory: appSkillsDirectory,
            StateRoot: stateRoot);
    }

    private static void ConfigureServices(IServiceCollection services, DesktopPaths paths)
    {
        var skillLoadResult = SkillAddinLoader.LoadFromDirectories(
        [
            Path.Combine(paths.WorkspaceRoot, "addins", "skills"),
            paths.AppSkillsDirectory
        ]);
        services.AddSingleton(skillLoadResult);
        services.AddSingleton<IWebBrowserService, WebBrowserService>();

        var applicationStore = new CSharpDbApplicationStore(new CSharpDbApplicationStoreOptions(
            Path.Combine(paths.StateRoot, "application.cdb"), paths.StateRoot));
        services.AddSingleton<ISessionStore>(applicationStore);
        services.AddSingleton<ITranscriptStore>(applicationStore);
        services.AddSingleton<IMemoryStore>(applicationStore);
        services.AddSingleton<IRunHistoryStore>(applicationStore);
        // CSharpDB startup may await library operations that capture their caller's context.
        // Resolve it off the dispatcher so synchronous DI construction cannot deadlock WPF.
        services.AddSingleton<IWorkflowStore>(_ => Task.Run(() => CSharpDbWorkflowStore.OpenAsync(
            ResolveWorkflowDatabasePath(paths.StateRoot, paths.WorkspaceRoot))).GetAwaiter().GetResult());

        services.AddSingleton<InMemoryToolRegistry>(sp =>
        {
            var tools = BuiltInToolFactory.CreateDefault(
                paths.WorkspaceRoot,
                sp.GetRequiredService<SkillAddinLoadResult>().SkillTools,
                runHistoryStore: sp.GetRequiredService<IRunHistoryStore>(),
                webBrowserService: sp.GetRequiredService<IWebBrowserService>());
            return new InMemoryToolRegistry(tools);
        });
        services.AddSingleton<IToolRegistry>(sp => sp.GetRequiredService<InMemoryToolRegistry>());
        services.AddSingleton<IToolRegistrar>(sp => sp.GetRequiredService<InMemoryToolRegistry>());

        services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
        services.AddSingleton<IAgentMessageBus>(sp => new CSharpDbAgentMessageBus(applicationStore, sp.GetRequiredService<IAgentRegistry>()));
        services.AddSingleton<ISessionLaneScheduler, SessionLaneScheduler>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<IToolSchemaNormalizer, ToolSchemaNormalizer>();
        // Browsing saved state and recording control/reconciliation receipts do not
        // require provider configuration. Resolve that configuration on first model use.
        services.AddSingleton<ILlmClient>(sp => new DeferredDesktopLlmClient(() => LlmClientFactory.Build(
            sp.GetRequiredService<IronPawSettings>().Llm.ToOverrides(), readEnvironmentOverrides: false)));
        services.AddSingleton(sp => new TaskGraphHostController(
            (IAgentTaskGraphStore)sp.GetRequiredService<IWorkflowStore>(),
            sp.GetRequiredService<IWorkflowStore>(), sp.GetRequiredService<ILlmClient>(),
            paths.WorkspaceRoot,
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
                runHistoryStore: sp.GetRequiredService<IRunHistoryStore>(),
                workflowStore: sp.GetRequiredService<IWorkflowStore>());

            RegisterRuntimeTools(sp, paths.AppSkillsDirectory, orchestrator);
            return orchestrator;
        });
        services.AddSingleton<IAgentOrchestrator>(sp => sp.GetRequiredService<IResumableAgentOrchestrator>());
    }

    private static string ResolveWorkflowDatabasePath(string stateRoot, string workspaceRoot)
    {
        var normalizedWorkspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        if (OperatingSystem.IsWindows())
        {
            normalizedWorkspace = normalizedWorkspace.ToUpperInvariant();
        }

        var workspaceId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedWorkspace)));
        return Path.Combine(stateRoot, "workflows", workspaceId, "workflows.cdb");
    }

    private static void RegisterRuntimeTools(IServiceProvider services, string appSkillsDirectory, IAgentOrchestrator orchestrator)
    {
        var toolRegistrar = services.GetRequiredService<IToolRegistrar>();
        var agentRegistry = services.GetRequiredService<IAgentRegistry>();
        var messageBus = services.GetRequiredService<IAgentMessageBus>();

        toolRegistrar.RegisterTools([new CreateSkillTool(toolRegistrar, appSkillsDirectory)]);
        DefaultAgentCatalog.Register(agentRegistry, services.GetRequiredService<IronPawSettings>());
        toolRegistrar.RegisterTools([
            new SendAgentMessageTool(messageBus, agentRegistry),
            new ReadAgentMessagesTool(messageBus),
            new InvokeAgentTool(agentRegistry, orchestrator),
            new ListAgentsTool(agentRegistry)
        ]);
    }

    private sealed record DesktopPaths(
        string WorkspaceRoot,
        string AppSkillsDirectory,
        string StateRoot);
}

internal sealed class DesktopBootstrap : IDisposable
{
    private readonly ServiceProvider _serviceProvider;

    public DesktopBootstrap(ServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        // Ensure runtime tools are registered at startup.
        Orchestrator = _serviceProvider.GetRequiredService<IResumableAgentOrchestrator>();
        SkillLoadResult = _serviceProvider.GetRequiredService<SkillAddinLoadResult>();
    }

    public IResumableAgentOrchestrator Orchestrator { get; }

    public SkillAddinLoadResult SkillLoadResult { get; }

    public IWorkflowStore WorkflowStore => _serviceProvider.GetRequiredService<IWorkflowStore>();

    public ISessionStore SessionStore => _serviceProvider.GetRequiredService<ISessionStore>();
    public ITranscriptStore TranscriptStore => _serviceProvider.GetRequiredService<ITranscriptStore>();
    public IMemoryStore MemoryStore => _serviceProvider.GetRequiredService<IMemoryStore>();
    public IRunHistoryStore RunHistoryStore => _serviceProvider.GetRequiredService<IRunHistoryStore>();
    public IAgentMessageBus MessageBus => _serviceProvider.GetRequiredService<IAgentMessageBus>();
    public IToolRegistry ToolRegistry => _serviceProvider.GetRequiredService<IToolRegistry>();

    public IronPawSettings Settings => _serviceProvider.GetService<IronPawSettings>() ?? new();

    // Optional for lightweight window probes that register only the normal workflow host.
    public TaskGraphHostController? Graphs => _serviceProvider.GetService<TaskGraphHostController>();

    public void Dispose()
    {
        // Mirror the startup boundary when CSharpDB drains asynchronous storage work.
        Task.Run(_serviceProvider.Dispose).GetAwaiter().GetResult();
    }
}

internal sealed class DeferredDesktopLlmClient(Func<ILlmClient> factory) : ILlmClient
{
    private readonly Lazy<ILlmClient> _client = new(factory);
    public IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Enforce routing at dispatch as well as graph preflight. A stale scheduling
        // hint must never let an unknown provider fall back to the local model.
        if (!ProviderDefaults.DefaultModels.Keys.Contains(request.Provider, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported task graph provider '{request.Provider}'.", nameof(request));
        return _client.Value.StreamAsync(request, ct);
    }
}
