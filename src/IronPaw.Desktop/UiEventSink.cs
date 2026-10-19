using System.Globalization;
using System.Text;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Desktop;

internal sealed record UiEventUpdate(string RunId, string? AgentId, AgentEventStream Stream, string Type,
    DateTimeOffset Timestamp, string Content, bool IsAssistantText, int EventCount = 1)
{
    public int CharacterCost => RunId.Length + (AgentId?.Length ?? 0) + Type.Length + Content.Length + 128;
}
internal sealed record UiEventBatch(IReadOnlyList<UiEventUpdate> Updates, long Omitted, long Truncated,
    long Coalesced, long PresentationFailures);

/// <summary>A bounded display projection, never the workflow journal. The dispatcher drains it; producers never call UI code.</summary>
internal sealed class UiEventSink(int capacity = 512, int maxCharacters = 131072) : IAgentEventSink, IDisposable
{
    internal const int MaxTextBatch = 8192;
    private readonly object _gate = new();
    private readonly LinkedList<UiEventUpdate> _pending = new();
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly int _maxCharacters = maxCharacters >= 1024 ? maxCharacters : throw new ArgumentOutOfRangeException(nameof(maxCharacters));
    private int _characters;
    private bool _closed;
    private long _omitted, _truncated, _coalesced, _failures;
    internal int PendingCount { get { lock (_gate) return _pending.Count; } }
    internal int PendingCharacters { get { lock (_gate) return _characters; } }

    public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct)
    {
        lock (_gate) { if (_closed) return ValueTask.CompletedTask; }
        try
        {
            var (update, truncated) = Capture(evt, Math.Min(MaxTextBatch, _maxCharacters - 512));
            lock (_gate)
            {
                if (_closed) return ValueTask.CompletedTask;
                if (truncated) _truncated++;
                if (update.IsAssistantText && _pending.Last?.Value is { IsAssistantText: true } last &&
                    last.RunId == update.RunId && last.AgentId == update.AgentId && last.Type == update.Type &&
                    last.Content.Length + update.Content.Length <= MaxTextBatch && _characters + update.Content.Length <= _maxCharacters)
                {
                    _pending.Last.Value = last with { Content = last.Content + update.Content, EventCount = last.EventCount + 1 };
                    _characters += update.Content.Length;
                    _coalesced++;
                    return ValueTask.CompletedTask;
                }
                while (_pending.Count >= _capacity || _characters + update.CharacterCost > _maxCharacters)
                {
                    var victim = _pending.First;
                    for (var node = _pending.First; node is not null; node = node.Next)
                        if (victim is null || Priority(node.Value) < Priority(victim.Value)) victim = node;
                    if (victim is null || Priority(victim.Value) > Priority(update)) { Omit(update.EventCount); return ValueTask.CompletedTask; }
                    Omit(victim.Value.EventCount);
                    _characters -= victim.Value.CharacterCost;
                    _pending.Remove(victim);
                }
                _pending.AddLast(update);
                _characters += update.CharacterCost;
            }
        }
        catch (Exception)
        {
            lock (_gate) { if (!_closed) _failures++; }
        }
        return ValueTask.CompletedTask;
    }

    // Call only from the view's owner thread. No application callback runs under the producer lock.
    internal void DrainTo(Action<UiEventBatch> present, int maxUpdates = 128)
    {
        UiEventBatch batch;
        lock (_gate)
        {
            if (_closed || maxUpdates <= 0) return;
            var rows = new List<UiEventUpdate>(Math.Min(maxUpdates, _pending.Count));
            while (_pending.First is { } node && rows.Count < maxUpdates)
            {
                rows.Add(node.Value); _characters -= node.Value.CharacterCost; _pending.RemoveFirst();
            }
            batch = new(rows, _omitted, _truncated, _coalesced, _failures);
            _omitted = _truncated = _coalesced = _failures = 0;
        }
        if (batch.Updates.Count == 0 && batch.Omitted == 0 && batch.Truncated == 0 && batch.Coalesced == 0 && batch.PresentationFailures == 0) return;
        ExecutionDiagnostics.RecordPresentation(PresentationObservation.Omitted, batch.Omitted);
        ExecutionDiagnostics.RecordPresentation(PresentationObservation.Truncated, batch.Truncated);
        ExecutionDiagnostics.RecordPresentation(PresentationObservation.Coalesced, batch.Coalesced);
        ExecutionDiagnostics.RecordPresentation(PresentationObservation.CallbackFailed, batch.PresentationFailures);
        try { present(batch); }
        catch (Exception)
        {
            lock (_gate) { if (!_closed) _failures++; }
        }
    }

    internal void Reset()
    {
        lock (_gate) { _pending.Clear(); _characters = 0; _omitted = _truncated = _coalesced = _failures = 0; }
    }
    public void Dispose() { lock (_gate) { _closed = true; _pending.Clear(); _characters = 0; } }
    private void Omit(int count) => _omitted += count;
    private static int Priority(UiEventUpdate row) => row.Stream switch { AgentEventStream.Lifecycle => 4, AgentEventStream.Tool => 3, AgentEventStream.Assistant or AgentEventStream.Reasoning => 1, _ => 2 };

    private static (UiEventUpdate Update, bool Truncated) Capture(AgentEvent evt, int limit)
    {
        var truncated = false;
        var text = evt.Type == "assistant_delta" && evt.Stream == AgentEventStream.Assistant &&
            evt.Data.TryGetValue("text", out var value) && value is string;
        string content;
        if (text) content = (string)evt.Data["text"]!;
        else
        {
            var summary = new StringBuilder();
            var fields = 0;
            foreach (var pair in evt.Data)
            {
                if (fields++ == 16) { truncated = true; break; }
                var display = pair.Value switch {
                    null => "null", string message => ExecutionTimelineBuilder.Clean(message, 256),
                    bool flag => flag ? "true" : "false",
                    int or long or short or byte or uint or ulong or decimal or double or float => ((IFormattable)pair.Value).ToString(null, CultureInfo.InvariantCulture),
                    _ => "[structured data]"
                };
                if (pair.Value is string source && source.Length > 256) truncated = true;
                if (fields > 1) summary.Append(", ");
                summary.Append(ExecutionTimelineBuilder.Clean(pair.Key, 64)).Append('=').Append(display);
            }
            content = summary.ToString();
            limit = Math.Min(limit, 2048);
        }
        truncated |= content.Length > limit;
        return (new(ExecutionTimelineBuilder.Clean(evt.RunId.Value, 128), evt.AgentId is null ? null : ExecutionTimelineBuilder.Clean(evt.AgentId, 64),
            evt.Stream, ExecutionTimelineBuilder.Clean(evt.Type, 64), evt.Timestamp, ExecutionTimelineBuilder.Clean(content, limit), text), truncated);
    }
}
