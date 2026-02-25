using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Providers;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

public sealed class ToolObservabilityEventTests
{
    [Fact]
    public async Task RunAsync_ToolDetailsEmitMessageObservabilityEvents()
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

            var toolRegistry = new InMemoryToolRegistry([new ObservabilityTool()]);
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
                llmClient: new ObservabilityLlmClient(),
                transcriptSanitizer: new TranscriptSanitizer(),
                toolResultTruncator: new ToolResultTruncator(),
                promptBuilder: new DefaultSystemPromptBuilder());

            var sink = new CapturingEventSink();
            var request = new AgentRunRequest(
                SessionKey: new SessionKey("observability"),
                UserInput: "Run observability check",
                Provider: "local",
                Model: "rule-based-v1",
                ToolPolicy: new ToolSelectionPolicy(ToolProfile.Minimal),
                Caller: CallerIdentity.LocalOwner(),
                AgentId: "codegen",
                MaxRounds: 3);

            var result = await orchestrator.RunAsync(request, sink, CancellationToken.None);

            Assert.Equal("All done.", result.FinalAssistantMessage);
            Assert.Contains(sink.Events, evt => evt.Type == "message.sent");
            Assert.Contains(sink.Events, evt => evt.Type == "message.received");

            var sent = sink.Events.First(evt => evt.Type == "message.sent");
            Assert.Equal("codegen", sent.AgentId);
            Assert.Equal("reviewer", sent.Data["recipient_agent_id"]?.ToString());

            var received = sink.Events.First(evt => evt.Type == "message.received");
            Assert.Equal("2", received.Data["message_count"]?.ToString());
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

    private sealed class ObservabilityLlmClient : ILlmClient
    {
        private int _round;

        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            _round++;
            if (_round == 1)
            {
                yield return new ToolCallDelta(
                    "obs-call-1",
                    "emit_observability",
                    JsonSerializer.SerializeToElement(new
                    {
                        event_type = "message.sent",
                        recipient_agent_id = "reviewer",
                        message_count = 1
                    }));
                yield return new ToolCallDelta(
                    "obs-call-2",
                    "emit_observability",
                    JsonSerializer.SerializeToElement(new
                    {
                        event_type = "message.received",
                        recipient_agent_id = "codegen",
                        message_count = 2
                    }));
                yield return new CompletedDelta();
                yield break;
            }

            yield return new AssistantTextDelta("All done.");
            yield return new CompletedDelta();
            await Task.CompletedTask;
        }
    }

    private sealed class ObservabilityTool : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(
            Name: "emit_observability",
            Description: "Emits synthetic observability metadata for testing.",
            ParametersSchema: JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    event_type = new { type = "string" },
                    recipient_agent_id = new { type = "string" },
                    message_count = new { type = "integer" }
                },
                required = new[] { "event_type", "recipient_agent_id", "message_count" }
            }),
            Group: "group:system");

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
        {
            var eventType = call.Arguments.GetProperty("event_type").GetString();
            var recipient = call.Arguments.GetProperty("recipient_agent_id").GetString();
            var count = call.Arguments.GetProperty("message_count").GetInt32();

            return Task.FromResult(
                ToolResult.Success(
                    "ok",
                    details: new Dictionary<string, object?>
                    {
                        ["observability_event_type"] = eventType,
                        ["recipient_agent_id"] = recipient,
                        ["message_count"] = count
                    }));
        }
    }
}
