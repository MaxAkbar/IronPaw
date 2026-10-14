namespace IronPaw.AgentCore.Abstractions;

public enum WorkflowStatus
{
    Queued, Running, Interrupted, Succeeded, Failed, Cancelled,
    AwaitingInput, BudgetExhausted, NeedsReconciliation, Paused
}

public enum WorkflowControl { None, Pause, Cancel }
public enum WorkflowToolState { Started, Planned, Completed, WaitingForChild }
public enum WorkflowHumanRequestKind { Input, ToolApproval }

public sealed record WorkflowHumanResponse(string? Answer, bool? Approved,
    string CallerId, DateTimeOffset RespondedAt);

public sealed record WorkflowHumanRequest(string RequestId, WorkflowHumanRequestKind Kind,
    string Prompt, string SourceKey, DateTimeOffset CreatedAt,
    string? ExecutionId = null, string? OperationFingerprint = null,
    WorkflowHumanResponse? Response = null);

/// <summary>Limits apply to new attempts and remain consumed across resume.</summary>
public sealed record WorkflowBudget(int MaxModelCalls = 32, int MaxToolCalls = 64,
    long? MaxTotalTokens = null, TimeSpan? MaxExecutionTime = null);

public enum WorkflowReservationState { Running, Settled, Unknown }
public sealed record WorkflowModelAttempt(string AttemptId, long ReservedTokens,
    int MaxOutputTokens, long ReportedTokens = 0, bool UsageReported = false,
    WorkflowReservationState State = WorkflowReservationState.Running, bool UsageComplete = false);
public sealed record WorkflowBudgetSnapshot(string RootRunId, WorkflowBudget Limits,
    long ChargedTokens, long ReservedTokens, TimeSpan ExecutionTime,
    TimeSpan? RemainingExecutionTime)
{
    public long? RemainingTokens => Limits.MaxTotalTokens is { } limit
        ? Math.Max(0, limit - ChargedTokens - ReservedTokens) : null;
}

public sealed record WorkflowUsage(int ModelCalls = 0, int ToolCalls = 0,
    long InputTokens = 0, long OutputTokens = 0, long CacheReadTokens = 0,
    long CacheWriteTokens = 0, int InterruptedModelCalls = 0);

public sealed record WorkflowModelDelta(string Kind, string? Text = null,
    ToolCall? ToolCall = null, UsageDelta? Usage = null);

public sealed record WorkflowModelCall(string Fingerprint,
    IReadOnlyList<WorkflowModelDelta>? Response = null, string? SystemPrompt = null,
    IReadOnlyList<WorkflowModelAttempt>? Attempts = null);

public sealed record WorkflowToolExecution(string ExecutionId, ToolCall Call,
    ToolResult? Result = null, string? ReconciliationNote = null,
    WorkflowToolState State = WorkflowToolState.Started, string? ChildRunId = null);

/// <summary>The versioned durable source of truth. Session/history stores are projections.</summary>
public sealed record WorkflowCheckpoint(
    string RunId, string RootRunId, string? ParentRunId, string TaskId,
    AgentRunRequest Request, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    int Revision = 0, int SchemaVersion = 3,
    WorkflowStatus Status = WorkflowStatus.Queued, string? LeaseOwner = null,
    IReadOnlyList<ChatMessage>? InitialTranscript = null,
    IReadOnlyList<WorkflowModelCall>? ModelCalls = null,
    IReadOnlyList<WorkflowToolExecution>? ToolExecutions = null,
    IReadOnlyList<ChatMessage>? Transcript = null,
    WorkflowBudget? Budget = null, WorkflowUsage? Usage = null,
    AgentRunResult? Result = null, string? Error = null,
    IReadOnlyList<WorkflowHumanRequest>? HumanRequests = null,
    WorkflowControl Control = WorkflowControl.None,
    AgentRunResult? Suspension = null);

public sealed record WorkflowJournalEntry(string RunId, int Revision,
    DateTimeOffset Timestamp, string EventType);

