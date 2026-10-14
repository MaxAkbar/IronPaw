using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed partial class AgentOrchestrator
{
    public async Task<AgentRunResult> ResumeAsync(RunId runId, CallerIdentity caller,
        IAgentEventSink sink, CancellationToken ct)
    {
        var checkpoint = await RequireWorkflowAsync(runId, caller, ct);
        if (checkpoint.Result is not null) return checkpoint.Result;
        if (checkpoint.Status == WorkflowStatus.BudgetExhausted)
            throw new WorkflowBudgetExceededException("This workflow exhausted its persistent budget and cannot resume.");
        if (checkpoint.Status == WorkflowStatus.Failed)
            throw new InvalidOperationException("This workflow is terminal and cannot resume.");
        if (checkpoint.LeaseOwner is not null)
            throw new WorkflowConflictException("This workflow is already running.");
        if (checkpoint.ToolExecutions?.Any(tool => tool.Result is null) == true)
            throw new WorkflowReconciliationRequiredException("The workflow contains tool actions with unknown outcomes. Reconcile them before resuming.");
        return await RunCoreAsync(checkpoint.Request, sink, ct, checkpoint);
    }

    public Task<IReadOnlyList<WorkflowCheckpoint>> ListWorkflowsAsync(CancellationToken ct) =>
        (_workflowStore ?? throw new InvalidOperationException("Durable workflow storage is not configured.")).ListAsync(ct);

    public async Task ReconcileToolAsync(RunId runId, string executionId, ToolResult result,
        string note, CallerIdentity caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        var checkpoint = await RequireWorkflowAsync(runId, caller, ct);
        if (checkpoint.LeaseOwner is not null)
            throw new WorkflowConflictException("A running workflow cannot be reconciled.");
        var tools = (checkpoint.ToolExecutions ?? []).ToList();
        var index = tools.FindIndex(tool => tool.ExecutionId == executionId);
        if (index < 0) throw new ArgumentException("Tool execution was not found.", nameof(executionId));
        if (tools[index].Result is not null)
            throw new WorkflowConflictException("A recorded tool outcome cannot be replaced.");
        tools[index] = tools[index] with { Result = result, ReconciliationNote = note };
        await _workflowStore!.SaveAsync(checkpoint with { ToolExecutions = tools,
            Status = tools.Any(tool => tool.Result is null) ? WorkflowStatus.NeedsReconciliation : WorkflowStatus.Interrupted,
            UpdatedAt = DateTimeOffset.UtcNow }, checkpoint.Revision, "tool_reconciled", ct);
    }

    private async Task<WorkflowCheckpoint> RequireWorkflowAsync(RunId runId,
        CallerIdentity caller, CancellationToken ct)
    {
        var checkpoint = await (_workflowStore ?? throw new InvalidOperationException("Durable workflow storage is not configured."))
            .GetAsync(runId, ct) ?? throw new ArgumentException("Workflow was not found.", nameof(runId));
        if (checkpoint.SchemaVersion != 1) throw new InvalidOperationException("Unsupported workflow checkpoint version.");
        if (checkpoint.Request.Caller.Id != caller.Id || checkpoint.Request.Caller.IsOwner != caller.IsOwner)
            throw new UnauthorizedAccessException("Only the original caller with the original ownership level can resume or reconcile this workflow.");
        return checkpoint;
    }

    private IAsyncEnumerable<LlmDelta> StreamModelAsync(LlmRequest request, RunProgress progress, CancellationToken ct) =>
        progress.Execution?.StreamAsync(request, _llmClient, ct) ?? _llmClient.StreamAsync(request, ct);

    private Task<ToolResult> ExecuteToolAsync(ITool tool, ToolCall call, ToolExecutionContext context,
        RunProgress progress, CancellationToken ct) => progress.Execution?.ExecuteAsync(tool, call, context, _toolExecutor, ct)
            ?? _toolExecutor.ExecuteAsync(tool, call, context, ct);

    private static bool AlreadyAccounted(SessionEntry entry, RunId runId)
    {
        if (entry.Diagnostics?.TryGetValue("accountedRunId", out var value) != true) return false;
        return value is string text ? text == runId.Value
            : value is JsonElement { ValueKind: JsonValueKind.String } json && json.GetString() == runId.Value;
    }

    private static void SynchronizeWorkflowUsage(RunProgress progress)
    {
        if (progress.Execution?.Checkpoint.Usage is not { } usage) return;
        progress.InputTokens = usage.InputTokens;
        progress.OutputTokens = usage.OutputTokens;
        progress.CacheReadTokens = usage.CacheReadTokens;
        progress.CacheWriteTokens = usage.CacheWriteTokens;
    }
}
