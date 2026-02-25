using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

public sealed class InMemoryAgentMessageBusTests
{
    [Fact]
    public async Task SendAsync_AuthorizedSender_CanReadMessageFromSameScope()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgent("alpha", allowedSenders: ["beta"]));
        registry.Register(CreateAgent("beta", allowedSenders: ["alpha"]));
        var bus = new InMemoryAgentMessageBus(registry);

        var sessionKey = new SessionKey("session-a");
        var scopeRunId = new RunId("run-a");

        var delivered = await bus.SendAsync(
            new AgentMessageSendRequest(
                SessionKey: sessionKey,
                ScopeRunId: scopeRunId,
                SenderAgentId: "alpha",
                RecipientAgentId: "beta",
                Content: "hello from alpha",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        var messages = await bus.ReadAsync(
            new AgentMessageReadRequest(
                SessionKey: sessionKey,
                ScopeRunId: scopeRunId,
                RecipientAgentId: "beta"),
            CancellationToken.None);

        Assert.Single(messages);
        Assert.Equal(delivered.MessageId, messages[0].MessageId);
        Assert.Equal("alpha", messages[0].SenderAgentId);
        Assert.Equal("beta", messages[0].RecipientAgentId);
        Assert.Equal("hello from alpha", messages[0].Content);

        var afterRead = await bus.ReadAsync(
            new AgentMessageReadRequest(
                SessionKey: sessionKey,
                ScopeRunId: scopeRunId,
                RecipientAgentId: "beta"),
            CancellationToken.None);

        Assert.Empty(afterRead);
    }

    [Fact]
    public async Task SendAsync_UnauthorizedSender_Throws()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgent("alpha", allowedSenders: ["beta"]));
        registry.Register(CreateAgent("beta", allowedSenders: ["alpha"]));
        registry.Register(CreateAgent("gamma", allowedSenders: ["beta"]));
        var bus = new InMemoryAgentMessageBus(registry);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bus.SendAsync(
                new AgentMessageSendRequest(
                    SessionKey: new SessionKey("session-a"),
                    ScopeRunId: new RunId("run-a"),
                    SenderAgentId: "gamma",
                    RecipientAgentId: "beta",
                    Content: "not allowed"),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_IsolatedBySessionAndRunScope()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgent("alpha", allowedSenders: ["beta"]));
        registry.Register(CreateAgent("beta", allowedSenders: ["alpha"]));
        var bus = new InMemoryAgentMessageBus(registry);

        await bus.SendAsync(
            new AgentMessageSendRequest(
                SessionKey: new SessionKey("session-a"),
                ScopeRunId: new RunId("run-a"),
                SenderAgentId: "alpha",
                RecipientAgentId: "beta",
                Content: "scoped message"),
            CancellationToken.None);

        var wrongSession = await bus.ReadAsync(
            new AgentMessageReadRequest(
                SessionKey: new SessionKey("session-b"),
                ScopeRunId: new RunId("run-a"),
                RecipientAgentId: "beta"),
            CancellationToken.None);
        Assert.Empty(wrongSession);

        var wrongRun = await bus.ReadAsync(
            new AgentMessageReadRequest(
                SessionKey: new SessionKey("session-a"),
                ScopeRunId: new RunId("run-b"),
                RecipientAgentId: "beta"),
            CancellationToken.None);
        Assert.Empty(wrongRun);

        var correctScope = await bus.ReadAsync(
            new AgentMessageReadRequest(
                SessionKey: new SessionKey("session-a"),
                ScopeRunId: new RunId("run-a"),
                RecipientAgentId: "beta"),
            CancellationToken.None);
        Assert.Single(correctScope);
    }

    [Fact]
    public async Task ReadAsync_CanFilterBySenderAndCorrelation()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgent("alpha", allowedSenders: ["beta", "gamma"]));
        registry.Register(CreateAgent("beta", allowedSenders: ["alpha", "gamma"]));
        registry.Register(CreateAgent("gamma", allowedSenders: ["alpha"]));
        var bus = new InMemoryAgentMessageBus(registry);

        var sessionKey = new SessionKey("session-a");
        var scopeRunId = new RunId("run-a");

        await bus.SendAsync(
            new AgentMessageSendRequest(sessionKey, scopeRunId, "alpha", "beta", "m1", "corr-1"),
            CancellationToken.None);
        await bus.SendAsync(
            new AgentMessageSendRequest(sessionKey, scopeRunId, "gamma", "beta", "m2", "corr-1"),
            CancellationToken.None);
        await bus.SendAsync(
            new AgentMessageSendRequest(sessionKey, scopeRunId, "alpha", "beta", "m3", "corr-2"),
            CancellationToken.None);

        var filtered = await bus.ReadAsync(
            new AgentMessageReadRequest(
                SessionKey: sessionKey,
                ScopeRunId: scopeRunId,
                RecipientAgentId: "beta",
                MarkAsRead: false,
                SenderAgentId: "alpha",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        Assert.Single(filtered);
        Assert.Equal("alpha", filtered[0].SenderAgentId);
        Assert.Equal("corr-1", filtered[0].CorrelationId);
    }

    [Fact]
    public async Task ReadAsync_ConsumeWithFilter_RemovesOnlyMatchedMessages()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgent("alpha", allowedSenders: ["beta"]));
        registry.Register(CreateAgent("beta", allowedSenders: ["alpha"]));
        var bus = new InMemoryAgentMessageBus(registry);

        var sessionKey = new SessionKey("session-a");
        var scopeRunId = new RunId("run-a");

        await bus.SendAsync(
            new AgentMessageSendRequest(sessionKey, scopeRunId, "alpha", "beta", "m1", "corr-1"),
            CancellationToken.None);
        await bus.SendAsync(
            new AgentMessageSendRequest(sessionKey, scopeRunId, "alpha", "beta", "m2", "corr-2"),
            CancellationToken.None);

        var consumed = await bus.ReadAsync(
            new AgentMessageReadRequest(
                SessionKey: sessionKey,
                ScopeRunId: scopeRunId,
                RecipientAgentId: "beta",
                MarkAsRead: true,
                CorrelationId: "corr-1"),
            CancellationToken.None);
        Assert.Single(consumed);
        Assert.Equal("corr-1", consumed[0].CorrelationId);

        var remaining = await bus.ReadAsync(
            new AgentMessageReadRequest(
                SessionKey: sessionKey,
                ScopeRunId: scopeRunId,
                RecipientAgentId: "beta",
                MarkAsRead: true),
            CancellationToken.None);
        Assert.Single(remaining);
        Assert.Equal("corr-2", remaining[0].CorrelationId);
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
}
