using IronPaw.AgentCore.Abstractions;
using Spectre.Console;

namespace IronPaw.Cli;

internal sealed class AnsiConsoleEventSink : IAgentEventSink
{
    public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct)
    {
        var severityStyle = evt.Severity switch
        {
            AgentEventSeverity.Warning => "yellow",
            AgentEventSeverity.Error => "red",
            _ => "grey70"
        };

        var streamStyle = evt.Stream switch
        {
            AgentEventStream.Lifecycle => "deepskyblue1",
            AgentEventStream.Assistant => "green3_1",
            AgentEventStream.Tool => "darkorange3",
            AgentEventStream.Diagnostics => "mediumpurple",
            AgentEventStream.Compaction => "khaki1",
            _ => "white"
        };

        var time = evt.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        var data = string.Join(", ", evt.Data.Select(pair => $"{pair.Key}={pair.Value}"));
        var dataPart = string.IsNullOrWhiteSpace(data)
            ? string.Empty
            : $" [silver]{Markup.Escape(data)}[/]";
        var agentPrefix = evt.AgentId is not null
            ? $"[cyan]@{Markup.Escape(evt.AgentId)}[/] "
            : string.Empty;

        AnsiConsole.MarkupLine(
            $"[{severityStyle}]{Markup.Escape(time)}[/] " +
            agentPrefix +
            $"[{streamStyle}]{Markup.Escape(evt.Stream.ToString())}[/]/" +
            $"[{streamStyle}]{Markup.Escape(evt.Type)}[/]{dataPart}");

        return ValueTask.CompletedTask;
    }
}
