using System.Text.Json;
using System.Collections.Concurrent;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed partial class AgentOrchestrator
{
    private readonly ConcurrentDictionary<string, ActiveWorkflow> _activeWorkflows = new(StringComparer.Ordinal);
    private sealed record ActiveWorkflow(string RootRunId, CancellationTokenSource Cancellation);

    public async Task<AgentRunResult> ResumeAsync(RunId runId, CallerIdentity caller,
        IAgentEventSink sink, CancellationToken ct)
    {
        var checkpoint = await RequireWorkflowAsync(runId, caller, ct);
        if (checkpoint.TaskId == "task-graph-root" && checkpoint.ParentRunId is null &&
            _workflowStore is ITaskGraphStore graphs && (await graphs.ListTaskGraphsAsync(ct))
                .Any(graph => graph.AgentBinding?.RootRunId == checkpoint.RunId))
            throw new WorkflowConflictException("The graph root is a non-executable ledger anchor. Resume its task graph instead.");
        var root = await _workflowStore!.GetAsync(new RunId(checkpoint.RootRunId), ct)
            ?? throw new WorkflowConflictException("Root workflow was not found.");
        if (root.Control == WorkflowControl.Cancel)
            throw new WorkflowCancelledException("Workflow was permanently cancelled.");
        if (checkpoint.Result is not null) return checkpoint.Result;
        if (checkpoint.Status == WorkflowStatus.BudgetExhausted)
            throw new WorkflowBudgetExceededException("This workflow exhausted its persistent budget and cannot resume.");
        if (checkpoint.Status == WorkflowStatus.Failed)
            throw new InvalidOperationException("This workflow is terminal and cannot resume.");
        if (checkpoint.LeaseOwner is not null)
            throw new WorkflowConflictException("This workflow is already running.");
        if (root.Control == WorkflowControl.Pause)
        {
            if ((await _workflowStore.ListAsync(ct)).Any(run => run.RootRunId == root.RunId && run.LeaseOwner is not null))
                throw new WorkflowConflictException("Pause is still settling. Resume after active invocations have stopped.");
            await _workflowStore.SaveAsync(root with { Control = WorkflowControl.None,
                Status = root.Status == WorkflowStatus.Paused ? WorkflowStatus.Interrupted : root.Status },
                root.Revision, "resume_requested", ct);
            checkpoint = await RequireWorkflowAsync(runId, caller, ct);
        }
        if (WorkflowIdentity.HasUnknownEffects(checkpoint))
            throw new WorkflowReconciliationRequiredException("The workflow contains tool actions with unknown outcomes. Reconcile them before resuming.");
        if ((checkpoint.HumanRequests ?? []).Any(request => request.Response is null) && checkpoint.Suspension is not null)
            return checkpoint.Suspension;
        foreach (var waiting in (checkpoint.ToolExecutions ?? []).Where(tool => tool.State == WorkflowToolState.WaitingForChild))
        {
            var child = await _workflowStore.GetAsync(new RunId(waiting.ChildRunId!), ct);
            if (child?.Suspension is not null && ((child.HumanRequests ?? []).Any(request => request.Response is null) ||
                WorkflowIdentity.HasUnknownEffects(child)))
                return checkpoint.Suspension ?? child.Suspension with { RunId = runId, SessionKey = checkpoint.Request.SessionKey };
        }
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
        if (tools[index].Result is not null || tools[index].State != WorkflowToolState.Started)
            throw new WorkflowConflictException("A recorded tool outcome cannot be replaced.");
        tools[index] = tools[index] with { Result = result, ReconciliationNote = note, State = WorkflowToolState.Completed };
        await _workflowStore!.SaveAsync(checkpoint with { ToolExecutions = tools,
            Status = tools.Any(tool => tool.Result is null && tool.State == WorkflowToolState.Started) ? WorkflowStatus.NeedsReconciliation
                : checkpoint.Control == WorkflowControl.Cancel ? WorkflowStatus.Cancelled : WorkflowStatus.Interrupted,
            UpdatedAt = DateTimeOffset.UtcNow }, checkpoint.Revision, "tool_reconciled", ct);
    }

    private async Task<WorkflowCheckpoint> RequireWorkflowAsync(RunId runId,
        CallerIdentity caller, CancellationToken ct)
    {
        var checkpoint = await (_workflowStore ?? throw new InvalidOperationException("Durable workflow storage is not configured."))
            .GetAsync(runId, ct) ?? throw new ArgumentException("Workflow was not found.", nameof(runId));
        if (checkpoint.SchemaVersion != 3) throw new InvalidOperationException("Unsupported workflow checkpoint version.");
        if (checkpoint.Request.Caller.Id != caller.Id || checkpoint.Request.Caller.IsOwner != caller.IsOwner)
            throw new UnauthorizedAccessException("Only the original caller with the original ownership level can resume or reconcile this workflow.");
        return checkpoint;
    }

    public Task AnswerAsync(RunId runId, string requestId, string answer, CallerIdentity caller, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);
        return RespondAsync(runId, requestId, answer, null, caller, ct);
    }

    public Task DecideApprovalAsync(RunId runId, string requestId, bool approved, CallerIdentity caller, CancellationToken ct) =>
        RespondAsync(runId, requestId, null, approved, caller, ct);

    private async Task RespondAsync(RunId runId, string requestId, string? answer, bool? approved,
        CallerIdentity caller, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var checkpoint = await RequireWorkflowAsync(runId, caller, ct);
            var root = await _workflowStore!.GetAsync(new RunId(checkpoint.RootRunId), ct);
            if (root?.Control == WorkflowControl.Cancel) throw new WorkflowCancelledException("Cancelled workflows cannot accept decisions.");
            var requests = (checkpoint.HumanRequests ?? []).ToList();
            var index = requests.FindIndex(request => request.RequestId == requestId);
            if (index < 0) throw new ArgumentException("The human request was not found.", nameof(requestId));
            var request = requests[index];
            if ((request.Kind == WorkflowHumanRequestKind.Input) != (answer is not null))
                throw new ArgumentException("This response does not match the human request kind.");
            if (request.Response is { } existing)
            {
                if (existing.Answer == answer && existing.Approved == approved && existing.CallerId == caller.Id) return;
                throw new WorkflowConflictException("This request already has a different response.");
            }
            requests[index] = request with { Response = new WorkflowHumanResponse(answer, approved, caller.Id, DateTimeOffset.UtcNow) };
            try
            {
                await _workflowStore.SaveAsync(checkpoint with { HumanRequests = requests }, checkpoint.Revision,
                    answer is null ? "approval_decided" : "input_answered", ct);
                return;
            }
            catch (WorkflowConflictException) when (attempt < 3) { }
        }
    }

    public Task PauseAsync(RunId runId, CallerIdentity caller, CancellationToken ct) =>
        RequestControlAsync(runId, caller, WorkflowControl.Pause, ct);
    public Task CancelAsync(RunId runId, CallerIdentity caller, CancellationToken ct) =>
        RequestControlAsync(runId, caller, WorkflowControl.Cancel, ct);

    private async Task RequestControlAsync(RunId runId, CallerIdentity caller, WorkflowControl control, CancellationToken ct)
    {
        string rootId;
        for (var attempt = 0; ; attempt++)
        {
            var selected = await RequireWorkflowAsync(runId, caller, ct);
            rootId = selected.RootRunId;
            var root = await RequireWorkflowAsync(new RunId(rootId), caller, ct);
            if (root.Control == WorkflowControl.Cancel)
            {
                if (control == WorkflowControl.Cancel) break;
                throw new WorkflowCancelledException("A permanent cancellation cannot be changed to pause.");
            }
            if (root.Result is not null) throw new WorkflowConflictException("A terminal workflow cannot be controlled.");
            if (control == WorkflowControl.Pause && root.Status is WorkflowStatus.BudgetExhausted or WorkflowStatus.Failed or WorkflowStatus.Succeeded)
                throw new WorkflowConflictException("A terminal workflow cannot be paused.");
            if (root.Control == control) break;
            try
            {
                await _workflowStore!.SaveAsync(root with { Control = control,
                    Status = root.LeaseOwner is not null ? root.Status
                        : WorkflowIdentity.HasUnknownEffects(root) ? WorkflowStatus.NeedsReconciliation
                        : control == WorkflowControl.Cancel ? WorkflowStatus.Cancelled : WorkflowStatus.Paused },
                    root.Revision, control == WorkflowControl.Cancel ? "cancel_requested" : "pause_requested", ct);
                break;
            }
            catch (WorkflowConflictException) when (attempt < 3) { }
        }
        foreach (var active in _activeWorkflows.Values.Where(active => active.RootRunId == rootId))
        {
            try { active.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task<AgentRunResult> SettleSuspensionAsync(RunProgress progress, WorkflowStatus status,
        string message, IAgentEventSink sink)
    {
        using var commitCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var root = await progress.Execution!.RootAsync(commitCt.Token);
        progress.Status = root.Control == WorkflowControl.Cancel ? AgentRunStatus.Cancelled
            : status == WorkflowStatus.AwaitingInput ? AgentRunStatus.AwaitingInput : AgentRunStatus.Paused;
        progress.FinalAssistantMessage = root.Control == WorkflowControl.Cancel ? "Workflow permanently cancelled." : message;
        SynchronizeWorkflowUsage(progress);
        var result = new AgentRunResult(progress.RunId, progress.Request.SessionKey, progress.FinalAssistantMessage,
            progress.Transcript.ToArray(), new Dictionary<string, object?> {
                ["rounds"] = progress.Rounds, ["totalTokens"] = progress.TotalTokens,
                ["workflowStatus"] = status.ToString(), ["workflowUsage"] = progress.Execution.Checkpoint.Usage }, progress.Status);
        result = await progress.Execution.SettleControlAsync(result, status, commitCt.Token);
        progress.Status = result.Status;
        progress.FinalAssistantMessage = result.FinalAssistantMessage;
        try { await PublishTerminalAsync(progress, sink, null, commitCt.Token); }
        catch (Exception error) { result = result with { Metadata = result.Metadata.Append(
            new KeyValuePair<string, object?>("eventDeliveryError", error.Message)).ToDictionary(pair => pair.Key, pair => pair.Value) }; }
        return result;
    }

    private IAsyncEnumerable<LlmDelta> StreamModelAsync(LlmRequest request, RunProgress progress, CancellationToken ct) =>
        progress.Execution?.StreamAsync(request, _llmClient, ct) ?? _llmClient.StreamAsync(request, ct);

    private async Task<AgentRunResult> SettleBudgetAsync(RunProgress progress, string reason, IAgentEventSink sink)
    {
        using var commitCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        progress.Status = AgentRunStatus.BudgetExhausted;
        progress.FinalAssistantMessage = reason + " Completed work is preserved; the task remains incomplete.";
        if (progress.PartialAssistant is { Length: > 0 } partial &&
            !progress.Transcript.Any(message => message.Role == ChatRole.Assistant && message.Content == partial.ToString().Trim()))
            progress.Transcript.Add(new ChatMessage(ChatRole.Assistant, "[Interrupted response]\n" + partial));
        SynchronizeWorkflowUsage(progress);
        var result = new AgentRunResult(progress.RunId, progress.Request.SessionKey, progress.FinalAssistantMessage,
            progress.Transcript.ToArray(), new Dictionary<string, object?> {
                ["rounds"] = progress.Rounds, ["totalTokens"] = progress.TotalTokens,
                ["stoppingReason"] = reason, ["workflowUsage"] = progress.Execution!.Checkpoint.Usage,
                ["rootWorkflowBudget"] = await progress.Execution.GetBudgetAsync(commitCt.Token) }, progress.Status);
        try { await progress.Execution.FinishAsync(result, commitCt.Token); }
        catch (WorkflowCancelledException)
        { return await SettleSuspensionAsync(progress, WorkflowStatus.Cancelled, "Workflow permanently cancelled.", sink); }
        catch (WorkflowSuspendedException error)
        { return await SettleSuspensionAsync(progress, error.Status, error.Message, sink); }
        try
        {
            if (progress.TranscriptLoaded)
                await _transcriptStore.ReplaceAsync(progress.Request.SessionKey, _transcriptSanitizer.Repair(progress.Transcript), commitCt.Token);
            await RecordSummaryAsync(progress, reason, commitCt.Token);
            await PublishTerminalAsync(progress, sink, reason, commitCt.Token);
        }
        catch (Exception error)
        {
            result = result with { Metadata = result.Metadata.Append(new KeyValuePair<string, object?>(
                "projectionError", error.Message)).ToDictionary(pair => pair.Key, pair => pair.Value) };
        }
        return result;
    }

    private static async Task ApplyBudgetDeadlineAsync(TimeSpan remaining, CancellationTokenSource budgetCancellation,
        CancellationToken stopTimer)
    {
        try
        {
            // Task.Delay has a finite platform range; split very long accepted budgets.
            while (remaining > TimeSpan.Zero)
            {
                var interval = remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining;
                await Task.Delay(interval, stopTimer);
                remaining -= interval;
            }
            budgetCancellation.Cancel();
        }
        catch (OperationCanceledException) when (stopTimer.IsCancellationRequested) { }
    }

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
