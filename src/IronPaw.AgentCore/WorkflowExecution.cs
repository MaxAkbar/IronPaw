using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

/// <summary>Reconstructs a run from recorded responses. Only unrecorded work executes.</summary>
internal sealed class WorkflowExecution(IWorkflowStore store, WorkflowCheckpoint checkpoint)
{
    private int _modelCursor;
    private int _toolCursor;
    private WorkflowCheckpoint _committedCheckpoint = checkpoint;
    public WorkflowCheckpoint Checkpoint { get; private set; } = checkpoint;
    public int CurrentModelIndex => _modelCursor - 1;

    public async Task ClaimAsync(CancellationToken ct)
    {
        Checkpoint = Checkpoint with { Status = WorkflowStatus.Running,
            LeaseOwner = Guid.NewGuid().ToString("n"), Error = null };
        await SaveAsync("run_claimed", ct);
    }

    public async Task SaveAsync(string eventType, CancellationToken ct)
    {
        var proposed = Checkpoint;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var current = await store.GetAsync(new RunId(proposed.RunId), ct)
                    ?? throw new WorkflowConflictException("Workflow no longer exists.");
                if (current.Revision != _committedCheckpoint.Revision)
                {
                    // Human/control commands may change these fields while a call is in flight.
                    // Another execution owner may never replace this owner's work.
                    var changedField = current.LeaseOwner != _committedCheckpoint.LeaseOwner ? "owner"
                        : JsonSerializer.Serialize(current.ModelCalls) != JsonSerializer.Serialize(_committedCheckpoint.ModelCalls) ? "model records"
                        : JsonSerializer.Serialize(current.ToolExecutions) != JsonSerializer.Serialize(_committedCheckpoint.ToolExecutions) ? "tool records"
                        : current.Usage != _committedCheckpoint.Usage ? "usage" : null;
                    if (changedField is not null)
                        throw new WorkflowConflictException($"Another execution changed this workflow's {changedField}.");
                }
                var interactions = (current.HumanRequests ?? []).ToList();
                interactions.AddRange((proposed.HumanRequests ?? []).Where(request =>
                    interactions.All(existing => existing.RequestId != request.RequestId)));
                var merged = proposed with { Revision = current.Revision, Control = current.Control,
                    HumanRequests = interactions, UpdatedAt = DateTimeOffset.UtcNow };
                try
                {
                    Checkpoint = await store.SaveAsync(merged, current.Revision, eventType, ct);
                    _committedCheckpoint = Checkpoint;
                    return;
                }
                catch (WorkflowConflictException) when (attempt < 3) { }
            }
        }
        catch
        {
            Checkpoint = _committedCheckpoint;
            throw;
        }
    }

    public async Task<WorkflowCheckpoint> RootAsync(CancellationToken ct) =>
        await store.GetAsync(new RunId(Checkpoint.RootRunId), ct)
        ?? throw new WorkflowConflictException("Root workflow was not found.");

    public async Task CheckControlAsync(CancellationToken ct)
    {
        var root = await RootAsync(ct);
        if (root.Control == WorkflowControl.Cancel)
            throw new WorkflowCancelledException("Workflow was permanently cancelled.");
        if (root.Control == WorkflowControl.Pause)
            throw new WorkflowSuspendedException(WorkflowStatus.Paused, "Workflow paused.");
        ct.ThrowIfCancellationRequested();
    }

    public Task<WorkflowBudgetSnapshot> GetBudgetAsync(CancellationToken ct) =>
        store.GetBudgetAsync(new RunId(Checkpoint.RootRunId), ct);

    public async Task CheckResourcesAsync(CancellationToken ct, bool admittingWork = true)
    {
        var snapshot = await GetBudgetAsync(ct);
        if (snapshot.RemainingExecutionTime is { } remaining && remaining <= TimeSpan.Zero)
            throw new WorkflowBudgetExceededException("The shared execution-time budget is exhausted.");
        if (snapshot.Limits.MaxTotalTokens is { } limit &&
            (snapshot.ChargedTokens > limit || admittingWork && snapshot.RemainingTokens <= 0))
            throw new WorkflowBudgetExceededException("The shared token budget is exhausted.");
    }

    public async Task<WorkflowHumanRequest> AwaitHumanAsync(string sourceKey,
        WorkflowHumanRequestKind kind, string prompt, string? executionId,
        string? operationFingerprint, CancellationToken ct)
    {
        await CheckControlAsync(ct);
        var request = (Checkpoint.HumanRequests ?? []).SingleOrDefault(item => item.SourceKey == sourceKey);
        if (request is null)
        {
            request = new WorkflowHumanRequest(Guid.NewGuid().ToString("n"), kind, prompt,
                sourceKey, DateTimeOffset.UtcNow, executionId, operationFingerprint);
            Checkpoint = Checkpoint with { HumanRequests = (Checkpoint.HumanRequests ?? []).Append(request).ToArray() };
            await SaveAsync(kind == WorkflowHumanRequestKind.Input ? "input_requested" : "approval_requested", ct);
            request = Checkpoint.HumanRequests!.Single(item => item.RequestId == request.RequestId);
        }
        if (request.Kind != kind || request.ExecutionId != executionId || request.OperationFingerprint != operationFingerprint)
            throw new WorkflowConflictException("The requested human decision differs from its saved operation.");
        if (request.Response is null)
            throw new WorkflowSuspendedException(WorkflowStatus.AwaitingInput, request.Prompt);
        return request;
    }

    public async Task InitializeAsync(AgentRunRequest request, IReadOnlyList<ChatMessage> transcript,
        CancellationToken ct)
    {
        if (Checkpoint.InitialTranscript is not null) return;
        Checkpoint = Checkpoint with { Request = request, InitialTranscript = transcript.ToArray() };
        await SaveAsync("initialized", ct);
    }

    public ToolCall NormalizeCall(ToolCall call)
    {
        var executions = Checkpoint.ToolExecutions ?? [];
        if (_toolCursor >= executions.Count) return call;
        var recorded = executions[_toolCursor].Call;
        if (!SameCall(recorded, call))
            throw new WorkflowConflictException("Tool execution differs from the saved workflow. Resume stopped before executing it.");
        return recorded;
    }

    public async Task<ToolResult> ExecuteAsync(ITool tool, ToolCall call,
        ToolExecutionContext context, IToolExecutor executor, CancellationToken ct)
    {
        call = NormalizeCall(call);
        var index = _toolCursor++;
        var executions = (Checkpoint.ToolExecutions ?? []).ToList();
        WorkflowToolExecution execution;
        if (index < executions.Count)
        {
            execution = executions[index];
            if (execution.Result is not null) return execution.Result;
            if (execution.State == WorkflowToolState.Started)
                throw new WorkflowReconciliationRequiredException(
                    $"Tool execution '{execution.ExecutionId}' has an unknown outcome. Establish its outcome and reconcile it before resuming.");
        }
        else
        {
            await CheckControlAsync(ct);
            execution = new WorkflowToolExecution(Guid.NewGuid().ToString("n"), call, State: WorkflowToolState.Planned);
            executions.Add(execution);
            Checkpoint = Checkpoint with { ToolExecutions = executions };
            await SaveAsync("tool_planned", ct);
        }

        ToolResult result;
        if (tool is IHumanInputTool inputTool)
        {
            string question;
            try { question = inputTool.GetQuestion(call); }
            catch (ArgumentException error)
            {
                result = ToolResult.Error(error.Message);
                await CompleteToolAsync(executions, index, execution, result);
                return result;
            }
            var input = await AwaitHumanAsync("tool:" + execution.ExecutionId, WorkflowHumanRequestKind.Input,
                question, execution.ExecutionId, OperationFingerprint(tool, call), ct);
            result = ToolResult.Success(input.Response!.Answer!, ContentTrust.Trusted,
                new Dictionary<string, object?> { ["human_request_id"] = input.RequestId });
            await CompleteToolAsync(executions, index, execution, result);
            return result;
        }

        var root = await RootAsync(ct);
        if ((root.Request.ApprovalRequiredTools ?? []).Concat(Checkpoint.Request.ApprovalRequiredTools ?? [])
            .Contains(call.ToolName, StringComparer.OrdinalIgnoreCase))
        {
            var approval = await AwaitHumanAsync("tool:" + execution.ExecutionId, WorkflowHumanRequestKind.ToolApproval,
                $"Approve {call.ToolName} with arguments {call.Arguments.GetRawText()}?", execution.ExecutionId,
                OperationFingerprint(tool, call), ct);
            if (approval.Response!.Approved != true)
            {
                result = ToolResult.Error($"The user denied tool execution '{execution.ExecutionId}'.");
                await CompleteToolAsync(executions, index, execution, result);
                return result;
            }
        }

        if (execution.State == WorkflowToolState.Planned)
        {
            await CheckControlAsync(ct);
            await CheckResourcesAsync(ct);
            var usage = Checkpoint.Usage ?? new();
            var budget = Checkpoint.Budget ?? new();
            if (usage.ToolCalls >= budget.MaxToolCalls)
                throw new WorkflowBudgetExceededException("The persistent tool call budget is exhausted.");
            execution = execution with {
                State = tool is IDurableDelegationTool ? WorkflowToolState.WaitingForChild : WorkflowToolState.Started,
                ChildRunId = tool is IDurableDelegationTool
                    ? WorkflowIdentity.ChildRunId(new RunId(Checkpoint.RunId), execution.ExecutionId).Value : null };
            executions = executions.ToList();
            executions[index] = execution;
            Checkpoint = Checkpoint with { ToolExecutions = executions.ToArray(),
                Usage = usage with { ToolCalls = usage.ToolCalls + 1 } };
            await SaveAsync("tool_started", ct);
        }
        await CheckControlAsync(ct);
        // Nothing external belongs in the store's transaction/retry callback.
        result = await executor.ExecuteAsync(tool, call, context with {
            ToolExecutionId = execution.ExecutionId, RootRunId = new RunId(Checkpoint.RootRunId) }, ct);
        if (result.Details?.ContainsKey("error") == true)
        {
            if (tool is IDurableDelegationTool)
                throw new WorkflowSuspendedException(WorkflowStatus.Interrupted,
                    "The child workflow was interrupted. Inspect and resume its saved invocation.", execution.ChildRunId);
            throw new WorkflowReconciliationRequiredException(
                $"Tool execution '{execution.ExecutionId}' failed after starting; its effects require reconciliation.");
        }
        await CompleteToolAsync(executions, index, execution, result);
        return result;
    }

    private async Task CompleteToolAsync(List<WorkflowToolExecution> executions, int index,
        WorkflowToolExecution execution, ToolResult result)
    {
        var completed = executions.ToArray();
        completed[index] = execution with { Result = result, State = WorkflowToolState.Completed };
        Checkpoint = Checkpoint with { ToolExecutions = completed };
        // Preserve a returned outcome even if cancellation races with its return.
        using var commitCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await SaveAsync("tool_completed", commitCt.Token);
    }

    public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, ILlmClient client,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var index = _modelCursor++;
        await CheckControlAsync(ct);
        var calls = (Checkpoint.ModelCalls ?? []).ToList();
        var recorded = index < calls.Count ? calls[index] : null;
        request = request with { SystemPrompt = recorded?.SystemPrompt ?? request.SystemPrompt };
        var fingerprint = Fingerprint(request);
        if (recorded is not null && recorded.Fingerprint != fingerprint)
            throw new WorkflowConflictException("Model inputs or tool schemas differ from the saved workflow. Resume stopped.");
        if (recorded?.Response is { } response)
        {
            foreach (var delta in response) { ct.ThrowIfCancellationRequested(); yield return Decode(delta); }
            yield return new CompletedDelta();
            yield break;
        }

        var usage = Checkpoint.Usage ?? new();
        var budget = Checkpoint.Budget ?? new();
        if (usage.ModelCalls >= budget.MaxModelCalls)
            throw new WorkflowBudgetExceededException("The persistent model call budget is exhausted.");
        var snapshot = await GetBudgetAsync(ct);
        await CheckResourcesAsync(ct);
        long reservation = 0;
        var outputLimit = request.MaxOutputTokens;
        if (snapshot.RemainingTokens is { } remainingTokens)
        {
            // UTF-8 payload bytes plus framing deliberately overestimate ordinary text.
            // Provider tokenization and receipts remain the authority for actual usage.
            var inputEstimate = checked((long)Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new {
                request.SystemPrompt, request.Messages, request.Tools })) + 1024L +
                64L * (request.Messages.Count + request.Tools.Count));
            if (remainingTokens <= inputEstimate)
                throw new WorkflowBudgetExceededException("The shared token budget cannot admit this model input and an output token.");
            outputLimit = (int)Math.Min(outputLimit, remainingTokens - inputEstimate);
            reservation = checked(inputEstimate + outputLimit);
            request = request with { MaxOutputTokens = outputLimit, MaxAutoContinuationPasses = 0 };
        }
        if (snapshot.RemainingExecutionTime is { } remainingTime)
            request = request with { Timeout = request.Timeout < remainingTime ? request.Timeout : remainingTime };
        var attempts = (recorded?.Attempts ?? []).Select(attempt => attempt.State == WorkflowReservationState.Running
            ? attempt with { State = WorkflowReservationState.Unknown } : attempt).ToList();
        attempts.Add(new WorkflowModelAttempt(Guid.NewGuid().ToString("n"), reservation, outputLimit));
        var call = new WorkflowModelCall(fingerprint, SystemPrompt: request.SystemPrompt, Attempts: attempts.ToArray());
        if (recorded is null) calls.Add(call); else calls[index] = call;
        Checkpoint = Checkpoint with { ModelCalls = calls, Usage = usage with {
            ModelCalls = usage.ModelCalls + 1,
            InterruptedModelCalls = usage.InterruptedModelCalls + (recorded is null ? 0 : 1) } };
        await SaveAsync("model_started", ct);
        var deltas = new List<WorkflowModelDelta>();
        var completed = false;
        var inputReported = false;
        var outputReported = false;
        var settled = false;
        try
        {
            await foreach (var delta in client.StreamAsync(request, ct))
            {
                if (delta is CompletedDelta) { completed = true; continue; }
                if (delta is LlmErrorDelta error) throw new InvalidOperationException($"LLM returned an error: {error.Error}");
                deltas.Add(Encode(delta));
                if (delta is UsageDelta tokens)
                {
                    if (tokens.InputTokens < 0 || tokens.OutputTokens < 0 || tokens.CacheReadTokens < 0 || tokens.CacheWriteTokens < 0)
                        throw new InvalidOperationException("Provider usage cannot be negative.");
                    usage = Checkpoint.Usage!;
                    var reported = checked((tokens.InputTokens ?? 0) + (tokens.OutputTokens ?? 0) +
                        (tokens.CacheReadTokens ?? 0) + (tokens.CacheWriteTokens ?? 0));
                    call = Checkpoint.ModelCalls![index];
                    attempts = call.Attempts!.ToList();
                    inputReported |= tokens.InputTokens.HasValue;
                    outputReported |= tokens.OutputTokens.HasValue;
                    attempts[^1] = attempts[^1] with { ReportedTokens = checked(attempts[^1].ReportedTokens + reported),
                        UsageReported = attempts[^1].UsageReported || tokens.InputTokens.HasValue || tokens.OutputTokens.HasValue ||
                            tokens.CacheReadTokens.HasValue || tokens.CacheWriteTokens.HasValue,
                        UsageComplete = inputReported && outputReported };
                    var updatedCalls = Checkpoint.ModelCalls.ToArray();
                    updatedCalls[index] = call with { Attempts = attempts.ToArray() };
                    Checkpoint = Checkpoint with { ModelCalls = updatedCalls, Usage = usage with {
                        InputTokens = checked(usage.InputTokens + (tokens.InputTokens ?? 0)),
                        OutputTokens = checked(usage.OutputTokens + (tokens.OutputTokens ?? 0)),
                        CacheReadTokens = checked(usage.CacheReadTokens + (tokens.CacheReadTokens ?? 0)),
                        CacheWriteTokens = checked(usage.CacheWriteTokens + (tokens.CacheWriteTokens ?? 0)) } };
                    using var usageCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await SaveAsync("model_usage", usageCt.Token);
                }
                yield return delta;
            }
            if (!completed) throw new InvalidOperationException("The model stream ended before a completion marker.");
            var completedCalls = Checkpoint.ModelCalls!.ToArray();
            call = completedCalls[index];
            attempts = call.Attempts!.ToList();
            attempts[^1] = attempts[^1] with { State = WorkflowReservationState.Settled };
            completedCalls[index] = call with { Response = deltas.ToArray(), Attempts = attempts.ToArray() };
            Checkpoint = Checkpoint with { ModelCalls = completedCalls };
            using var responseCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await SaveAsync("model_completed", responseCt.Token);
            settled = true;
            await CheckResourcesAsync(responseCt.Token, admittingWork: false);
            yield return new CompletedDelta();
        }
        finally
        {
            if (!settled && Checkpoint.ModelCalls is { } activeCalls && index < activeCalls.Count &&
                activeCalls[index].Attempts?.LastOrDefault() is { State: WorkflowReservationState.Running })
            {
                var interruptedCalls = activeCalls.ToArray();
                var interruptedAttempts = interruptedCalls[index].Attempts!.ToArray();
                interruptedAttempts[^1] = interruptedAttempts[^1] with { State = WorkflowReservationState.Unknown };
                interruptedCalls[index] = interruptedCalls[index] with { Attempts = interruptedAttempts };
                Checkpoint = Checkpoint with { ModelCalls = interruptedCalls };
                using var commitCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await SaveAsync("model_interrupted", commitCt.Token);
            }
        }
    }

    public async Task FinishAsync(AgentRunResult result, CancellationToken ct)
    {
        var waiting = result.Status is AgentRunStatus.AwaitingInput or AgentRunStatus.Paused;
        Checkpoint = Checkpoint with { Result = waiting ? null : result, Suspension = waiting ? result : null, Transcript = result.Transcript,
            Status = result.Status == AgentRunStatus.BudgetExhausted && WorkflowIdentity.HasUnknownEffects(Checkpoint)
                ? WorkflowStatus.NeedsReconciliation : result.Status switch {
                AgentRunStatus.Succeeded => WorkflowStatus.Succeeded,
                AgentRunStatus.AwaitingInput => WorkflowStatus.AwaitingInput,
                AgentRunStatus.Paused => WorkflowStatus.Paused,
                AgentRunStatus.RoundLimitReached or AgentRunStatus.BudgetExhausted => WorkflowStatus.BudgetExhausted,
                AgentRunStatus.Cancelled => WorkflowStatus.Cancelled,
                _ => WorkflowStatus.Failed }, LeaseOwner = null, Error = null };
        await SaveAsync("run_finished", ct);
    }

    public async Task InterruptAsync(Exception error, IReadOnlyList<ChatMessage> transcript,
        CancellationToken ct)
    {
        Checkpoint = Checkpoint with { Transcript = transcript.ToArray(), LeaseOwner = null,
            Status = WorkflowIdentity.HasUnknownEffects(Checkpoint)
                ? WorkflowStatus.NeedsReconciliation
                : error is WorkflowBudgetExceededException ? WorkflowStatus.BudgetExhausted
                : error is OperationCanceledException ? WorkflowStatus.Cancelled : WorkflowStatus.Interrupted,
            Error = error.Message };
        await SaveAsync("run_interrupted", ct);
    }

    public async Task<AgentRunResult> SettleControlAsync(AgentRunResult result, WorkflowStatus status, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var root = await RootAsync(ct);
            var cancelled = root.Control == WorkflowControl.Cancel;
            var paused = root.Control == WorkflowControl.Pause;
            var normalized = result with {
                Status = cancelled ? AgentRunStatus.Cancelled : paused ? AgentRunStatus.Paused : result.Status,
                FinalAssistantMessage = cancelled ? "Workflow permanently cancelled." : paused ? "Workflow paused." : result.FinalAssistantMessage };
            Checkpoint = Checkpoint with { Transcript = normalized.Transcript, LeaseOwner = null,
                Status = WorkflowIdentity.HasUnknownEffects(Checkpoint) ? WorkflowStatus.NeedsReconciliation
                    : cancelled ? WorkflowStatus.Cancelled : paused ? WorkflowStatus.Paused : status,
                Result = cancelled ? normalized : null, Suspension = cancelled ? null : normalized, Error = null };
            try
            {
                await SaveAsync(cancelled ? "cancel_settled" : "run_suspended", ct);
                return normalized;
            }
            catch (WorkflowCancelledException) when (attempt < 3) { }
            catch (WorkflowSuspendedException) when (attempt < 3) { }
        }
    }

    private static string OperationFingerprint(ITool tool, ToolCall call) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tool.Descriptor.Name, call.Arguments,
            tool.Descriptor.ParametersSchema, tool.Descriptor.OwnerOnly, tool.Descriptor.Group }))));

    private static bool SameCall(ToolCall a, ToolCall b) =>
        a.ToolName == b.ToolName && a.Arguments.GetRawText() == b.Arguments.GetRawText();
    private static string Fingerprint(LlmRequest request) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request.Provider, request.Model,
            request.SystemPrompt, request.Messages, request.Tools, request.MaxOutputTokens }))));
    private static WorkflowModelDelta Encode(LlmDelta delta) => delta switch {
        AssistantTextDelta text => new("text", Text: text.Text),
        ToolCallDelta tool => new("tool", ToolCall: new(tool.ToolCallId, tool.ToolName, tool.Arguments.Clone())),
        UsageDelta usage => new("usage", Usage: usage),
        _ => throw new InvalidOperationException($"Unsupported durable model delta '{delta.GetType().Name}'.") };
    private static LlmDelta Decode(WorkflowModelDelta delta) => delta.Kind switch {
        "text" => new AssistantTextDelta(delta.Text!),
        "tool" => new ToolCallDelta(delta.ToolCall!.ToolCallId, delta.ToolCall.ToolName, delta.ToolCall.Arguments),
        "usage" => delta.Usage!,
        _ => throw new InvalidOperationException($"Unsupported saved model delta '{delta.Kind}'.") };
}
