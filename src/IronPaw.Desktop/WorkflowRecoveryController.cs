using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Desktop;

/// <summary>Desktop commands preserve the durable invocation and original local ownership.</summary>
internal sealed class WorkflowRecoveryController(IResumableAgentOrchestrator orchestrator, IWorkflowStore store)
{
    private readonly CallerIdentity _caller = CallerIdentity.LocalOwner();

    public async Task<IReadOnlyList<WorkflowCheckpoint>> ListRootsAsync(CancellationToken ct)
    {
        var graphRoots = await GraphRootIdsAsync(ct);
        return (await orchestrator.ListWorkflowsAsync(ct)).Where(run => run.RunId == run.RootRunId && !graphRoots.Contains(run.RunId) &&
                run.Request.Caller.Id == _caller.Id && run.Request.Caller.IsOwner == _caller.IsOwner)
            .OrderByDescending(run => run.UpdatedAt).ToArray();
    }

    public async Task<WorkflowRecoveryDetail> InspectAsync(RunId runId, CancellationToken ct)
    {
        var selected = await RequireAsync(runId, ct);
        var root = await RequireAsync(new RunId(selected.RootRunId), ct);
        var invocations = (await orchestrator.ListWorkflowsAsync(ct))
            .Where(run => run.RootRunId == root.RunId && run.Request.Caller.Id == _caller.Id && run.Request.Caller.IsOwner == _caller.IsOwner)
            .OrderBy(run => run.CreatedAt).ToArray();
        var entries = await Task.WhenAll(invocations.Select(run => store.ReadJournalAsync(new RunId(run.RunId), ct)));
        return new(root, selected, invocations,
            entries.SelectMany(journal => journal).OrderBy(entry => entry.Timestamp).ToArray(),
            await store.GetUsageAsync(new RunId(root.RunId), ct),
            await store.GetBudgetAsync(new RunId(root.RunId), ct));
    }

    public async Task<AgentRunResult> ResumeAsync(RunId runId, IAgentEventSink sink, CancellationToken ct)
    {
        var run = await RequireAsync(runId, ct);
        return await orchestrator.ResumeAsync(new RunId(run.RootRunId), _caller, sink, ct);
    }

    public async Task<AgentRunResult> AnswerAndResumeAsync(RunId runId, string requestId,
        string answer, IAgentEventSink sink, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);
        var run = await RequireHumanRequestAsync(runId, requestId, WorkflowHumanRequestKind.Input, ct);
        await orchestrator.AnswerAsync(runId, requestId, answer, _caller, ct);
        return await orchestrator.ResumeAsync(new RunId(run.RootRunId), _caller, sink, ct);
    }

    public async Task<AgentRunResult?> DecideApprovalAsync(RunId runId, string requestId,
        bool approved, IAgentEventSink sink, CancellationToken ct)
    {
        var run = await RequireHumanRequestAsync(runId, requestId, WorkflowHumanRequestKind.ToolApproval, ct);
        await orchestrator.DecideApprovalAsync(runId, requestId, approved, _caller, ct);
        // A denial records only the decision. Resuming is a separate, explicit action.
        return approved ? await orchestrator.ResumeAsync(new RunId(run.RootRunId), _caller, sink, ct) : null;
    }

    public async Task<AgentRunResult?> ReconcileAndResumeAsync(RunId runId, string executionId,
        string content, bool isError, string note, IAgentEventSink sink, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        var run = await RequireAsync(runId, ct);
        var execution = run.ToolExecutions?.SingleOrDefault(tool => tool.ExecutionId == executionId);
        if (execution is null || execution.State != WorkflowToolState.Started || execution.Result is not null)
            throw new WorkflowConflictException("Select a tool execution whose outcome is unknown.");
        await orchestrator.ReconcileToolAsync(runId, executionId,
            new ToolResult(isError, content, ContentTrust.Local), note, _caller, ct);
        var root = await RequireAsync(new RunId(run.RootRunId), ct);
        if (root.Control == WorkflowControl.Cancel) return null;
        return await orchestrator.ResumeAsync(new RunId(run.RootRunId), _caller, sink, ct);
    }

    public async Task PauseAsync(RunId runId, CancellationToken ct)
    {
        var run = await RequireAsync(runId, ct);
        await orchestrator.PauseAsync(new RunId(run.RootRunId), _caller, ct);
    }

    public async Task CancelAsync(RunId runId, CancellationToken ct)
    {
        var run = await RequireAsync(runId, ct);
        await orchestrator.CancelAsync(new RunId(run.RootRunId), _caller, ct);
    }

    private async Task<WorkflowCheckpoint> RequireAsync(RunId runId, CancellationToken ct)
    {
        var run = await store.GetAsync(runId, ct) ?? throw new ArgumentException("The selected workflow no longer exists.", nameof(runId));
        if (run.Request.Caller.Id != _caller.Id || run.Request.Caller.IsOwner != _caller.IsOwner)
            throw new UnauthorizedAccessException("This workflow belongs to another caller.");
        if ((await GraphRootIdsAsync(ct)).Contains(run.RootRunId))
            throw new WorkflowConflictException("Use the Task graphs tab to inspect or control a graph invocation.");
        return run;
    }

    private async Task<HashSet<string>> GraphRootIdsAsync(CancellationToken ct) => store is ITaskGraphStore graphs
        ? (await graphs.ListTaskGraphsAsync(ct)).Select(graph => graph.AgentBinding?.RootRunId)
            .OfType<string>().ToHashSet(StringComparer.Ordinal)
        : [];

    private async Task<WorkflowCheckpoint> RequireHumanRequestAsync(RunId runId, string requestId,
        WorkflowHumanRequestKind kind, CancellationToken ct)
    {
        var run = await RequireAsync(runId, ct);
        var request = run.HumanRequests?.SingleOrDefault(request => request.RequestId == requestId);
        if (request is null || request.Kind != kind || request.Response is not null)
            throw new WorkflowConflictException("Select an unanswered request of the matching kind.");
        return run;
    }
}

internal sealed record WorkflowRecoveryDetail(WorkflowCheckpoint Root, WorkflowCheckpoint Selected,
    IReadOnlyList<WorkflowCheckpoint> Invocations, IReadOnlyList<WorkflowJournalEntry> Journal,
    WorkflowUsage Usage, WorkflowBudgetSnapshot Budget);

internal static class DesktopRunSettings
{
    public static WorkflowBudget ParseBudget(string tokens, string seconds)
    {
        long? tokenLimit = null;
        TimeSpan? timeLimit = null;
        if (!string.IsNullOrWhiteSpace(tokens))
        {
            if (!long.TryParse(tokens.Trim(), out var parsed) || parsed <= 0)
                throw new ArgumentException("Token limit must be a positive whole number, or empty.");
            tokenLimit = parsed;
        }
        if (!string.IsNullOrWhiteSpace(seconds))
        {
            if (!int.TryParse(seconds.Trim(), out var parsed) || parsed <= 0)
                throw new ArgumentException("Execution limit must be a positive whole number of seconds, or empty.");
            timeLimit = TimeSpan.FromSeconds(parsed);
        }
        return new(MaxTotalTokens: tokenLimit, MaxExecutionTime: timeLimit);
    }
}
