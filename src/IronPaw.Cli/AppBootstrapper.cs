using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Persistence.Sqlite;
using IronPaw.AgentCore.Providers;
using IronPaw.AgentCore.Tools;

namespace IronPaw.Cli;

internal static class AppBootstrapper
{
    public static (IAgentOrchestrator Orchestrator, SkillAddinLoadResult SkillLoadResult, IMemoryStore MemoryStore, IRunHistoryStore RunHistoryStore, ISessionStore SessionStore, ITranscriptStore TranscriptStore) Build(StoreBackend storeBackend = StoreBackend.Json)
    {
        var workspaceRoot = Directory.GetCurrentDirectory();
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IronPaw");
        var appSkillsDirectory = Path.Combine(appData, "skills");
        var stateRoot = Path.Combine(appData, "state");

        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(appSkillsDirectory);

        var (memoryStore, runHistoryStore, sessionStore, transcriptStore) = storeBackend switch
        {
            StoreBackend.Sqlite => BuildSqliteStores(stateRoot),
            _ => BuildJsonStores(stateRoot)
        };

        var skillLoadResult = SkillAddinLoader.LoadFromDirectories(
        [
            Path.Combine(workspaceRoot, "addins", "skills"),
            appSkillsDirectory
        ]);
        var tools = BuiltInToolFactory.CreateDefault(workspaceRoot, skillLoadResult.SkillTools, memoryStore, runHistoryStore);
        var toolRegistry = new InMemoryToolRegistry(tools);
        toolRegistry.RegisterTools([new CreateSkillTool(toolRegistry, appSkillsDirectory)]);
        var laneScheduler = new SessionLaneScheduler();
        var toolExecutor = new ToolExecutor();
        var schemaNormalizer = new ToolSchemaNormalizer();
        var llmClient = LlmClientFactory.Build();
        var sanitizer = new TranscriptSanitizer();
        var truncator = new ToolResultTruncator();
        var promptBuilder = new DefaultSystemPromptBuilder();

        var orchestrator = new AgentOrchestrator(
            laneScheduler,
            sessionStore,
            transcriptStore,
            toolRegistry,
            toolExecutor,
            schemaNormalizer,
            llmClient,
            sanitizer,
            truncator,
            promptBuilder,
            memoryStore,
            runHistoryStore);

        return (orchestrator, skillLoadResult, memoryStore, runHistoryStore, sessionStore, transcriptStore);
    }

    private static (IMemoryStore, IRunHistoryStore, ISessionStore, ITranscriptStore) BuildJsonStores(string stateRoot)
    {
        var sessionsDirectory = Path.Combine(stateRoot, "sessions");
        Directory.CreateDirectory(sessionsDirectory);

        var memoryStore = new FileMemoryStore(new FileMemoryStoreOptions(
            GlobalMemoryPath: Path.Combine(stateRoot, "memories.json"),
            SessionMemoriesDirectory: sessionsDirectory));
        var runHistoryStore = new JsonRunHistoryStore(Path.Combine(stateRoot, "run-history.jsonl"));
        var sessionStore = new FileSessionStore(new FileSessionStoreOptions(
            StoreFilePath: Path.Combine(stateRoot, "session-store.json"),
            CacheTtl: TimeSpan.FromSeconds(5)));
        var transcriptStore = new JsonTranscriptStore(sessionsDirectory);

        return (memoryStore, runHistoryStore, sessionStore, transcriptStore);
    }

    private static (IMemoryStore, IRunHistoryStore, ISessionStore, ITranscriptStore) BuildSqliteStores(string stateRoot)
    {
        var dbPath = Path.Combine(stateRoot, "ironpaw.db");
        var connectionString = SqliteSchemaInitializer.BuildConnectionString(dbPath);
        SqliteSchemaInitializer.Initialize(connectionString);

        var options = new SqliteStoreOptions(dbPath);
        IMemoryStore memoryStore = new SqliteMemoryStore(options);
        IRunHistoryStore runHistoryStore = new SqliteRunHistoryStore(options);
        ISessionStore sessionStore = new SqliteSessionStore(options);
        ITranscriptStore transcriptStore = new SqliteTranscriptStore(options);

        return (memoryStore, runHistoryStore, sessionStore, transcriptStore);
    }
}
