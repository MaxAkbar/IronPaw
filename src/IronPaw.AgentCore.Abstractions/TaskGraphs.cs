using System.Collections.Immutable;

namespace IronPaw.AgentCore.Abstractions;

/// <summary>A proposed task graph. Validate and snapshot it before accepting or scheduling work.</summary>
public sealed record WorkflowTaskGraph(string GraphId, string Objective,
    ImmutableArray<WorkflowTaskDefinition> Tasks, int SchemaVersion = 1);

/// <summary>Task identity is independent of the invocations that may execute or retry it.</summary>
public sealed record WorkflowTaskDefinition(string TaskId, string Objective)
{
    public ImmutableArray<string> Dependencies { get; init; } = [];
    public string? AgentId { get; init; }
    public ToolSelectionPolicy ToolPolicy { get; init; } = new(ToolProfile.Minimal);
    public TaskResourceScope Resources { get; init; } = new();
    public WorkflowBudget? Budget { get; init; }
    public ImmutableArray<TaskArtifactRequirement> ExpectedArtifacts { get; init; } = [];
    public ImmutableArray<TaskAcceptanceCriterion> AcceptanceCriteria { get; init; } = [];
}

/// <summary>
/// Requested resource restrictions, not authority grants. A future executor must intersect
/// these declarations with the root's permissions and enforce resolved resource boundaries.
/// </summary>
public sealed record TaskResourceScope
{
    public ImmutableArray<string> ReadPaths { get; init; } = [];
    public ImmutableArray<string> WritePaths { get; init; } = [];
    public ImmutableArray<string> NetworkHosts { get; init; } = [];
    public bool AllowProcessExecution { get; init; }
    public bool AllowCredentialAccess { get; init; }
    public bool AllowExternalMutations { get; init; }
}

public enum TaskArtifactKind { File, Directory, Patch, Report, Other }

public sealed record TaskArtifactRequirement(string ArtifactId, string Description,
    TaskArtifactKind Kind, string? RelativePath = null, bool Required = true);

public sealed record TaskAcceptanceCriterion(string CriterionId, string Description, bool Required = true)
{
    /// <summary>IDs of this task's declared outputs needed to assess the criterion.</summary>
    public ImmutableArray<string> ArtifactIds { get; init; } = [];
}

public enum WorkflowTaskStatus { Blocked, Running, AwaitingInput, Succeeded, Failed, Cancelled, BudgetExhausted, AwaitingVerification }
public enum TaskVerificationOutcome { Inconclusive, Passed, Failed }

/// <summary>ContentDigest and Revision identify the exact reported artifact, not a mutable path alone.</summary>
public sealed record WorkflowTaskArtifact(string ArtifactId, string ProducerTaskId,
    TaskArtifactKind Kind, string Location, string ContentDigest, string Revision);

/// <summary>Recorded evidence is a claim until a trusted host verifies its origin and contents.</summary>
public sealed record TaskVerificationEvidence(string EvidenceId, string CriterionId,
    string Revision, TaskVerificationOutcome Outcome, string Summary)
{
    public ImmutableArray<string> ArtifactIds { get; init; } = [];
    public string? Command { get; init; }
    public int? ExitCode { get; init; }
}

/// <summary>Separate from AgentRunResult: loop completion alone does not establish task acceptance.</summary>
public sealed record WorkflowTaskResult(string GraphId, string GraphFingerprint, string TaskId,
    WorkflowTaskStatus Status, string Revision, string Explanation)
{
    public ImmutableArray<WorkflowTaskArtifact> Artifacts { get; init; } = [];
    public ImmutableArray<TaskVerificationEvidence> VerificationEvidence { get; init; } = [];
    public ImmutableArray<string> UnresolvedIssues { get; init; } = [];
}
