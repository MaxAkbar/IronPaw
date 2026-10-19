using System.Diagnostics;
using System.Diagnostics.Metrics;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public enum PresentationObservation { Coalesced, Omitted, Truncated, CallbackFailed, ObserverFailed }

/// <summary>Optional, payload-free diagnostics. Hosts opt in with ActivityListener/MeterListener or an exporter.</summary>
public static class ExecutionDiagnostics
{
    public const string SourceName = "IronPaw.AgentCore";
    public const string MeterName = "IronPaw.AgentCore";
    private static readonly Lazy<Instruments> Telemetry = new(() => new());

    // Only fixed operation/outcome/direction values are metric dimensions. IDs belong in traces.
    internal static Operation Observe(string operation, params (string Key, string? Value)[] identity) => new(operation, identity);

    public static void RecordPresentation(PresentationObservation observation, long count = 1)
    {
        if (count <= 0 || !Enum.IsDefined(observation)) return;
        Try(() => Telemetry.Value.Presentation.Add(count, new KeyValuePair<string, object?>("outcome", observation.ToString())));
    }

    internal static void RecordTokens(UsageDelta usage)
    {
        Record("input", usage.InputTokens); Record("output", usage.OutputTokens);
        Record("cache_read", usage.CacheReadTokens); Record("cache_write", usage.CacheWriteTokens);
        static void Record(string direction, long? count)
        {
            if (count is > 0) Try(() => Telemetry.Value.Tokens.Add(count.Value, new KeyValuePair<string, object?>("direction", direction)));
        }
    }

    internal static string Outcome(AgentRunStatus status) => status switch {
        AgentRunStatus.Succeeded => "completed", AgentRunStatus.Cancelled => "cancelled",
        AgentRunStatus.BudgetExhausted => "budget_exhausted", AgentRunStatus.AwaitingInput => "awaiting_input",
        AgentRunStatus.Paused => "paused", AgentRunStatus.RoundLimitReached => "round_limit", _ => "failed"
    };
    internal static string Outcome(TaskGraphTaskState task) => task.Status switch {
        _ when task.Verifications.Any(invocation => invocation.State != TaskVerificationInvocationState.Completed) => "needs_reconciliation",
        TaskGraphTaskStatus.Succeeded when task.Acceptance?.Outcome == TaskAcceptanceOutcome.Accepted => "accepted",
        TaskGraphTaskStatus.Failed => "failed", TaskGraphTaskStatus.BudgetExhausted => "budget_exhausted",
        TaskGraphTaskStatus.Cancelled => "cancelled", TaskGraphTaskStatus.NeedsReconciliation => "needs_reconciliation",
        _ => "inconclusive"
    };
    internal static string Outcome(TaskGraphRunStatus status) => status switch {
        TaskGraphRunStatus.Succeeded => "accepted", TaskGraphRunStatus.Failed => "failed",
        TaskGraphRunStatus.Cancelled => "cancelled", TaskGraphRunStatus.Paused => "paused",
        TaskGraphRunStatus.BudgetExhausted => "budget_exhausted", TaskGraphRunStatus.NeedsReconciliation => "needs_reconciliation",
        _ => "inconclusive"
    };
    internal static string Outcome(TaskCommandOutcome outcome) => outcome switch {
        TaskCommandOutcome.Passed => "completed", TaskCommandOutcome.Failed => "failed",
        TaskCommandOutcome.BudgetExhausted => "budget_exhausted", _ => "needs_reconciliation"
    };

    internal sealed class Operation : IDisposable
    {
        private readonly string _operation;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly Activity? _previous = Activity.Current;
        private Activity? _activity;
        private string _outcome = "failed";
        private int _disposed;

        internal Operation(string operation, (string Key, string? Value)[] identity)
        {
            _operation = operation;
            Try(() => Telemetry.Value.Active.Add(1, new KeyValuePair<string, object?>("operation", operation)));
            try
            {
                var tags = identity.Where(item => item.Value is not null)
                    .Select(item => new KeyValuePair<string, object?>(item.Key, ExecutionTimelineBuilder.Clean(item.Value, 255)));
                _activity = Telemetry.Value.Source.StartActivity("ironpaw." + operation, ActivityKind.Internal,
                    parentContext: Activity.Current?.Context ?? default, tags: tags);
            }
            catch
            {
                // Listener exceptions can escape StartActivity after it changed the ambient context.
                var current = Activity.Current;
                if (!ReferenceEquals(current, _previous)) Try(() => current?.Dispose());
                Activity.Current = _previous;
            }
        }

        internal void Complete(string outcome) => _outcome = outcome;
        internal void Complete(TaskGraphCheckpoint checkpoint, string? taskId = null) => Try(() => Complete(taskId is null
            ? Outcome(TaskGraphProgress.Inspect(checkpoint).Status)
            : Outcome(checkpoint.Tasks.Single(task => task.TaskId == taskId))));
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var tags = new TagList { { "operation", _operation }, { "outcome", _outcome } };
            Try(() => Telemetry.Value.Operations.Add(1, tags));
            Try(() => Telemetry.Value.Duration.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds, tags));
            Try(() => Telemetry.Value.Active.Add(-1, new KeyValuePair<string, object?>("operation", _operation)));
            Try(() => _activity?.SetTag("ironpaw.outcome", _outcome));
            Try(() => _activity?.SetStatus(_outcome is "failed" or "needs_reconciliation" ? ActivityStatusCode.Error : ActivityStatusCode.Unset));
            Try(() => _activity?.Dispose());
            if (_activity is not null && ReferenceEquals(Activity.Current, _activity)) Activity.Current = _previous;
        }
    }

    private sealed class Instruments
    {
        internal readonly ActivitySource Source = new(SourceName, "1.0.0");
        private readonly Meter _meter = new(MeterName, "1.0.0");
        internal readonly Counter<long> Operations;
        internal readonly UpDownCounter<long> Active;
        internal readonly Histogram<double> Duration;
        internal readonly Counter<long> Tokens;
        internal readonly Counter<long> Presentation;
        internal Instruments()
        {
            Operations = _meter.CreateCounter<long>("ironpaw.operations", "{operation}");
            Active = _meter.CreateUpDownCounter<long>("ironpaw.operations.active", "{operation}");
            Duration = _meter.CreateHistogram<double>("ironpaw.operation.duration", "s");
            Tokens = _meter.CreateCounter<long>("ironpaw.model.reported_tokens", "{token}");
            Presentation = _meter.CreateCounter<long>("ironpaw.presentation.events", "{event}");
        }
    }
    private static void Try(Action action) { try { action(); } catch (Exception) { /* Observers never authorize or fail execution. */ } }
}
