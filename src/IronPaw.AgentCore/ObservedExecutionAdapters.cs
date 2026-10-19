using System.Runtime.CompilerServices;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

// Wrap the actual adapter boundary so durable replay does not count a second provider/tool call.
internal sealed class ObservedLlmClient(ILlmClient inner) : ILlmClient
{
    public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var observation = ExecutionDiagnostics.Observe("model", ("ironpaw.run.id", request.RunId.Value));
        var ended = false;
        var completed = false;
        var failed = false;
        try
        {
            await foreach (var delta in inner.StreamAsync(request, ct).ConfigureAwait(false))
            {
                if (delta is UsageDelta usage) ExecutionDiagnostics.RecordTokens(usage);
                if (delta is CompletedDelta) completed = true;
                if (delta is LlmErrorDelta) failed = true;
                yield return delta;
            }
            ended = true;
        }
        finally
        {
            observation.Complete(ended && completed && !failed ? "completed" : ct.IsCancellationRequested ? "cancelled" : "failed");
        }
    }
}

internal sealed class ObservedToolExecutor(IToolExecutor inner) : IToolExecutor
{
    public async Task<ToolResult> ExecuteAsync(ITool tool, ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        using var observation = ExecutionDiagnostics.Observe("tool", ("ironpaw.run.id", context.RunId.Value),
            ("ironpaw.workflow.root_id", (context.RootRunId ?? context.RunId).Value),
            ("ironpaw.parent_run.id", context.ParentRunId?.Value), ("ironpaw.tool.execution_id", context.ToolExecutionId),
            ("ironpaw.tool.call_id", call.ToolCallId));
        try
        {
            var result = await inner.ExecuteAsync(tool, call, context, ct).ConfigureAwait(false);
            observation.Complete(result.IsError ? "failed" : "completed");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { observation.Complete("cancelled"); throw; }
    }
}

/// <summary>Presentation errors cannot change execution. Async observers are also bounded by execution cancellation.</summary>
internal sealed class ResilientAgentEventSink(IAgentEventSink inner) : IAgentEventSink
{
    private long _failures;
    private string? _lastError;
    public async ValueTask PublishAsync(AgentEvent evt, CancellationToken ct)
    {
        try
        {
            var pending = inner.PublishAsync(evt, ct);
            if (pending.IsCompletedSuccessfully) pending.GetAwaiter().GetResult();
            else await pending.AsTask().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref _failures);
            _lastError = error.GetType().Name;
            try { _lastError = ExecutionTimelineBuilder.Clean(error.Message, 2000); } catch (Exception) { }
            ExecutionDiagnostics.RecordPresentation(PresentationObservation.ObserverFailed);
        }
    }
    internal AgentRunResult Annotate(AgentRunResult result)
    {
        if (Interlocked.Read(ref _failures) == 0) return result;
        var metadata = result.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value);
        metadata["eventDeliveryError"] = _lastError;
        metadata["eventDeliveryFailures"] = Interlocked.Read(ref _failures);
        return result with { Metadata = metadata };
    }
}
