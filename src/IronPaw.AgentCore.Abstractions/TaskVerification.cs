using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace IronPaw.AgentCore.Abstractions;

public enum TaskVerificationAuthority { None, TrustedLocalProcess }
public enum TaskVerificationKind { Build, Tests }

/// <summary>Host-defined literal command. Repository code executes with the worker's OS authority.</summary>
public sealed record TaskVerificationProfile(string ProfileId, string ExecutablePath, string ExecutableDigest,
    ImmutableArray<string> Arguments, TimeSpan Timeout)
{
    public string WorkingDirectory { get; init; } = ".";
    public int MaxOutputBytes { get; init; } = 65_536;
    public TaskVerificationKind Kind { get; init; } = TaskVerificationKind.Build;
    public string? TestResultsPath { get; init; }
}

public sealed record TaskCommandVerificationRule(string TaskId, string CriterionId, string ProfileId);
public enum TaskVerificationInvocationState { Started, Completed, NeedsReconciliation }
public enum TaskCommandOutcome { Passed, Failed, BudgetExhausted, NeedsReconciliation }

public sealed record TaskVerificationLog(string Text, string Digest, long ByteCount, bool Truncated);
public sealed record TaskVerificationTestSummary(int Total, int Passed, int Failed, int Skipped);
public sealed record TaskVerificationCompletion(TaskCommandOutcome Outcome, string WorkerId, string Explanation,
    int? ExitCode, TaskVerificationLog StandardOutput, TaskVerificationLog StandardError,
    TaskVerificationTestSummary? Tests = null);

public sealed record TaskVerificationInvocation(string InvocationId, string ProfileId, string ProfileFingerprint,
    string ResultDigest, string Revision, TaskVerificationInvocationState State, DateTimeOffset StartedAt,
    TaskVerificationCompletion? Completion = null, DateTimeOffset? CompletedAt = null, string? ReconciliationNote = null);
public sealed record TaskVerificationAdmission(bool ShouldExecute, TaskVerificationInvocation Invocation);

public sealed record TaskVerificationCopy(string InvocationId, string Revision, string WorkspacePath, string EvidencePath,
    string InputDigest = "");
public sealed record TaskVerificationJob(TaskGraphClaim Claim, WorkflowTaskResult Result, TaskVerificationProfile Profile,
    string ProfileFingerprint, TaskVerificationCopy Copy);

/// <summary>Trusted host authority; never supplied or configured by model tool calls.</summary>
public interface ITaskVerificationWorker
{
    void ValidateProfile(TaskVerificationProfile profile, CallerIdentity caller);
    Task<TaskVerificationCompletion> ExecuteAsync(TaskVerificationJob job, CancellationToken ct);
}

/// <summary>Effectful durable verification stage, separate from the read-only acceptance verifier.</summary>
public interface ITaskVerificationRunner
{
    Task RunAsync(TaskGraphClaim claim, WorkflowTaskResult result, CancellationToken ct);
}

public interface ITaskVerificationStore
{
    Task<TaskVerificationAdmission> BeginTaskVerificationAsync(TaskGraphClaim claim, string profileId, CancellationToken ct);
    Task<TaskGraphCheckpoint> CompleteTaskVerificationAsync(TaskGraphClaim claim, string invocationId,
        TaskVerificationCompletion completion, CancellationToken ct);
    Task<TaskGraphCheckpoint> ReconcileTaskVerificationAsync(string graphId, string taskId, string invocationId,
        TaskVerificationCompletion completion, string note, CallerIdentity caller, CancellationToken ct);
}

public static class TaskVerificationIdentity
{
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 256 };
    public static string ProfileFingerprint(TaskVerificationProfile profile) => Hash(JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions));
    public static string InvocationId(TaskGraphClaim claim, WorkflowTaskResult result, TaskVerificationProfile profile) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(new { claim.GraphId, claim.GraphFingerprint, TaskId = claim.Task.TaskId,
            claim.AttemptId, ResultDigest = TaskInputIdentity.ResultDigest(result), result.Revision,
            ProfileFingerprint = ProfileFingerprint(profile) }, JsonOptions));
    public static string EmptyLogDigest => Hash([]);
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
