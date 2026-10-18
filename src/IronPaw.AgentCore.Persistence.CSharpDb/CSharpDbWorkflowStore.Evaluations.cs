using System.Collections.Immutable;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence.CSharpDb;

/// <summary>A separate campaign ledger: admission and reservation are one durable transaction.</summary>
public sealed partial class CSharpDbWorkflowStore : ILiveEvaluationStore
{
    public async Task<LiveEvaluationCheckpoint> CreateEvaluationAsync(LiveEvaluationDefinition definition,
        CallerIdentity caller, CancellationToken ct)
    {
        ValidateEvaluationDefinition(definition);
        ValidateEvaluationCaller(caller);
        LiveEvaluationCheckpoint? accepted = null;
        await InGateAsync(() => TransactionAsync(async () =>
        {
            var current = await ReadEvaluationAsync(definition.CampaignId, ct).ConfigureAwait(false);
            if (current is not null)
            {
                RequireEvaluationCaller(current, caller);
                if (current.DefinitionFingerprint != LiveEvaluationIdentity.Fingerprint(definition))
                    throw new WorkflowConflictException("An existing live campaign cannot replace its configuration, prices, workload, or spending cap.");
                accepted = current;
                return;
            }
            var now = _timeProvider.GetUtcNow();
            accepted = new(1, definition, LiveEvaluationIdentity.Fingerprint(definition), caller, 0,
                WorkflowControl.None, now, now, [], []);
            ValidateEvaluation(accepted);
            var id = await NextIdAsync("live_evaluations", ct).ConfigureAwait(false);
            await ExecuteAsync($"INSERT INTO live_evaluations VALUES ({id}, {Quote(definition.CampaignId)}, {Quote(SerializeTaskGraph(accepted))})", ct).ConfigureAwait(false);
            await AppendEvaluationJournalAsync(accepted, "accepted", ct).ConfigureAwait(false);
        }, ct), ct).ConfigureAwait(false);
        return accepted!;
    }

    public async Task<LiveEvaluationCheckpoint?> GetEvaluationAsync(string campaignId, CallerIdentity caller, CancellationToken ct)
    {
        EvaluationId(campaignId, nameof(campaignId));
        ValidateEvaluationCaller(caller);
        LiveEvaluationCheckpoint? checkpoint = null;
        await InGateAsync(async () =>
        {
            checkpoint = await ReadEvaluationAsync(campaignId, ct).ConfigureAwait(false);
            if (checkpoint is not null) RequireEvaluationCaller(checkpoint, caller);
        }, ct).ConfigureAwait(false);
        return checkpoint;
    }

