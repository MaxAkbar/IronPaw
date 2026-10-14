namespace IronPaw.AgentCore.Abstractions;

public enum WorkflowStatus
{
    Queued, Running, Interrupted, Succeeded, Failed, Cancelled,
    AwaitingInput, BudgetExhausted, NeedsReconciliation
}

/// <summary>Limits apply to new attempts and remain consumed across resume.</summary>
public sealed record WorkflowBudget(int MaxModelCalls = 32, int MaxToolCalls = 64);

public sealed record WorkflowUsage(int ModelCalls = 0, int ToolCalls = 0,
    long InputTokens = 0, long OutputTokens = 0, long CacheReadTokens = 0,
    long CacheWriteTokens = 0, int InterruptedModelCalls = 0);

public sealed record WorkflowModelDelta(string Kind, string? Text = null,
    ToolCall? ToolCall = null, UsageDelta? Usage = null);

public sealed record WorkflowModelCall(string Fingerprint,
    IReadOnlyList<WorkflowModelDelta>? Response = null, string? SystemPrompt = null);

public sealed record WorkflowToolExecution(string ExecutionId, ToolCall Call,
    ToolResult? Result = null, string? ReconciliationNote = null);

/// <summary>The versioned durable source of truth. Session/history stores are projections.</summary>
public sealed record WorkflowCheckpoint(
    string RunId, string RootRunId, string? ParentRunId, string TaskId,
    AgentRunRequest Request, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    int Revision = 0, int SchemaVersion = 1,
    WorkflowStatus Status = WorkflowStatus.Queued, string? LeaseOwner = null,
    IReadOnlyList<ChatMessage>? InitialTranscript = null,
    IReadOnlyList<WorkflowModelCall>? ModelCalls = null,
    IReadOnlyList<WorkflowToolExecution>? ToolExecutions = null,
    IReadOnlyList<ChatMessage>? Transcript = null,
    WorkflowBudget? Budget = null, WorkflowUsage? Usage = null,
    AgentRunResult? Result = null, string? Error = null);

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
}

public sealed class WorkflowConflictException(string message) : InvalidOperationException(message);
public sealed class WorkflowReconciliationRequiredException(string message) : InvalidOperationException(message);
public sealed class WorkflowBudgetExceededException(string message) : InvalidOperationException(message);
