using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Desktop;

internal enum ExecutionTimelineKind { Workflow, TaskGraph }
internal sealed record ExecutionTimelineChoice(ExecutionTimelineKind Kind, string Id, string Title, DateTimeOffset UpdatedAt)
{
    public string Label => $"{(Kind == ExecutionTimelineKind.TaskGraph ? "Task graph" : "Workflow")}: {Title} ({ExecutionTimelineBuilder.Clean(Id, 24)})";
}

/// <summary>Browsing saved execution never initializes a provider or grants execution authority.</summary>
internal sealed class ExecutionTimelineController(WorkflowRecoveryController workflows, TaskGraphHostController? graphs)
{
    public async Task<IReadOnlyList<ExecutionTimelineChoice>> ListAsync(CancellationToken ct)
    {
        var roots = await workflows.ListRootsAsync(ct);
        var choices = roots.Select(run => new ExecutionTimelineChoice(ExecutionTimelineKind.Workflow, run.RunId,
            ExecutionTimelineBuilder.Clean(run.Request.UserInput, 90), run.UpdatedAt)).ToList();
        if (graphs is not null)
            choices.AddRange((await graphs.ListAsync(CallerIdentity.LocalOwner(), ct)).Select(graph =>
                new ExecutionTimelineChoice(ExecutionTimelineKind.TaskGraph, graph.Graph.GraphId,
                    ExecutionTimelineBuilder.Clean(graph.Graph.Objective, 90), graph.UpdatedAt)));
        return choices.OrderByDescending(choice => choice.UpdatedAt).ThenBy(choice => choice.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<ExecutionTimelineSnapshot> InspectAsync(ExecutionTimelineChoice choice, CancellationToken ct)
    {
        if (choice.Kind == ExecutionTimelineKind.TaskGraph)
        {
            var detail = await (graphs ?? throw new InvalidOperationException("Task graphs are unavailable in this host."))
                .InspectAsync(choice.Id, CallerIdentity.LocalOwner(), ct);
            return ExecutionTimelineBuilder.ForGraph(detail, DateTimeOffset.UtcNow);
        }
        var workflow = await workflows.InspectAsync(new(choice.Id), ct);
        return ExecutionTimelineBuilder.ForWorkflow(workflow.Root, workflow.Invocations, workflow.Journal,
            workflow.Usage, workflow.Budget, DateTimeOffset.UtcNow);
    }
}

/// <summary>Late results and failures cannot update a different selection or a closed view.</summary>
internal sealed class TimelineReadCoordinator : IDisposable
{
    private long _version;
    private CancellationTokenSource? _current;
    private bool _disposed;
    public bool IsReading => _current is not null;

    public async Task ReadAsync(Func<CancellationToken, Task<ExecutionTimelineSnapshot>> read,
        Action<ExecutionTimelineSnapshot> apply, Action<Exception> error)
    {
        if (_disposed) return;
        Invalidate();
        var version = _version;
        using var cancellation = new CancellationTokenSource();
        _current = cancellation;
        try
        {
            var snapshot = await read(cancellation.Token);
            if (!_disposed && version == _version && !cancellation.IsCancellationRequested) apply(snapshot);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception failure)
        {
            if (!_disposed && version == _version) error(failure);
        }
        finally { if (ReferenceEquals(_current, cancellation)) _current = null; }
    }

    public void Invalidate()
    {
        _version++;
        var previous = _current;
        _current = null;
        try { previous?.Cancel(); }
        catch (AggregateException) { /* Cancellation observers cannot keep a stale selection current. */ }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Invalidate(); }
}
