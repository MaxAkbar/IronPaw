using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class AgentPrefixedEventSink : IAgentEventSink
{
    private readonly IAgentEventSink _inner;
    private readonly string _agentId;

    public AgentPrefixedEventSink(IAgentEventSink inner, string agentId)
    {
        _inner = inner;
        _agentId = agentId;
    }

    public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct)
    {
        var tagged = evt with { AgentId = _agentId };
        return _inner.PublishAsync(tagged, ct);
    }
}
