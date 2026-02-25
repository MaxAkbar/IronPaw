using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Persistence.Sqlite;
using IronPaw.AgentCore.Providers;
using IronPaw.AgentCore.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace IronPaw.Cli;

internal static class AppBootstrapper
{
    public static AppBootstrap Build(
        StoreBackend storeBackend = StoreBackend.Json,
        int messageTtlDays = 30)
    {
        var paths = ResolvePaths();
        Directory.CreateDirectory(paths.StateRoot);
        Directory.CreateDirectory(paths.AppSkillsDirectory);

        var services = new ServiceCollection();
        ConfigureServices(services, storeBackend, messageTtlDays, paths);

        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        return new AppBootstrap(serviceProvider);
    }

    private static BootstrapPaths ResolvePaths()
    {
        var workspaceRoot = Directory.GetCurrentDirectory();
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IronPaw");
        return new BootstrapPaths(
            WorkspaceRoot: workspaceRoot,
            AppSkillsDirectory: Path.Combine(appDataRoot, "skills"),
            StateRoot: Path.Combine(appDataRoot, "state"));
    }

    private static void ConfigureServices(
        IServiceCollection services,
        StoreBackend storeBackend,
        int messageTtlDays,
        BootstrapPaths paths)
    {
        RegisterStores(services, storeBackend, paths.StateRoot, messageTtlDays);

        var skillLoadResult = SkillAddinLoader.LoadFromDirectories(
        [
            Path.Combine(paths.WorkspaceRoot, "addins", "skills"),
            paths.AppSkillsDirectory
        ]);
        services.AddSingleton(skillLoadResult);

        services.AddSingleton<InMemoryToolRegistry>(sp =>
        {
            var builtInTools = BuiltInToolFactory.CreateDefault(
                paths.WorkspaceRoot,
                sp.GetRequiredService<SkillAddinLoadResult>().SkillTools,
                sp.GetRequiredService<IMemoryStore>(),
                sp.GetRequiredService<IRunHistoryStore>());
            return new InMemoryToolRegistry(builtInTools);
        });
        services.AddSingleton<IToolRegistry>(sp => sp.GetRequiredService<InMemoryToolRegistry>());
        services.AddSingleton<IToolRegistrar>(sp => sp.GetRequiredService<InMemoryToolRegistry>());

        services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
        services.AddSingleton<ISessionLaneScheduler, SessionLaneScheduler>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<IToolSchemaNormalizer, ToolSchemaNormalizer>();
        services.AddSingleton<ILlmClient>(_ => LlmClientFactory.Build());
        services.AddSingleton<ITranscriptSanitizer, TranscriptSanitizer>();
        services.AddSingleton<IToolResultTruncator, ToolResultTruncator>();
        services.AddSingleton<ISystemPromptBuilder, DefaultSystemPromptBuilder>();

        services.AddSingleton<IAgentOrchestrator>(sp =>
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
                sp.GetRequiredService<IRunHistoryStore>());

            RegisterRuntimeTools(sp, paths.AppSkillsDirectory, orchestrator);
            return orchestrator;
        });
    }

    private static void RegisterStores(
        IServiceCollection services,
        StoreBackend storeBackend,
        string stateRoot,
        int messageTtlDays)
    {
        switch (storeBackend)
        {
            case StoreBackend.Sqlite:
                RegisterSqliteStores(services, stateRoot, messageTtlDays);
                break;
            default:
                RegisterJsonStores(services, stateRoot);
                break;
        }
    }

    private static void RegisterJsonStores(IServiceCollection services, string stateRoot)
    {
        var sessionsDirectory = Path.Combine(stateRoot, "sessions");
        Directory.CreateDirectory(sessionsDirectory);

        IMemoryStore memoryStore = new FileMemoryStore(new FileMemoryStoreOptions(
            GlobalMemoryPath: Path.Combine(stateRoot, "memories.json"),
            SessionMemoriesDirectory: sessionsDirectory));
        IRunHistoryStore runHistoryStore = new JsonRunHistoryStore(Path.Combine(stateRoot, "run-history.jsonl"));
        ISessionStore sessionStore = new FileSessionStore(new FileSessionStoreOptions(
            StoreFilePath: Path.Combine(stateRoot, "session-store.json"),
            CacheTtl: TimeSpan.FromSeconds(5)));
        ITranscriptStore transcriptStore = new JsonTranscriptStore(sessionsDirectory);

        services.AddSingleton(memoryStore);
        services.AddSingleton(runHistoryStore);
        services.AddSingleton(sessionStore);
        services.AddSingleton(transcriptStore);
        services.AddSingleton<IAgentMessageBus, InMemoryAgentMessageBus>();
    }

    private static void RegisterSqliteStores(IServiceCollection services, string stateRoot, int messageTtlDays)
    {
        var dbPath = Path.Combine(stateRoot, "ironpaw.db");
        var connectionString = SqliteSchemaInitializer.BuildConnectionString(dbPath);
        SqliteSchemaInitializer.Initialize(connectionString);

        var options = new SqliteStoreOptions(dbPath, MessageTtlDays: messageTtlDays);
        IMemoryStore memoryStore = new SqliteMemoryStore(options);
        IRunHistoryStore runHistoryStore = new SqliteRunHistoryStore(options);
        ISessionStore sessionStore = new SqliteSessionStore(options);
        ITranscriptStore transcriptStore = new SqliteTranscriptStore(options);

        services.AddSingleton(memoryStore);
        services.AddSingleton(runHistoryStore);
        services.AddSingleton(sessionStore);
        services.AddSingleton(transcriptStore);
        services.AddSingleton<IAgentMessageBus>(sp =>
            new SqliteAgentMessageBus(options, sp.GetRequiredService<IAgentRegistry>()));
    }

    private static void RegisterRuntimeTools(IServiceProvider services, string appSkillsDirectory, IAgentOrchestrator orchestrator)
    {
        var toolRegistrar = services.GetRequiredService<IToolRegistrar>();
        var agentRegistry = services.GetRequiredService<IAgentRegistry>();
        var messageBus = services.GetRequiredService<IAgentMessageBus>();

        toolRegistrar.RegisterTools([new CreateSkillTool(toolRegistrar, appSkillsDirectory)]);
        RegisterDefaultAgents(agentRegistry);
        toolRegistrar.RegisterTools([
            new SendAgentMessageTool(messageBus, agentRegistry),
            new ReadAgentMessagesTool(messageBus),
            new InvokeAgentTool(agentRegistry, orchestrator),
            new ListAgentsTool(agentRegistry)
        ]);
    }

    private static void RegisterDefaultAgents(IAgentRegistry registry)
    {
        var codegenProvider = ReadOptionalEnv("IRONPAW_CODEGEN_PROVIDER");
        var codegenModel = ReadOptionalEnv("IRONPAW_CODEGEN_MODEL");
        var reviewerProvider = ReadOptionalEnv("IRONPAW_REVIEWER_PROVIDER");
        var reviewerModel = ReadOptionalEnv("IRONPAW_REVIEWER_MODEL");

        registry.Register(new AgentDefinition(
            AgentId: "codegen",
            DisplayName: "Code Generator",
            Description: "Implements requested code changes with concise implementation notes.",
            SystemPrompt: """
                          You are a specialist coding agent.
                          Implement the delegated task directly in the workspace using available tools.
                          Prefer minimal, correct changes and keep explanations short.
                          If requirements are ambiguous, state assumptions briefly in the final answer.
                          """,
            ToolPolicy: new ToolSelectionPolicy(
                Profile: ToolProfile.Full,
                DenyList: new[] { "invoke_agent", "list_agents", "create_skill" }),
            Provider: codegenProvider,
            Model: codegenModel,
            MaxRounds: 6,
            MaxOutputTokens: 4096,
            AllowedMessageSenders: new[] { "codegen", "reviewer" }));

        registry.Register(new AgentDefinition(
            AgentId: "reviewer",
            DisplayName: "Code Reviewer",
            Description: "Reviews changes for correctness, regressions, and missing tests.",
            SystemPrompt: """
                          You are a specialist code review agent.
                          Focus on bugs, behavioral regressions, risks, and missing tests.
                          Report findings first, ordered by severity, with file references.
                          If no issues are found, say so explicitly and note residual risks.
                          """,
            ToolPolicy: new ToolSelectionPolicy(
                Profile: ToolProfile.Full,
                DenyList: new[] { "invoke_agent", "list_agents", "create_skill" }),
            Provider: reviewerProvider,
            Model: reviewerModel,
            MaxRounds: 4,
            MaxOutputTokens: 3072,
            AllowedMessageSenders: new[] { "codegen", "reviewer" }));
    }

    private static string? ReadOptionalEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record BootstrapPaths(
        string WorkspaceRoot,
        string AppSkillsDirectory,
        string StateRoot);
}

