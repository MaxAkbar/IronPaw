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

    public async Task ClaimAsync(CancellationToken ct)
    {
        Checkpoint = Checkpoint with { Status = WorkflowStatus.Running,
            LeaseOwner = Guid.NewGuid().ToString("n"), Error = null };
        await SaveAsync("run_claimed", ct);
    }

    public async Task SaveAsync(string eventType, CancellationToken ct)
    {
        try
        {
            Checkpoint = await store.SaveAsync(Checkpoint with { UpdatedAt = DateTimeOffset.UtcNow },
                Checkpoint.Revision, eventType, ct);
            _committedCheckpoint = Checkpoint;
        }
        catch
        {
            Checkpoint = _committedCheckpoint;
            throw;
        }
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
        if (index < executions.Count)
        {
            return executions[index].Result ?? throw new WorkflowReconciliationRequiredException(
                $"Tool execution '{executions[index].ExecutionId}' has an unknown outcome. Establish its outcome and reconcile it before resuming.");
        }
        var usage = Checkpoint.Usage ?? new();
        var budget = Checkpoint.Budget ?? new();
        if (usage.ToolCalls >= budget.MaxToolCalls)
            throw new WorkflowBudgetExceededException("The persistent tool call budget is exhausted.");
        var execution = new WorkflowToolExecution(Guid.NewGuid().ToString("n"), call);
        executions.Add(execution);
        Checkpoint = Checkpoint with { ToolExecutions = executions,
            Usage = usage with { ToolCalls = usage.ToolCalls + 1 } };
        await SaveAsync("tool_started", ct);
        // Nothing external belongs in the store's transaction/retry callback.
        var result = await executor.ExecuteAsync(tool, call, context with {
            ToolExecutionId = execution.ExecutionId, RootRunId = new RunId(Checkpoint.RootRunId) }, ct);
        if (result.Details?.ContainsKey("error") == true)
            throw new WorkflowReconciliationRequiredException(
                $"Tool execution '{execution.ExecutionId}' failed after starting; its effects require reconciliation.");
        executions[index] = execution with { Result = result };
        Checkpoint = Checkpoint with { ToolExecutions = executions };
        // Preserve a returned outcome even if cancellation races with its return.
        using var commitCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await SaveAsync("tool_completed", commitCt.Token);
        return result;
    }

    public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, ILlmClient client,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var index = _modelCursor++;
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
        var call = new WorkflowModelCall(fingerprint, SystemPrompt: request.SystemPrompt);
        if (recorded is null) calls.Add(call); else calls[index] = call;
        Checkpoint = Checkpoint with { ModelCalls = calls, Usage = usage with {
            ModelCalls = usage.ModelCalls + 1,
            InterruptedModelCalls = usage.InterruptedModelCalls + (recorded is null ? 0 : 1) } };
        await SaveAsync("model_started", ct);
        var deltas = new List<WorkflowModelDelta>();
        var completed = false;
        await foreach (var delta in client.StreamAsync(request, ct))
        {
            if (delta is CompletedDelta) { completed = true; continue; }
            if (delta is LlmErrorDelta error) throw new InvalidOperationException($"LLM returned an error: {error.Error}");
            deltas.Add(Encode(delta));
            if (delta is UsageDelta tokens)
            {
                usage = Checkpoint.Usage!;
                Checkpoint = Checkpoint with { Usage = usage with {
                    InputTokens = usage.InputTokens + (tokens.InputTokens ?? 0),
                    OutputTokens = usage.OutputTokens + (tokens.OutputTokens ?? 0),
                    CacheReadTokens = usage.CacheReadTokens + (tokens.CacheReadTokens ?? 0),
                    CacheWriteTokens = usage.CacheWriteTokens + (tokens.CacheWriteTokens ?? 0) } };
                using var usageCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await SaveAsync("model_usage", usageCt.Token);
            }
            yield return delta;
        }
        if (!completed) throw new InvalidOperationException("The model stream ended before a completion marker.");
        calls[index] = call with { Response = deltas };
        Checkpoint = Checkpoint with { ModelCalls = calls };
        using var responseCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await SaveAsync("model_completed", responseCt.Token);
        yield return new CompletedDelta();
    }

    public async Task FinishAsync(AgentRunResult result, CancellationToken ct)
    {
        Checkpoint = Checkpoint with { Result = result, Transcript = result.Transcript,
            Status = result.Status switch {
                AgentRunStatus.Succeeded => WorkflowStatus.Succeeded,
                AgentRunStatus.AwaitingInput => WorkflowStatus.AwaitingInput,
                AgentRunStatus.RoundLimitReached => WorkflowStatus.BudgetExhausted,
                AgentRunStatus.Cancelled => WorkflowStatus.Cancelled,
                _ => WorkflowStatus.Failed }, LeaseOwner = null, Error = null };
        await SaveAsync("run_finished", ct);
    }

    public async Task InterruptAsync(Exception error, IReadOnlyList<ChatMessage> transcript,
        CancellationToken ct)
    {
        Checkpoint = Checkpoint with { Transcript = transcript.ToArray(), LeaseOwner = null,
            Status = Checkpoint.ToolExecutions?.Any(tool => tool.Result is null) == true
                ? WorkflowStatus.NeedsReconciliation
                : error is WorkflowBudgetExceededException ? WorkflowStatus.BudgetExhausted
                : error is OperationCanceledException ? WorkflowStatus.Cancelled : WorkflowStatus.Interrupted,
            Error = error.Message };
        await SaveAsync("run_interrupted", ct);
    }

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
