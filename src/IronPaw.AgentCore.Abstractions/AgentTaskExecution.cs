using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IronPaw.AgentCore.Abstractions;

public sealed record TaskArtifactVerificationRule(string TaskId, string CriterionId);

public enum TaskDependencyMode { BaseCommitOnly, AcceptedChanges }

/// <summary>Exact accepted predecessor outcome, captured atomically before a consumer starts.</summary>
public sealed record TaskDependencyReceipt(string TaskId, string AttemptId, string ResultDigest,
    string Revision, TaskAcceptanceReceipt Acceptance);

public sealed record TaskAttemptInputs(string GraphFingerprint, string TaskId, string AttemptId,
    string BaseCommit, ImmutableArray<TaskDependencyReceipt> Dependencies, string Digest);

/// <summary>Host-resolved sealed producer, never an arbitrary artifact location from the model.</summary>
public sealed record TaskDependencyInput(WorkflowTaskDefinition Task, string AttemptId,
    WorkflowTaskResult Result, TaskAcceptanceReceipt Acceptance, TaskAttemptInputs Inputs);

/// <summary>Immutable host configuration accepted before a bound graph can dispatch tasks.</summary>
public sealed record TaskGraphAgentBinding(string RootRunId, string SourceWorkspaceRoot, string BaseCommit,
    string WorkspaceStoreRoot, string Provider, string Model, ToolSelectionPolicy RootToolPolicy,
    WorkflowBudget SharedBudget, string AgentId = "graph-agent", string? SystemPrompt = null,
    int MaxRounds = 8, int MaxOutputTokens = 4096)
{
    /// <summary>Explicit host mappings for artifact-integrity criteria only; never implies semantic/test success.</summary>
    public ImmutableArray<TaskArtifactVerificationRule> ArtifactVerificationRules { get; init; } = [];
    public TaskDependencyMode DependencyMode { get; init; } = TaskDependencyMode.BaseCommitOnly;
    public TaskVerificationAuthority VerificationAuthority { get; init; }
    public ImmutableArray<TaskVerificationProfile> VerificationProfiles { get; init; } = [];
    public ImmutableArray<TaskCommandVerificationRule> CommandVerificationRules { get; init; } = [];
}

public sealed record TaskGraphAgentOptions(string SourceWorkspaceRoot, string WorkspaceStoreRoot,
    string BaseRevision, string Provider, string Model, ToolSelectionPolicy RootToolPolicy, WorkflowBudget SharedBudget,
    string AgentId = "graph-agent", string? SystemPrompt = null, int MaxRounds = 8, int MaxOutputTokens = 4096)
{
    public ImmutableArray<TaskArtifactVerificationRule> ArtifactVerificationRules { get; init; } = [];
    public TaskDependencyMode DependencyMode { get; init; } = TaskDependencyMode.AcceptedChanges;
    public TaskVerificationAuthority VerificationAuthority { get; init; }
    public ImmutableArray<TaskVerificationProfile> VerificationProfiles { get; init; } = [];
    public ImmutableArray<TaskCommandVerificationRule> CommandVerificationRules { get; init; } = [];
}

public sealed record TaskWorkspace(string GraphId, string GraphFingerprint, string TaskId, string AttemptId,
    string BaseCommit, string WorkspacePath, string AttemptPath)
{
    public string? InputDigest { get; init; }
}
public sealed record TaskWorkspaceCapture(string Revision, ImmutableArray<WorkflowTaskArtifact> Artifacts,
    ImmutableArray<string> MissingRequiredArtifacts);

/// <summary>Host-owned snapshots outside the checkout, excluding repository metadata and links.</summary>
public interface ITaskWorkspaceManager
{
    Task<string> ResolveCommitAsync(string sourceWorkspaceRoot, string revision, CancellationToken ct);
    Task<TaskWorkspace> OpenOrCreateAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct);
    /// <summary>Prepare accepted predecessor changes before granting model tools. Existing attempts must match pinned inputs.</summary>
    Task<TaskWorkspace> PrepareAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim,
        ImmutableArray<TaskDependencyInput> dependencies, CancellationToken ct) =>
        throw new NotSupportedException("This workspace manager does not support accepted dependency changes.");
    /// <summary>Read-only check against frozen inputs plus confirmed scoped file-tool receipts before execution/replay.</summary>
    Task ValidateWorkingStateAsync(TaskWorkspace workspace, ImmutableArray<WorkflowToolExecution> tools, CancellationToken ct) =>
        throw new NotSupportedException("This workspace manager does not validate prepared execution state.");
    /// <summary>Read-only lookup. Must never create a workspace, metadata, lock file or Git process.</summary>
    Task<TaskWorkspace> OpenExistingAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct) =>
        throw new NotSupportedException("This workspace manager does not implement read-only lookup.");
    Task<TaskWorkspaceCapture> CaptureAsync(TaskWorkspace workspace,
        ImmutableArray<TaskArtifactRequirement> requirements, CancellationToken ct);
    Task<bool> VerifyAsync(TaskWorkspace workspace, WorkflowTaskResult result, CancellationToken ct);
    /// <summary>Copy the exact sealed revision once to a separate host-owned command workspace.</summary>
    Task<TaskVerificationCopy> CreateVerificationCopyAsync(TaskWorkspace workspace, WorkflowTaskResult result,
        string invocationId, CancellationToken ct) => throw new NotSupportedException("Verification copies are unsupported.");
}

/// <summary>A known input or recovery-state conflict; no new task model/tool work may be admitted.</summary>
public sealed class TaskInputConflictException(string message) : InvalidOperationException(message);

/// <summary>Canonical content identities shared by persistence and filesystem hosts.</summary>
public static class TaskInputIdentity
{
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 256 };
    public static string Digest(TaskAttemptInputs inputs) => Hash(JsonSerializer.SerializeToUtf8Bytes(inputs with { Digest = "" }, JsonOptions));
    public static string ResultDigest(WorkflowTaskResult result) => Hash(JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions));
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public interface IAgentTaskGraphStore : ITaskGraphStore
{
    Task<TaskGraphCheckpoint> AcceptAgentTaskGraphAsync(WorkflowTaskGraph graph, CallerIdentity caller,
        int maxConcurrency, TaskGraphAgentBinding binding, CancellationToken ct);
    /// <summary>Explicitly resumes the same durable child only after its effects are safe to replay.</summary>
    Task<TaskGraphClaim> ReclaimAgentTaskAsync(string graphId, string taskId, CallerIdentity caller, CancellationToken ct);
}

public static class AgentTaskIdentity
{
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static RunId RootRunId(string graphId) => new(Hash("task-graph-root/v1/" + graphId));
    public static RunId ChildRunId(string graphFingerprint, string taskId, string attemptId) =>
        new(Hash("task-graph-agent/v1/" + graphFingerprint + "/" + taskId + "/" + attemptId));
    public static SessionKey RootSession(string graphId) => new("graph-root-" + Hash(graphId));
    public static SessionKey ChildSession(string graphFingerprint, string taskId, string attemptId) =>
        new("graph-task-" + ChildRunId(graphFingerprint, taskId, attemptId).Value);
}