    public async Task<LiveEvaluationAdmission> BeginEvaluationRequestAsync(string campaignId, LiveEvaluationRequest request,
        CallerIdentity caller, CancellationToken ct)
    {
        ValidateEvaluationRequest(request);
        LiveEvaluationAdmission? admission = null;
        await MutateEvaluationAsync(campaignId, caller, async checkpoint =>
        {
            var existing = checkpoint.Requests.SingleOrDefault(item => item.Request.RequestId == request.RequestId);
            if (existing is not null)
            {
                if (SerializeTaskGraph(existing.Request) != SerializeTaskGraph(request))
                    throw new WorkflowConflictException("A paid request identity cannot replace its frozen request or reservation.");
                if (existing.State != LiveEvaluationRequestState.Completed)
                    throw new WorkflowReconciliationRequiredException("This paid request has active or uncertain effects and cannot be dispatched again.");
                admission = new(false, existing);
                return checkpoint;
            }
            if (checkpoint.NeedsReconciliation)
                throw new WorkflowReconciliationRequiredException("Reconcile uncertain provider usage before admitting another paid request.");
            if (checkpoint.HasReservationOverrun)
                throw new WorkflowBudgetExceededException("Observed provider cost exceeded a declared request reservation; this campaign cannot admit further paid work.");
            if (checkpoint.Control == WorkflowControl.Cancel) throw new WorkflowCancelledException("This live campaign was permanently cancelled.");
            if (checkpoint.Control != WorkflowControl.None) throw new WorkflowSuspendedException(WorkflowStatus.Paused, "This live campaign is paused.");
            if (checkpoint.Trials.Any(item => item.TrialId == request.TrialId && item.Strategy == request.Strategy))
                throw new WorkflowConflictException("A completed strategy/trial cannot admit another provider request.");
            if (request.ReservedSpend > checkpoint.RemainingSpend)
                throw new WorkflowBudgetExceededException("This reservation would exceed the total campaign spending cap.");
            var invocation = new LiveEvaluationRequestRecord(request, LiveEvaluationRequestState.Started, _timeProvider.GetUtcNow());
            admission = new(true, invocation);
            return await SaveEvaluationAsync(checkpoint with { Requests = checkpoint.Requests.Add(invocation) }, "request_started", ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return admission!;
    }

    public Task<LiveEvaluationCheckpoint> CompleteEvaluationRequestAsync(string campaignId, string requestId,
        LiveEvaluationReceipt receipt, CallerIdentity caller, CancellationToken ct)
    {
        ValidateEvaluationReceipt(receipt);
        return MutateEvaluationAsync(campaignId, caller, async checkpoint =>
        {
            var index = EvaluationRequestIndex(checkpoint, requestId);
            var current = checkpoint.Requests[index];
            if (current.State == LiveEvaluationRequestState.Completed ||
                current.State == LiveEvaluationRequestState.NeedsReconciliation && current.Receipt is not null)
            {
                if (SerializeTaskGraph(current.Receipt) == SerializeTaskGraph(receipt)) return checkpoint;
                throw new WorkflowConflictException("An established provider receipt cannot be replaced; uncertain requests require reconciliation.");
            }
            if (current.State != LiveEvaluationRequestState.Started)
                throw new WorkflowReconciliationRequiredException("A recovered uncertain request requires explicit owner reconciliation.");
            var next = current with { State = receipt.UsageComplete ? LiveEvaluationRequestState.Completed : LiveEvaluationRequestState.NeedsReconciliation,
                Receipt = receipt, CompletedAt = receipt.UsageComplete ? Later(_timeProvider.GetUtcNow(), current.StartedAt) : null };
            return await SaveEvaluationAsync(checkpoint with { Requests = checkpoint.Requests.SetItem(index, next) }, "request_receipt", ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<LiveEvaluationCheckpoint> ReconcileEvaluationRequestAsync(string campaignId, string requestId,
        LiveEvaluationReceipt receipt, string note, CallerIdentity caller, CancellationToken ct)
    {
        ValidateEvaluationReceipt(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        if (!receipt.UsageComplete) throw new ArgumentException("Reconciliation requires an established complete usage receipt.", nameof(receipt));
        return MutateEvaluationAsync(campaignId, caller, async checkpoint =>
        {
            var index = EvaluationRequestIndex(checkpoint, requestId);
            var current = checkpoint.Requests[index];
            if (current.State != LiveEvaluationRequestState.NeedsReconciliation)
                throw new WorkflowConflictException("Only an uncertain provider request can be explicitly reconciled.");
            if (current.Receipt is { } observed && (receipt.ActualSpend < observed.ActualSpend ||
                observed.ReservationAssumptionsExceeded && !receipt.ReservationAssumptionsExceeded ||
                observed.InputTokens is { } input && receipt.InputTokens < input ||
                observed.OutputTokens is { } output && receipt.OutputTokens < output))
                throw new WorkflowConflictException("Reconciliation cannot discard already observed usage or cost.");
            var next = current with { State = LiveEvaluationRequestState.Completed, Receipt = receipt,
                CompletedAt = Later(_timeProvider.GetUtcNow(), current.StartedAt), ReconciliationNote = note };
            return await SaveEvaluationAsync(checkpoint with { Requests = checkpoint.Requests.SetItem(index, next) }, "request_reconciled", ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<LiveEvaluationCheckpoint> RecordEvaluationTrialAsync(string campaignId, LiveEvaluationTrialResult trial,
        CallerIdentity caller, CancellationToken ct)
    {
        ValidateEvaluationTrial(trial);
        return MutateEvaluationAsync(campaignId, caller, async checkpoint =>
        {
            var existing = checkpoint.Trials.SingleOrDefault(item => item.TrialId == trial.TrialId && item.Strategy == trial.Strategy);
            if (existing is not null)
            {
                if (SerializeTaskGraph(existing) == SerializeTaskGraph(trial)) return checkpoint;
                throw new WorkflowConflictException("An established strategy/trial result cannot be replaced.");
            }
            if (checkpoint.Requests.Any(item => item.Request.TrialId == trial.TrialId && item.Request.Strategy == trial.Strategy && item.State != LiveEvaluationRequestState.Completed))
                throw new WorkflowReconciliationRequiredException("Settle this strategy/trial's active or uncertain provider requests before saving its final result.");
            return await SaveEvaluationAsync(checkpoint with { Trials = checkpoint.Trials.Add(trial) }, "trial_result", ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<LiveEvaluationCheckpoint> SetEvaluationControlAsync(string campaignId, WorkflowControl control,
        CallerIdentity caller, CancellationToken ct)
    {
        if (!Enum.IsDefined(control)) throw new ArgumentOutOfRangeException(nameof(control));
        return MutateEvaluationAsync(campaignId, caller, async checkpoint =>
        {
            if (checkpoint.Control == control) return checkpoint;
            if (checkpoint.Control == WorkflowControl.Cancel) throw new WorkflowCancelledException("This live campaign was permanently cancelled.");
            if (control == WorkflowControl.None && checkpoint.Requests.Any(item => item.State == LiveEvaluationRequestState.Started))
                throw new WorkflowConflictException("Active paid requests must settle before clearing pause.");
            return await SaveEvaluationAsync(checkpoint with { Control = control }, "control", ct).ConfigureAwait(false);
        }, ct);
    }

    private async Task InitializeEvaluationsAsync(CancellationToken ct)
    {
        await ExecuteAsync("CREATE TABLE IF NOT EXISTS live_evaluations (id INTEGER PRIMARY KEY, campaign_id TEXT, payload TEXT)", ct).ConfigureAwait(false);
        await ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS live_evaluations_campaign ON live_evaluations (campaign_id)", ct).ConfigureAwait(false);
        await ExecuteAsync("CREATE TABLE IF NOT EXISTS live_evaluation_journal (id INTEGER PRIMARY KEY, campaign_id TEXT, revision INTEGER, timestamp TEXT, event_type TEXT)", ct).ConfigureAwait(false);
        await ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS live_evaluation_journal_revision ON live_evaluation_journal (campaign_id, revision)", ct).ConfigureAwait(false);
        var checkpoints = new List<LiveEvaluationCheckpoint>();
        var query = await _database.ExecuteAsync("SELECT payload FROM live_evaluations ORDER BY id", ct).ConfigureAwait(false);
        try
        {
            while (await query.MoveNextAsync(ct).ConfigureAwait(false)) checkpoints.Add(DeserializeEvaluation(query.Current[0].AsText));
        }
        finally { await query.DisposeAsync().ConfigureAwait(false); }
        await TransactionAsync(async () =>
        {
            foreach (var checkpoint in checkpoints.Where(item => item.Requests.Any(request => request.State == LiveEvaluationRequestState.Started)))
                await SaveEvaluationAsync(checkpoint with { Requests = checkpoint.Requests.Select(request => request.State == LiveEvaluationRequestState.Started
                    ? request with { State = LiveEvaluationRequestState.NeedsReconciliation } : request).ToImmutableArray() }, "recovered", ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task<LiveEvaluationCheckpoint> MutateEvaluationAsync(string campaignId, CallerIdentity caller,
        Func<LiveEvaluationCheckpoint, Task<LiveEvaluationCheckpoint>> mutate, CancellationToken ct)
    {
        EvaluationId(campaignId, nameof(campaignId));
        ValidateEvaluationCaller(caller);
        LiveEvaluationCheckpoint? saved = null;
        await InGateAsync(() => TransactionAsync(async () =>
        {
            var current = await ReadEvaluationAsync(campaignId, ct).ConfigureAwait(false)
                ?? throw new WorkflowConflictException("This live campaign does not exist.");
            RequireEvaluationCaller(current, caller);
            saved = await mutate(current).ConfigureAwait(false);
        }, ct), ct).ConfigureAwait(false);
        return saved!;
    }

    private async Task<LiveEvaluationCheckpoint?> ReadEvaluationAsync(string campaignId, CancellationToken ct)
    {
        var query = await _database.ExecuteAsync($"SELECT payload FROM live_evaluations WHERE campaign_id = {Quote(campaignId)}", ct).ConfigureAwait(false);
        try { return await query.MoveNextAsync(ct).ConfigureAwait(false) ? DeserializeEvaluation(query.Current[0].AsText) : null; }
        finally { await query.DisposeAsync().ConfigureAwait(false); }
    }

    private static LiveEvaluationCheckpoint DeserializeEvaluation(string payload)
    {
        var checkpoint = JsonSerializer.Deserialize<LiveEvaluationCheckpoint>(payload, TaskGraphJsonOptions)
            ?? throw new InvalidDataException("A live campaign checkpoint is empty.");
        ValidateEvaluation(checkpoint);
        return checkpoint;
    }

    private async Task<LiveEvaluationCheckpoint> SaveEvaluationAsync(LiveEvaluationCheckpoint checkpoint, string eventType, CancellationToken ct)
    {
        var saved = checkpoint with { Revision = checked(checkpoint.Revision + 1), UpdatedAt = Later(_timeProvider.GetUtcNow(), checkpoint.CreatedAt) };
        ValidateEvaluation(saved);
        await ExecuteAsync($"UPDATE live_evaluations SET payload = {Quote(SerializeTaskGraph(saved))} WHERE campaign_id = {Quote(saved.Definition.CampaignId)}", ct).ConfigureAwait(false);
        await AppendEvaluationJournalAsync(saved, eventType, ct).ConfigureAwait(false);
        return saved;
    }

    private async Task AppendEvaluationJournalAsync(LiveEvaluationCheckpoint checkpoint, string eventType, CancellationToken ct)
    {
        var id = await NextIdAsync("live_evaluation_journal", ct).ConfigureAwait(false);
        await ExecuteAsync($"INSERT INTO live_evaluation_journal VALUES ({id}, {Quote(checkpoint.Definition.CampaignId)}, {checkpoint.Revision}, {Quote(checkpoint.UpdatedAt.ToString("O"))}, {Quote(eventType)})", ct).ConfigureAwait(false);
    }

    private static void ValidateEvaluation(LiveEvaluationCheckpoint checkpoint)
    {
        if (checkpoint.SchemaVersion != 1) throw new NotSupportedException("This live campaign schema is not supported.");
        ValidateEvaluationDefinition(checkpoint.Definition);
        ValidateEvaluationCaller(checkpoint.Caller);
        if (checkpoint.DefinitionFingerprint != LiveEvaluationIdentity.Fingerprint(checkpoint.Definition) || checkpoint.Revision < 0 ||
            !Enum.IsDefined(checkpoint.Control) || checkpoint.CreatedAt == default || checkpoint.UpdatedAt < checkpoint.CreatedAt ||
            checkpoint.Requests.IsDefault || checkpoint.Trials.IsDefault)
            throw new InvalidDataException("A live campaign checkpoint has invalid identity, chronology, or collections.");
        var requests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in checkpoint.Requests)
        {
            if (item is null) throw new InvalidDataException("A live campaign request cannot be null.");
            ValidateEvaluationRequest(item.Request);
            if (!requests.Add(item.Request.RequestId) || !Enum.IsDefined(item.State) || item.StartedAt < checkpoint.CreatedAt ||
                item.CompletedAt < item.StartedAt || item.ReconciliationNote is not null && string.IsNullOrWhiteSpace(item.ReconciliationNote))
                throw new InvalidDataException("A live campaign request has invalid identity or chronology.");
            if (item.Receipt is { } receipt) ValidateEvaluationReceipt(receipt);
            if (item.State == LiveEvaluationRequestState.Started && (item.Receipt is not null || item.CompletedAt is not null || item.ReconciliationNote is not null) ||
                item.State == LiveEvaluationRequestState.Completed && (item.Receipt is not { UsageComplete: true } || item.CompletedAt is null) ||
                item.State == LiveEvaluationRequestState.NeedsReconciliation && (item.Receipt is { UsageComplete: true } || item.CompletedAt is not null || item.ReconciliationNote is not null))
                throw new InvalidDataException("Live request state and usage evidence disagree.");
        }
        var trials = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trial in checkpoint.Trials)
        {
            ValidateEvaluationTrial(trial);
            if (!trials.Add(JsonSerializer.Serialize(new[] { trial.TrialId, trial.Strategy })) || checkpoint.Requests.Any(item =>
                item.Request.TrialId == trial.TrialId && item.Request.Strategy == trial.Strategy && item.State != LiveEvaluationRequestState.Completed))
                throw new InvalidDataException("Strategy/trial results must be unique and have established usage.");
        }
        _ = checkpoint.ChargedSpend; // Overflow is corrupt financial evidence, never an implicit zero balance.
    }

    private static void ValidateEvaluationDefinition(LiveEvaluationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        EvaluationId(definition.CampaignId, nameof(definition.CampaignId));
        EvaluationId(definition.Provider, nameof(definition.Provider));
        EvaluationId(definition.Model, nameof(definition.Model));
        if (definition.Currency is null || definition.Currency.Length != 3 || definition.Currency.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("Currency must be an exact uppercase three-letter label.");
        if (definition.MaxTotalSpend <= 0) throw new ArgumentOutOfRangeException(nameof(definition.MaxTotalSpend));
        EvaluationJson(definition.BindingJson, nameof(definition.BindingJson));
    }

    private static void ValidateEvaluationRequest(LiveEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EvaluationId(request.RequestId, nameof(request.RequestId));
        EvaluationId(request.TrialId, nameof(request.TrialId));
        EvaluationId(request.Strategy, nameof(request.Strategy));
        EvaluationDigest(request.RequestFingerprint, nameof(request.RequestFingerprint));
        if (request.ReservedSpend <= 0) throw new ArgumentOutOfRangeException(nameof(request.ReservedSpend));
    }

    private static void ValidateEvaluationReceipt(LiveEvaluationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.ActualSpend < 0 || receipt.InputTokens < 0 || receipt.OutputTokens < 0 ||
            receipt.UsageComplete && (receipt.InputTokens is null || receipt.OutputTokens is null))
            throw new ArgumentException("Established usage requires nonnegative input/output token counts and cost.");
        EvaluationJson(receipt.EvidenceJson, nameof(receipt.EvidenceJson));
    }

    private static void ValidateEvaluationTrial(LiveEvaluationTrialResult trial)
    {
        ArgumentNullException.ThrowIfNull(trial);
        EvaluationId(trial.TrialId, nameof(trial.TrialId));
        EvaluationId(trial.Strategy, nameof(trial.Strategy));
        EvaluationDigest(trial.BindingFingerprint, nameof(trial.BindingFingerprint));
        EvaluationJson(trial.ResultJson, nameof(trial.ResultJson));
    }

    private static void RequireEvaluationCaller(LiveEvaluationCheckpoint checkpoint, CallerIdentity caller)
    {
        ValidateEvaluationCaller(caller);
        if (checkpoint.Caller.Id != caller.Id || checkpoint.Caller.IsOwner != caller.IsOwner)
            throw new UnauthorizedAccessException("Only the original owner may inspect, run, or reconcile this live campaign.");
    }

    private static void ValidateEvaluationCaller(CallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        EvaluationId(caller.Id, nameof(caller.Id));
        if (!caller.IsOwner) throw new UnauthorizedAccessException("Live evaluation requires an explicit local owner.");
    }

    private static int EvaluationRequestIndex(LiveEvaluationCheckpoint checkpoint, string requestId)
    {
        EvaluationId(requestId, nameof(requestId));
        for (var index = 0; index < checkpoint.Requests.Length; index++)
            if (checkpoint.Requests[index].Request.RequestId == requestId) return index;
        throw new WorkflowConflictException("This paid request does not exist.");
    }

    private static void EvaluationId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 1024 || value.Any(char.IsControl))
            throw new ArgumentException("Identity must be nonblank, unpadded, bounded, and contain no control characters.", name);
    }

    private static void EvaluationDigest(string? value, string name)
    {
        if (value is null || value.Length != 64 || value.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new ArgumentException("Fingerprint must be a lowercase SHA-256 digest.", name);
    }

    private static void EvaluationJson(string json, string name)
    {
        if (json is null || json.Length > 4 * 1024 * 1024) throw new ArgumentException("Evidence JSON must be present and bounded.", name);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new ArgumentException("Evidence must be a JSON object or array.", name);
    }
}