public interface IWorkflowStore
{
    Task CreateAsync(WorkflowCheckpoint checkpoint, CancellationToken ct);
    Task<WorkflowCheckpoint?> GetAsync(RunId runId, CancellationToken ct);
    Task<IReadOnlyList<WorkflowCheckpoint>> ListAsync(CancellationToken ct);
    /// <summary>Atomically save checkpoint, usage, and journal entry; reject stale revisions.</summary>
    Task<WorkflowCheckpoint> SaveAsync(WorkflowCheckpoint checkpoint, int expectedRevision,
        string eventType, CancellationToken ct);
    Task<IReadOnlyList<WorkflowJournalEntry>> ReadJournalAsync(RunId runId, CancellationToken ct);
    async Task<WorkflowBudgetSnapshot> GetBudgetAsync(RunId rootRunId, CancellationToken ct)
    {
        var root = await GetAsync(rootRunId, ct) ?? throw new WorkflowConflictException("Root workflow was not found.");
        var workflows = (await ListAsync(ct)).Where(run => run.RootRunId == rootRunId.Value).ToArray();
        var usage = await GetUsageAsync(rootRunId, ct);
        var charged = checked(usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens + usage.CacheWriteTokens);
        long reserved = 0;
        foreach (var attempt in workflows.SelectMany(run => run.ModelCalls ?? []).SelectMany(call => call.Attempts ?? []))
        {
            var remainder = Math.Max(0, attempt.ReservedTokens - attempt.ReportedTokens);
            if (attempt.State == WorkflowReservationState.Running) reserved = checked(reserved + remainder);
            else if (attempt.State == WorkflowReservationState.Unknown || !attempt.UsageComplete) charged = checked(charged + remainder);
        }
        return new(rootRunId.Value, root.Budget ?? new(), charged, reserved, TimeSpan.Zero,
            root.Budget?.MaxExecutionTime);
    }
    /// <summary>Aggregate the durable ledger across the root and all descendants.</summary>
    async Task<WorkflowUsage> GetUsageAsync(RunId rootRunId, CancellationToken ct)
    {
        var usage = (await ListAsync(ct)).Where(run => run.RootRunId == rootRunId.Value)
            .Select(run => run.Usage ?? new WorkflowUsage()).ToArray();
        return new WorkflowUsage(usage.Sum(x => x.ModelCalls), usage.Sum(x => x.ToolCalls),
            usage.Sum(x => x.InputTokens), usage.Sum(x => x.OutputTokens),
            usage.Sum(x => x.CacheReadTokens), usage.Sum(x => x.CacheWriteTokens),
            usage.Sum(x => x.InterruptedModelCalls));
    }
}

public interface IResumableAgentOrchestrator : IAgentOrchestrator
{
    Task<AgentRunResult> ResumeAsync(RunId runId, CallerIdentity caller,
        IAgentEventSink sink, CancellationToken ct);
    Task<IReadOnlyList<WorkflowCheckpoint>> ListWorkflowsAsync(CancellationToken ct);
    /// <summary>Supply an externally established outcome. This never executes the tool.</summary>
    Task ReconcileToolAsync(RunId runId, string executionId, ToolResult result,
        string note, CallerIdentity caller, CancellationToken ct);
    Task AnswerAsync(RunId runId, string requestId, string answer, CallerIdentity caller,
        CancellationToken ct) => throw new NotSupportedException();
    Task DecideApprovalAsync(RunId runId, string requestId, bool approved, CallerIdentity caller,
        CancellationToken ct) => throw new NotSupportedException();
    Task PauseAsync(RunId runId, CallerIdentity caller, CancellationToken ct) => throw new NotSupportedException();
    Task CancelAsync(RunId runId, CallerIdentity caller, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class WorkflowConflictException(string message) : InvalidOperationException(message);
public sealed class WorkflowReconciliationRequiredException(string message) : InvalidOperationException(message);
public sealed class WorkflowBudgetExceededException(string message) : InvalidOperationException(message);
public sealed class WorkflowCancelledException(string message) : InvalidOperationException(message);
public sealed class WorkflowSuspendedException(WorkflowStatus status, string message,
    string? childRunId = null) : InvalidOperationException(message)
{
    public WorkflowStatus Status { get; } = status;
    public string? ChildRunId { get; } = childRunId;
}

/// <summary>A tool whose only effect is collecting durable human input.</summary>
public interface IHumanInputTool : ITool
{
    string GetQuestion(ToolCall call);
}

/// <summary>Delegation uses a stable child invocation identity across suspension/replay.</summary>
public interface IDurableDelegationTool : ITool { }

public static class WorkflowIdentity
{
    public static RunId ChildRunId(RunId parentRunId, string executionId) => new(
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(parentRunId.Value + "/" + executionId))));
    public static bool HasUnknownEffects(WorkflowCheckpoint checkpoint) =>
        checkpoint.ToolExecutions?.Any(tool => tool.Result is null && tool.State == WorkflowToolState.Started) == true;
}
