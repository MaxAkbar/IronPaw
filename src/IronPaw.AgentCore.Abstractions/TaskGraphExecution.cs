using System.Collections.Immutable;

namespace IronPaw.AgentCore.Abstractions;

public enum TaskGraphTaskStatus { Pending, Running, AwaitingAcceptance, Succeeded, Failed, Cancelled, BudgetExhausted, NeedsReconciliation }
public enum TaskAcceptanceOutcome { Inconclusive, Accepted, Rejected, BudgetExhausted }

public sealed record TaskAcceptanceDecision(TaskAcceptanceOutcome Outcome, string VerifierId, string Explanation);
public sealed record TaskAcceptanceReceipt(TaskAcceptanceOutcome Outcome, string VerifierId, string Explanation,
    string GraphFingerprint, string TaskId, string AttemptId, string ResultDigest, string Revision, DateTimeOffset RecordedAt);

public sealed record TaskGraphTaskState(string TaskId, TaskGraphTaskStatus Status = TaskGraphTaskStatus.Pending,
    string? AttemptId = null, string? LeaseToken = null, WorkflowTaskResult? Result = null,
    TaskAcceptanceReceipt? Acceptance = null, string? Error = null, string? ReconciliationNote = null,
    TaskAttemptInputs? Inputs = null)
{
    public ImmutableArray<TaskVerificationInvocation> Verifications { get; init; } = [];
}

/// <summary>Separate graph schema; concurrency is an immutable limit per accepted graph.</summary>
public sealed record TaskGraphCheckpoint(WorkflowTaskGraph Graph, string DefinitionFingerprint,
    CallerIdentity Caller, int MaxConcurrency, ImmutableArray<TaskGraphTaskState> Tasks,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Revision = 0, int SchemaVersion = 1,
    WorkflowControl Control = WorkflowControl.None, TaskGraphAgentBinding? AgentBinding = null);

/// <summary>A persisted attempt and its fencing token. A saved proposal requires verification only.</summary>
public sealed record TaskGraphClaim(string GraphId, string GraphFingerprint, WorkflowTaskDefinition Task,
    string AttemptId, string LeaseToken, WorkflowTaskResult? ProposedResult = null, TaskAttemptInputs? Inputs = null);

public sealed record TaskGraphJournalEntry(string GraphId, int Revision, DateTimeOffset Timestamp,
    string EventType, string? TaskId = null, string? AttemptId = null);

/// <summary>
/// Host-owned execution boundary. The host enforces task resources, budgets and isolation.
/// Implementations return established terminal outcomes and use AttemptId for idempotency.
/// </summary>
public interface IWorkflowTaskExecutor
{
    Task<WorkflowTaskResult> ExecuteAsync(TaskGraphClaim claim, CancellationToken ct);
}

/// <summary>Trusted, read-only, idempotent verification of the exact proposed revision and artifacts.</summary>
public interface ITaskAcceptanceVerifier
{
    Task<TaskAcceptanceDecision> VerifyAsync(TaskGraphClaim claim, WorkflowTaskResult result, CancellationToken ct);
}

/// <summary>Atomic graph commands; accepted definitions and established outcomes cannot be replaced.</summary>
public interface ITaskGraphStore
{
    Task<TaskGraphCheckpoint> AcceptTaskGraphAsync(WorkflowTaskGraph graph, CallerIdentity caller,
        int maxConcurrency, CancellationToken ct);
    Task<TaskGraphCheckpoint?> GetTaskGraphAsync(string graphId, CancellationToken ct);
    Task<IReadOnlyList<TaskGraphCheckpoint>> ListTaskGraphsAsync(CancellationToken ct);
    Task<TaskGraphClaim?> ClaimTaskAsync(string graphId, CallerIdentity caller, CancellationToken ct,
        IReadOnlyCollection<string>? excludedTaskIds = null);
    Task<TaskGraphCheckpoint> RecordTaskResultAsync(TaskGraphClaim claim, WorkflowTaskResult result, CancellationToken ct);
    Task<TaskGraphCheckpoint> RecordTaskAcceptanceAsync(TaskGraphClaim claim, TaskAcceptanceDecision decision, CancellationToken ct);
    Task<TaskGraphCheckpoint> AbandonTaskClaimAsync(TaskGraphClaim claim, string reason, CancellationToken ct);
    Task<TaskGraphCheckpoint> ReconcileTaskAsync(string graphId, string taskId, WorkflowTaskResult result,
        string note, CallerIdentity caller, CancellationToken ct);
    Task<TaskGraphCheckpoint> ControlTaskGraphAsync(string graphId, WorkflowControl control, CallerIdentity caller, CancellationToken ct);
    Task<IReadOnlyList<TaskGraphJournalEntry>> ReadTaskGraphJournalAsync(string graphId, CancellationToken ct);
}
