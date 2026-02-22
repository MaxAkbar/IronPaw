using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Desktop;

internal sealed class UiEventSink : IAgentEventSink
{
    private readonly Action<AgentEvent> _callback;

    public UiEventSink(Action<AgentEvent> callback)
    {
        _callback = callback;
    }

    public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct)
    {
        _callback(evt);
        return ValueTask.CompletedTask;
    }
}
