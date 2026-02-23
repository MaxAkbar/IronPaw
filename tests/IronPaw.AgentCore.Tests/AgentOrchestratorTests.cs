using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Providers;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

public sealed class AgentOrchestratorTests
{
    [Fact]
    public async Task RunAsync_LocalProviderPlanPrompt_WritesPlanAndEmitsToolEvents()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspaceRoot = Path.Combine(root, "workspace");
            var stateRoot = Path.Combine(root, "state");
            var sessionsDirectory = Path.Combine(stateRoot, "sessions");
            var sessionStorePath = Path.Combine(stateRoot, "session-store.json");

            Directory.CreateDirectory(workspaceRoot);
            Directory.CreateDirectory(sessionsDirectory);

            var tools = BuiltInToolFactory.CreateDefault(workspaceRoot);
            var toolRegistry = new InMemoryToolRegistry(tools);
            var sessionStore = new FileSessionStore(new FileSessionStoreOptions(
                StoreFilePath: sessionStorePath,
                CacheTtl: TimeSpan.FromSeconds(1)));
            var transcriptStore = new JsonTranscriptStore(sessionsDirectory);
            var orchestrator = new AgentOrchestrator(
                laneScheduler: new SessionLaneScheduler(),
                sessionStore: sessionStore,
                transcriptStore: transcriptStore,
                toolRegistry: toolRegistry,
                toolExecutor: new ToolExecutor(),
                schemaNormalizer: new ToolSchemaNormalizer(),
                llmClient: new RuleBasedLlmClient(),
                transcriptSanitizer: new TranscriptSanitizer(),
                toolResultTruncator: new ToolResultTruncator(),
                promptBuilder: new DefaultSystemPromptBuilder());

            var sessionKey = new SessionKey("integration-run");
            var request = new AgentRunRequest(
                SessionKey: sessionKey,
                UserInput: "Create a project plan and write it to Plan.md",
                Provider: "local",
                Model: "rule-based-v1",
                ToolPolicy: new ToolSelectionPolicy(ToolProfile.Coding),
                Caller: CallerIdentity.LocalOwner());
            var sink = new CapturingEventSink();

            var result = await orchestrator.RunAsync(request, sink, CancellationToken.None);

            Assert.Contains("Tool execution completed", result.FinalAssistantMessage, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(workspaceRoot, "Plan.md")));
            Assert.Contains(sink.Events, evt => evt.Type == "tool_call");
            Assert.Contains(sink.Events, evt => evt.Type == "tool_result");

            var savedSession = await sessionStore.GetAsync(sessionKey, CancellationToken.None);
            Assert.NotNull(savedSession);
            Assert.True(savedSession!.TotalTokens > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CapturingEventSink : IAgentEventSink
    {
        public List<AgentEvent> Events { get; } = [];

        public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct)
        {
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }
    }
}
