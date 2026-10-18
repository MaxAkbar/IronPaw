using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IronPaw.AgentCore.Abstractions;

/// <summary>Frozen host configuration. BindingJson contains the complete pricing, workload, and execution binding.</summary>
public sealed record LiveEvaluationDefinition(string CampaignId, string Provider, string Model,
    string Currency, decimal MaxTotalSpend, string BindingJson);

/// <summary>A reservation is persisted before dispatch. RequestId is stable across workflow replay.</summary>
public sealed record LiveEvaluationRequest(string RequestId, string TrialId, string Strategy,
    string RequestFingerprint, decimal ReservedSpend);

/// <summary>ActualSpend is calculated by the trusted host from the frozen price schedule, never by a model.</summary>
public sealed record LiveEvaluationReceipt(bool UsageComplete, decimal ActualSpend, long? InputTokens,
    long? OutputTokens, string EvidenceJson, bool ReservationAssumptionsExceeded = false);

public enum LiveEvaluationRequestState { Started, Completed, NeedsReconciliation }

public sealed record LiveEvaluationRequestRecord(LiveEvaluationRequest Request, LiveEvaluationRequestState State,
    DateTimeOffset StartedAt, LiveEvaluationReceipt? Receipt = null, DateTimeOffset? CompletedAt = null,
    string? ReconciliationNote = null)
{
    public decimal ChargedSpend => State == LiveEvaluationRequestState.Completed
        ? Receipt!.ActualSpend : Math.Max(Request.ReservedSpend, Receipt?.ActualSpend ?? 0m);
}

/// <summary>One immutable strategy/trial result; ResultJson includes its outcome, quality, and latency evidence.</summary>
public sealed record LiveEvaluationTrialResult(string TrialId, string Strategy, string BindingFingerprint,
    string ResultJson);

public sealed record LiveEvaluationCheckpoint(int SchemaVersion, LiveEvaluationDefinition Definition,
    string DefinitionFingerprint, CallerIdentity Caller, int Revision, WorkflowControl Control,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, ImmutableArray<LiveEvaluationRequestRecord> Requests,
    ImmutableArray<LiveEvaluationTrialResult> Trials)
{
    public decimal ChargedSpend => Requests.Sum(request => request.ChargedSpend);
    public decimal RemainingSpend => Math.Max(0m, Definition.MaxTotalSpend - ChargedSpend);
    public bool NeedsReconciliation => Requests.Any(request => request.State == LiveEvaluationRequestState.NeedsReconciliation);
    public bool HasReservationOverrun => Requests.Any(request => request.Receipt?.ActualSpend > request.Request.ReservedSpend ||
        request.Receipt?.ReservationAssumptionsExceeded == true);
}

public sealed record LiveEvaluationAdmission(bool ShouldExecute, LiveEvaluationRequestRecord Invocation);

public interface ILiveEvaluationStore
{
    Task<LiveEvaluationCheckpoint> CreateEvaluationAsync(LiveEvaluationDefinition definition, CallerIdentity caller, CancellationToken ct);
    Task<LiveEvaluationCheckpoint?> GetEvaluationAsync(string campaignId, CallerIdentity caller, CancellationToken ct);
    Task<LiveEvaluationAdmission> BeginEvaluationRequestAsync(string campaignId, LiveEvaluationRequest request, CallerIdentity caller, CancellationToken ct);
    Task<LiveEvaluationCheckpoint> CompleteEvaluationRequestAsync(string campaignId, string requestId, LiveEvaluationReceipt receipt, CallerIdentity caller, CancellationToken ct);
    Task<LiveEvaluationCheckpoint> ReconcileEvaluationRequestAsync(string campaignId, string requestId, LiveEvaluationReceipt receipt, string note, CallerIdentity caller, CancellationToken ct);
    Task<LiveEvaluationCheckpoint> RecordEvaluationTrialAsync(string campaignId, LiveEvaluationTrialResult trial, CallerIdentity caller, CancellationToken ct);
    Task<LiveEvaluationCheckpoint> SetEvaluationControlAsync(string campaignId, WorkflowControl control, CallerIdentity caller, CancellationToken ct);
}

public static class LiveEvaluationIdentity
{
    public static string Fingerprint(LiveEvaluationDefinition definition) => Digest(JsonSerializer.Serialize(definition));
    public static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
