using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Providers;
using IronPaw.AgentCore.Tools;

namespace IronPaw.Cli;

internal static class AppBootstrapper
{
    public static (IAgentOrchestrator Orchestrator, SkillAddinLoadResult SkillLoadResult, IMemoryStore MemoryStore, IRunHistoryStore RunHistoryStore) Build()
    {
        var workspaceRoot = Directory.GetCurrentDirectory();
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IronPaw");
        var appSkillsDirectory = Path.Combine(appData, "skills");
        var stateRoot = Path.Combine(appData, "state");
        var sessionsDirectory = Path.Combine(stateRoot, "sessions");
        var sessionStorePath = Path.Combine(stateRoot, "session-store.json");

        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(sessionsDirectory);
        Directory.CreateDirectory(appSkillsDirectory);

        var memoryStore = new FileMemoryStore(new FileMemoryStoreOptions(
            GlobalMemoryPath: Path.Combine(stateRoot, "memories.json"),
            SessionMemoriesDirectory: sessionsDirectory));
        var runHistoryStore = new JsonRunHistoryStore(Path.Combine(stateRoot, "run-history.jsonl"));

        var skillLoadResult = SkillAddinLoader.LoadFromDirectories(
        [
            Path.Combine(workspaceRoot, "addins", "skills"),
            appSkillsDirectory
        ]);
        var tools = BuiltInToolFactory.CreateDefault(workspaceRoot, skillLoadResult.SkillTools, memoryStore, runHistoryStore);
        var toolRegistry = new InMemoryToolRegistry(tools);
        toolRegistry.RegisterTools([new CreateSkillTool(toolRegistry, appSkillsDirectory)]);
        var sessionStore = new FileSessionStore(new FileSessionStoreOptions(
            StoreFilePath: sessionStorePath,
            CacheTtl: TimeSpan.FromSeconds(5)));
        var transcriptStore = new JsonTranscriptStore(sessionsDirectory);
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

        return (orchestrator, skillLoadResult, memoryStore, runHistoryStore);
    }
}
