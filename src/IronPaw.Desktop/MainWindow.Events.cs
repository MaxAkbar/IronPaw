using System.Text;
using System.Windows.Threading;
using IronPaw.AgentCore;

namespace IronPaw.Desktop;

public partial class MainWindow
{
    internal const int MaxVisibleEvents = 500;
    internal const int MaxVisibleResponseCharacters = 65536;
    private readonly UiEventSink _eventSink = new();
    private DispatcherTimer? _eventTimer;
    private bool _liveText;
    private bool _responseTruncated;

    private void InitializeEventDelivery()
    {
        _eventTimer = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _eventTimer.Tick += (_, _) => DrainUiEvents();
        Loaded += (_, _) => _eventTimer.Start();
    }
    private void DrainUiEvents() => _eventSink.DrainTo(PresentEvents);
    private void PresentEvents(UiEventBatch batch)
    {
        var response = new StringBuilder();
        foreach (var update in batch.Updates)
        {
            var summary = update.IsAssistantText ? $"{update.Content.Length} text characters ({update.EventCount} updates): " +
                ExecutionTimelineBuilder.Clean(update.Content, 160) : update.Content;
            AddVisibleEvent($"{update.Timestamp.LocalDateTime:HH:mm:ss.fff} [{update.Stream}/{update.Type}] " +
                $"{(update.AgentId is { } agent ? "@" + agent + " " : "")}run={update.RunId} {summary}");
            if (_liveText && !_responseTruncated && update.IsAssistantText && update.RunId == _activeRootRunId?.Value)
                response.Append(update.Content);
        }
        if (response.Length > 0) SetVisibleResponse(AssistantOutputTextBox.Text + response);
        if (batch.Omitted > 0 || batch.Truncated > 0 || batch.PresentationFailures > 0)
            AddVisibleEvent($"[Live view] {batch.Omitted} updates omitted, {batch.Truncated} shortened, {batch.PresentationFailures} display failures. Open Timeline for saved state and receipts.");
        if (batch.Coalesced > 0) AddVisibleEvent($"[Live view] Combined {batch.Coalesced} adjacent text updates.");
        EventsListBox.ScrollIntoView(Events.LastOrDefault());
    }
    private void AddVisibleEvent(string message)
    {
        try
        {
            while (Events.Count >= MaxVisibleEvents) Events.RemoveAt(0);
            Events.Add(ExecutionTimelineBuilder.Clean(message, 4096));
        }
        catch (Exception) { ExecutionDiagnostics.RecordPresentation(PresentationObservation.CallbackFailed); }
    }
    private void SetVisibleResponse(string text)
    {
        _responseTruncated = text.Length > MaxVisibleResponseCharacters;
        AssistantOutputTextBox.Text = ExecutionTimelineBuilder.Clean(text, MaxVisibleResponseCharacters) +
            (_responseTruncated ? "\n[Response shortened in the live view.]" : "");
    }
    private void StopEventDelivery() { _eventTimer?.Stop(); _eventSink.Dispose(); }
}
