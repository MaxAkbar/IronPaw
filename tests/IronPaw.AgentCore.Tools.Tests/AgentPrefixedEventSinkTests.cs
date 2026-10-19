using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tools.Tests;

[TestClass]
public sealed class AgentPrefixedEventSinkTests
{
    [TestMethod]
    public async Task PublishAsync_TagsEventWithAgentId()
    {
        var captured = new List<AgentEvent>();
        var inner = new CapturingSink(captured);
        var sink = new AgentPrefixedEventSink(inner, "coder");

        var evt = new AgentEvent(
            RunId.New(),
            new SessionKey("test"),
            AgentEventStream.Lifecycle,
            Type: "test_event",
            Timestamp: DateTimeOffset.UtcNow,
            Data: EventData.Of());

        await sink.PublishAsync(evt, CancellationToken.None);

        Assert.ContainsSingle(captured);
        Assert.AreEqual("coder", captured[0].AgentId);
    }

    [TestMethod]
    public async Task PublishAsync_OverwritesExistingAgentId()
    {
        var captured = new List<AgentEvent>();
        var inner = new CapturingSink(captured);
        var sink = new AgentPrefixedEventSink(inner, "reviewer");

        var evt = new AgentEvent(
            RunId.New(),
            new SessionKey("test"),
            AgentEventStream.Tool,
            Type: "tool_call",
            Timestamp: DateTimeOffset.UtcNow,
            Data: EventData.Of(),
            AgentId: "original");

        await sink.PublishAsync(evt, CancellationToken.None);

        Assert.AreEqual("reviewer", captured[0].AgentId);
    }

    private sealed class CapturingSink : IAgentEventSink
    {
        private readonly List<AgentEvent> _events;

        public CapturingSink(List<AgentEvent> events) => _events = events;

        public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct)
        {
            _events.Add(evt);
            return ValueTask.CompletedTask;
        }
    }
}