internal sealed class AppBootstrap : IDisposable
{
    private readonly ServiceProvider _serviceProvider;

    public AppBootstrap(ServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        // Force creation during startup so tool registrations run once.
        Orchestrator = _serviceProvider.GetRequiredService<IAgentOrchestrator>();
        SkillLoadResult = _serviceProvider.GetRequiredService<SkillAddinLoadResult>();
        MemoryStore = _serviceProvider.GetRequiredService<IMemoryStore>();
        RunHistoryStore = _serviceProvider.GetRequiredService<IRunHistoryStore>();
        SessionStore = _serviceProvider.GetRequiredService<ISessionStore>();
        TranscriptStore = _serviceProvider.GetRequiredService<ITranscriptStore>();
        AgentRegistry = _serviceProvider.GetRequiredService<IAgentRegistry>();
        MessageBus = _serviceProvider.GetRequiredService<IAgentMessageBus>();
    }

    public IAgentOrchestrator Orchestrator { get; }

    public SkillAddinLoadResult SkillLoadResult { get; }

    public IMemoryStore MemoryStore { get; }

    public IRunHistoryStore RunHistoryStore { get; }

    public ISessionStore SessionStore { get; }

    public ITranscriptStore TranscriptStore { get; }

    public IAgentRegistry AgentRegistry { get; }

    public IAgentMessageBus MessageBus { get; }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }
}
