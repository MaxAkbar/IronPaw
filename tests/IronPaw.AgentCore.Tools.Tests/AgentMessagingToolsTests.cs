using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tools.Tests;

[TestClass]
public sealed class AgentMessagingToolsTests
{
    [TestMethod]
    public async Task SendAgentMessage_WithoutAgentContext_ReturnsError()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgent("reviewer", ["codegen"]));

        var bus = new StubMessageBus();
        var tool = new SendAgentMessageTool(bus, registry);
        var call = CreateCall(
            "send_agent_message",
            new
            {
                recipient_agent_id = "reviewer",
                message = "hello"
            });
        var context = CreateContext(agentId: null);

        var result = await tool.ExecuteAsync(call, context, CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.Contains("named agent context", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(bus.SendCalled);
    }

    [TestMethod]
    public async Task SendAgentMessage_UnauthorizedRecipient_ReturnsErrorWithoutDispatch()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgent("reviewer", ["trusted_sender"]));

        var bus = new StubMessageBus();
        var tool = new SendAgentMessageTool(bus, registry);
        var call = CreateCall(
            "send_agent_message",
            new
            {
                recipient_agent_id = "reviewer",
                message = "hello reviewer"
            });
        var context = CreateContext(agentId: "codegen");

        var result = await tool.ExecuteAsync(call, context, CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.Contains("not authorized", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(bus.SendCalled);
    }

    [TestMethod]
    public async Task ReadAgentMessages_UsesScopedContextAndAgentIdentity()
    {
        var bus = new StubMessageBus
        {
            ReadResponse =
            [
                new AgentMessage(
                    MessageId: "m-1",
                    SessionKey: new SessionKey("root-session"),
                    ScopeRunId: new RunId("root-run"),
                    SenderAgentId: "codegen",
                    RecipientAgentId: "reviewer",
                    Content: "please validate tests",
                    SentAt: DateTimeOffset.UtcNow)
            ]
        };

        var tool = new ReadAgentMessagesTool(bus);
        var call = CreateCall(
            "read_agent_messages",
            new
            {
                max_messages = 5,
                mark_as_read = false
            });
        var context = CreateContext(
            agentId: "reviewer",
            runId: new RunId("child-run"),
            sessionKey: new SessionKey("child-session"),
            parentRunId: new RunId("root-run"),
            parentSessionKey: new SessionKey("root-session"));

        var result = await tool.ExecuteAsync(call, context, CancellationToken.None);

        Assert.IsFalse(result.IsError);
        Assert.IsTrue(bus.ReadCalled);
        Assert.IsNotNull(bus.LastReadRequest);
        Assert.AreEqual("reviewer", bus.LastReadRequest!.RecipientAgentId);
        Assert.AreEqual("root-run", bus.LastReadRequest.ScopeRunId.Value);
        Assert.AreEqual("root-session", bus.LastReadRequest.SessionKey.Value);
        Assert.AreEqual(5, bus.LastReadRequest.MaxMessages);
        Assert.IsFalse(bus.LastReadRequest.MarkAsRead);

        using var payload = JsonDocument.Parse(result.Content);
        Assert.AreEqual(JsonValueKind.Array, payload.RootElement.ValueKind);
        Assert.ContainsSingle(payload.RootElement.EnumerateArray());
        var first = payload.RootElement.EnumerateArray().First();
        Assert.AreEqual("codegen", first.GetProperty("sender_agent_id").GetString());
        Assert.AreEqual("reviewer", first.GetProperty("recipient_agent_id").GetString());
        Assert.AreEqual("please validate tests", first.GetProperty("content").GetString());
    }

    private static AgentDefinition CreateAgent(string agentId, IReadOnlyCollection<string>? allowedSenders = null)
    {
        return new AgentDefinition(
            AgentId: agentId,
            DisplayName: agentId,
            Description: $"Agent {agentId}",
            SystemPrompt: $"You are {agentId}.",
            ToolPolicy: new ToolSelectionPolicy(ToolProfile.Coding),
            AllowedMessageSenders: allowedSenders);
    }

    private static ToolCall CreateCall(string toolName, object args)
    {
        return new ToolCall(
            Guid.NewGuid().ToString("n"),
            toolName,
            JsonSerializer.SerializeToElement(args));
    }

    private static ToolExecutionContext CreateContext(
        string? agentId,
        RunId? runId = null,
        SessionKey? sessionKey = null,
        RunId? parentRunId = null,
        SessionKey? parentSessionKey = null)
    {
        return new ToolExecutionContext(
            RunId: runId ?? new RunId("run"),
            SessionKey: sessionKey ?? new SessionKey("session"),
            Provider: "local",
            Model: "rule",
            Caller: CallerIdentity.LocalOwner(),
            AgentId: agentId,
            ParentRunId: parentRunId,
            ParentSessionKey: parentSessionKey);
    }

    private sealed class StubMessageBus : IAgentMessageBus
    {
        public bool SendCalled { get; private set; }

        public bool ReadCalled { get; private set; }

        public AgentMessageSendRequest? LastSendRequest { get; private set; }

        public AgentMessageReadRequest? LastReadRequest { get; private set; }

        public IReadOnlyList<AgentMessage> ReadResponse { get; set; } = Array.Empty<AgentMessage>();

        public Task<AgentMessage> SendAsync(AgentMessageSendRequest request, CancellationToken ct)
        {
            SendCalled = true;
            LastSendRequest = request;

            return Task.FromResult(
                new AgentMessage(
                    MessageId: "stub-message",
                    SessionKey: request.SessionKey,
                    ScopeRunId: request.ScopeRunId,
                    SenderAgentId: request.SenderAgentId,
                    RecipientAgentId: request.RecipientAgentId,
                    Content: request.Content,
                    SentAt: DateTimeOffset.UtcNow,
                    CorrelationId: request.CorrelationId));
        }

        public Task<IReadOnlyList<AgentMessage>> ReadAsync(AgentMessageReadRequest request, CancellationToken ct)
        {
            ReadCalled = true;
            LastReadRequest = request;
            return Task.FromResult(ReadResponse);
        }
    }
}
