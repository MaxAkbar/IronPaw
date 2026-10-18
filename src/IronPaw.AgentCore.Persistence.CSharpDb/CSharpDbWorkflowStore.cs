using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpDB.Engine;
using CSharpDB.Storage.StorageEngine;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence.CSharpDb;

/// <summary>
/// Owns one exclusively opened CSharpDB file. A checkpoint, its budget usage,
/// and the corresponding journal record are one durable transaction.
/// </summary>
public sealed partial class CSharpDbWorkflowStore : IWorkflowStore, IAsyncDisposable, IDisposable
{
    private const int SchemaVersion = 3;
    private readonly Database _database;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private CSharpDbWorkflowStore(Database database, TimeProvider timeProvider)
    {
        _database = database;
        _timeProvider = timeProvider;
    }

    public static async Task<CSharpDbWorkflowStore> OpenAsync(string databasePath, CancellationToken ct = default,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var database = await Database.OpenAsync(path, new DatabaseOptions
        {
            StorageEngineOptions = new StorageEngineOptions { PrimaryFileShare = FileShare.None }
        }, ct).ConfigureAwait(false);
        var store = new CSharpDbWorkflowStore(database, timeProvider ?? TimeProvider.System);
        try
        {
            await store.InitializeAsync(ct).ConfigureAwait(false);
            return store;
        }
        catch
        {
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task CreateAsync(WorkflowCheckpoint checkpoint, CancellationToken ct)
    {
        Validate(checkpoint);
        if (checkpoint.Revision != 0)
            throw new WorkflowConflictException("A new workflow must have revision zero.");
        await InGateAsync(async () =>
        {
            await TransactionAsync(async () =>
            {
                var existing = await ReadAllAsync(ct).ConfigureAwait(false);
                if (existing.Any(x => x.RunId == checkpoint.RunId))
                    throw new WorkflowConflictException("A workflow with this run id already exists.");
                if (existing.Any(x => x.Request.SessionKey == checkpoint.Request.SessionKey && IsActive(x)))
                    throw new WorkflowConflictException("This session already has an unfinished workflow. Resume or resolve it first.");
                ValidateLineage(existing, checkpoint);
                await ValidateAgentTaskWorkflowWriteAsync(existing, null, checkpoint, ct).ConfigureAwait(false);
                ValidateExecutionAdmission(existing, null, checkpoint);
                ValidateSharedBudget(existing, checkpoint);
                await ValidateBudgetAdmissionAsync(existing, null, checkpoint, ct).ConfigureAwait(false);
                var id = await NextIdAsync("workflows", ct).ConfigureAwait(false);
                await ExecuteAsync($"INSERT INTO workflows VALUES ({id}, {Quote(checkpoint.RunId)}, {Quote(Serialize(checkpoint))})", ct).ConfigureAwait(false);
                await AppendJournalAsync(checkpoint, "accepted", ct).ConfigureAwait(false);
                await UpdateClockAsync(checkpoint.RootRunId, ReplaceCheckpoint(existing, checkpoint), ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<WorkflowCheckpoint?> GetAsync(RunId runId, CancellationToken ct)
    {
        WorkflowCheckpoint? checkpoint = null;
        await InGateAsync(async () => checkpoint = await ReadOneAsync(runId.Value, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return checkpoint;
    }

    public async Task<IReadOnlyList<WorkflowCheckpoint>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<WorkflowCheckpoint> checkpoints = [];
        await InGateAsync(async () => checkpoints = await ReadAllAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return checkpoints;
    }

    public async Task<WorkflowCheckpoint> SaveAsync(WorkflowCheckpoint checkpoint, int expectedRevision,
        string eventType, CancellationToken ct)
    {
        Validate(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        WorkflowCheckpoint? saved = null;
        await InGateAsync(async () =>
        {
            await TransactionAsync(async () =>
            {
                var current = await ReadOneAsync(checkpoint.RunId, ct).ConfigureAwait(false)
                    ?? throw new WorkflowConflictException("The workflow does not exist.");
                if (current.Revision != expectedRevision || checkpoint.Revision != expectedRevision)
                    throw new WorkflowConflictException("The workflow changed since this checkpoint was read.");
                ValidateUpdate(current, checkpoint);
                var existing = await ReadAllAsync(ct).ConfigureAwait(false);
                await ValidateAgentTaskWorkflowWriteAsync(existing, current, checkpoint, ct).ConfigureAwait(false);
                ValidateExecutionAdmission(existing, current, checkpoint);
                var rootControl = existing.Single(x => x.RunId == checkpoint.RootRunId).Control;
                if (checkpoint.LeaseOwner is null && checkpoint.Suspension is not null &&
                    (current.LeaseOwner is not null || Serialize(current.Suspension) != Serialize(checkpoint.Suspension)))
                {
                    if (rootControl == WorkflowControl.Cancel || checkpoint.Control == WorkflowControl.Cancel)
                        throw new WorkflowCancelledException("Cancellation superseded this suspension.");
                    if ((rootControl == WorkflowControl.Pause || checkpoint.Control == WorkflowControl.Pause) &&
                        checkpoint.Suspension.Status != AgentRunStatus.Paused)
                        throw new WorkflowSuspendedException(WorkflowStatus.Paused, "Pause superseded this suspension.");
                }
                ValidateControlBoundWrites(existing, current, checkpoint);
                if (IsActive(checkpoint) && existing.Any(x => x.RunId != checkpoint.RunId &&
                    x.Request.SessionKey == checkpoint.Request.SessionKey && IsActive(x)))
                    throw new WorkflowConflictException("This session already has another unfinished workflow.");
                ValidateSharedBudget(existing, checkpoint);
                await ValidateBudgetAdmissionAsync(existing, current, checkpoint, ct).ConfigureAwait(false);
                saved = checkpoint with { Revision = checked(expectedRevision + 1), UpdatedAt = _timeProvider.GetUtcNow() };
                await WriteCheckpointAsync(saved, ct).ConfigureAwait(false);
                await AppendJournalAsync(saved, eventType, ct).ConfigureAwait(false);
                await UpdateClockAsync(saved.RootRunId, ReplaceCheckpoint(existing, saved), ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return saved!;
    }

    public async Task<IReadOnlyList<WorkflowJournalEntry>> ReadJournalAsync(RunId runId, CancellationToken ct)
    {
        var entries = new List<WorkflowJournalEntry>();
        await InGateAsync(async () =>
        {
            var query = await _database.ExecuteAsync($"SELECT revision, timestamp, event_type FROM workflow_journal WHERE run_id = {Quote(runId.Value)} ORDER BY revision", ct).ConfigureAwait(false);
            try
            {
                while (await query.MoveNextAsync(ct).ConfigureAwait(false))
                    entries.Add(new WorkflowJournalEntry(runId.Value, checked((int)query.Current[0].AsInteger),
                        DateTimeOffset.Parse(query.Current[1].AsText, System.Globalization.CultureInfo.InvariantCulture), query.Current[2].AsText));
            }
            finally { await query.DisposeAsync().ConfigureAwait(false); }
        }, ct).ConfigureAwait(false);
        return entries;
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        await ExecuteAsync("CREATE TABLE IF NOT EXISTS workflows (id INTEGER PRIMARY KEY, run_id TEXT, payload TEXT)", ct).ConfigureAwait(false);
        await ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS workflows_run_id ON workflows (run_id)", ct).ConfigureAwait(false);
        await ExecuteAsync("CREATE TABLE IF NOT EXISTS workflow_journal (id INTEGER PRIMARY KEY, run_id TEXT, revision INTEGER, timestamp TEXT, event_type TEXT)", ct).ConfigureAwait(false);
        await ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS workflow_journal_revision ON workflow_journal (run_id, revision)", ct).ConfigureAwait(false);
        await ExecuteAsync("CREATE TABLE IF NOT EXISTS workflow_clocks (id INTEGER PRIMARY KEY, root_run_id TEXT, payload TEXT)", ct).ConfigureAwait(false);
        await ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS workflow_clocks_root ON workflow_clocks (root_run_id)", ct).ConfigureAwait(false);
        await EnsureTaskGraphTablesAsync(ct).ConfigureAwait(false);
        _ = await ReadTaskGraphsAsync(ct).ConfigureAwait(false);
        // Validate every version before mutating any checkpoint. Exclusive acquisition
        // guarantees that a persisted lease cannot belong to another live store.
        var checkpoints = await ReadAllAsync(ct, allowLegacy: true).ConfigureAwait(false);
        var changing = checkpoints.Where(x => x.SchemaVersion != SchemaVersion || x.LeaseOwner is not null ||
            x.Status == WorkflowStatus.Running || HasRunningAttempts(x)).ToArray();
        await TransactionAsync(async () =>
        {
            var recoveredCheckpoints = checkpoints.ToList();
            foreach (var original in changing)
            {
                var checkpoint = original;
                if (checkpoint.SchemaVersion == 1)
                {
                    var humanRequests = checkpoint.HumanRequests;
                    var result = checkpoint.Result;
                    var suspension = checkpoint.Suspension;
                    if (result?.Status == AgentRunStatus.AwaitingInput && !checkpoints
                        .SkipWhile(x => x.RunId != checkpoint.RunId).Skip(1)
                        .Any(x => x.Request.SessionKey == checkpoint.Request.SessionKey))
                    {
                        suspension = result;
                        result = null;
                        var modelIndex = FindClarificationCall(checkpoint);
                        var sourceKey = $"route:{modelIndex}";
                        if (!(humanRequests?.Any(request => request.SourceKey == sourceKey) ?? false))
                            humanRequests = (humanRequests ?? []).Append(new WorkflowHumanRequest(Guid.NewGuid().ToString("n"),
                                WorkflowHumanRequestKind.Input, suspension.FinalAssistantMessage,
                                sourceKey, checkpoint.UpdatedAt)).ToArray();
                    }
                    checkpoint = checkpoint with {
                        ToolExecutions = checkpoint.ToolExecutions?.Select(tool => tool with {
                            State = tool.Result is null ? WorkflowToolState.Started : WorkflowToolState.Completed }).ToArray(),
                        HumanRequests = humanRequests, Result = result, Suspension = suspension };
                }
                if (checkpoint.SchemaVersion < SchemaVersion)
                {
                    checkpoint = MigrateBudgetAttempts(checkpoint) with { SchemaVersion = SchemaVersion,
                        Revision = checked(checkpoint.Revision + 1), UpdatedAt = _timeProvider.GetUtcNow() };
                    Validate(checkpoint);
                    await WriteCheckpointAsync(checkpoint, ct).ConfigureAwait(false);
                    await AppendJournalAsync(checkpoint, "migrated_v3", ct).ConfigureAwait(false);
                }
                if (checkpoint.LeaseOwner is null && checkpoint.Status != WorkflowStatus.Running && !HasRunningAttempts(checkpoint))
                {
                    recoveredCheckpoints[recoveredCheckpoints.FindIndex(x => x.RunId == checkpoint.RunId)] = checkpoint;
                    continue;
                }
                var rootControl = checkpoints.Single(x => x.RunId == checkpoint.RootRunId).Control;
                var control = checkpoint.Control == WorkflowControl.Cancel || rootControl == WorkflowControl.Cancel
                    ? WorkflowControl.Cancel : checkpoint.Control == WorkflowControl.Pause || rootControl == WorkflowControl.Pause
                        ? WorkflowControl.Pause : WorkflowControl.None;
                var recovered = checkpoint with
                {
                    Status = WorkflowIdentity.HasUnknownEffects(checkpoint) ? WorkflowStatus.NeedsReconciliation
                        : control == WorkflowControl.Cancel ? WorkflowStatus.Cancelled
                        : control == WorkflowControl.Pause ? WorkflowStatus.Paused
                        : checkpoint.LeaseOwner is null && checkpoint.Status != WorkflowStatus.Running ? checkpoint.Status : WorkflowStatus.Interrupted,
                    LeaseOwner = null,
                    ModelCalls = checkpoint.ModelCalls?.Select(call => call with {
                        Attempts = call.Attempts?.Select(attempt => attempt.State == WorkflowReservationState.Running
                            ? attempt with { State = WorkflowReservationState.Unknown } : attempt).ToArray() }).ToArray(),
                    Revision = checked(checkpoint.Revision + 1),
                    UpdatedAt = _timeProvider.GetUtcNow()
                };
                await WriteCheckpointAsync(recovered, ct).ConfigureAwait(false);
                await AppendJournalAsync(recovered, "recovered", ct).ConfigureAwait(false);
                recoveredCheckpoints[recoveredCheckpoints.FindIndex(x => x.RunId == recovered.RunId)] = recovered;
            }
            foreach (var rootId in checkpoints.Select(x => x.RootRunId).Distinct(StringComparer.Ordinal))
                await UpdateClockAsync(rootId, recoveredCheckpoints, ct, legacySource: checkpoints).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        await InitializeTaskGraphsAsync(ct).ConfigureAwait(false);
        await InitializeEvaluationsAsync(ct).ConfigureAwait(false);
    }

    private async Task<WorkflowCheckpoint?> ReadOneAsync(string runId, CancellationToken ct)
    {
        var query = await _database.ExecuteAsync($"SELECT payload FROM workflows WHERE run_id = {Quote(runId)}", ct).ConfigureAwait(false);
        try
        {
            return await query.MoveNextAsync(ct).ConfigureAwait(false) ? Deserialize(query.Current[0].AsText) : null;
        }
        finally { await query.DisposeAsync().ConfigureAwait(false); }
    }

    private async Task<IReadOnlyList<WorkflowCheckpoint>> ReadAllAsync(CancellationToken ct, bool allowLegacy = false)
    {
        var checkpoints = new List<WorkflowCheckpoint>();
        var query = await _database.ExecuteAsync("SELECT payload FROM workflows ORDER BY id", ct).ConfigureAwait(false);
        try
        {
            while (await query.MoveNextAsync(ct).ConfigureAwait(false)) checkpoints.Add(Deserialize(query.Current[0].AsText, allowLegacy));
        }
        finally { await query.DisposeAsync().ConfigureAwait(false); }
        return checkpoints;
    }

    private async Task<long> NextIdAsync(string table, CancellationToken ct)
    {
        var query = await _database.ExecuteAsync($"SELECT id FROM {table} ORDER BY id DESC LIMIT 1", ct).ConfigureAwait(false);
        try { return await query.MoveNextAsync(ct).ConfigureAwait(false) ? checked(query.Current[0].AsInteger + 1) : 1; }
        finally { await query.DisposeAsync().ConfigureAwait(false); }
    }

    private Task WriteCheckpointAsync(WorkflowCheckpoint checkpoint, CancellationToken ct) =>
        ExecuteAsync($"UPDATE workflows SET payload = {Quote(Serialize(checkpoint))} WHERE run_id = {Quote(checkpoint.RunId)}", ct);

    private async Task AppendJournalAsync(WorkflowCheckpoint checkpoint, string eventType, CancellationToken ct)
    {
        var id = await NextIdAsync("workflow_journal", ct).ConfigureAwait(false);
        await ExecuteAsync($"INSERT INTO workflow_journal VALUES ({id}, {Quote(checkpoint.RunId)}, {checkpoint.Revision}, {Quote(checkpoint.UpdatedAt.ToString("O"))}, {Quote(eventType)})", ct).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        var result = await _database.ExecuteAsync(sql, ct).ConfigureAwait(false);
        await result.DisposeAsync().ConfigureAwait(false);
    }

    private async Task TransactionAsync(Func<Task> action, CancellationToken ct)
    {
        await _database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await _database.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            try { await _database.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception rollbackError) { error.Data["IronPaw.WorkflowRollbackError"] = rollbackError.Message; }
            throw;
        }
    }

    private async Task InGateAsync(Func<Task> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await action().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static void Validate(WorkflowCheckpoint checkpoint, bool allowLegacy = false)
    {
        if (checkpoint.SchemaVersion != SchemaVersion && !(allowLegacy && checkpoint.SchemaVersion is 1 or 2))
            throw new NotSupportedException("This workflow checkpoint schema version is not supported.");
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.RootRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.Request.SessionKey.Value);
        if (checkpoint.Revision < 0) throw new ArgumentOutOfRangeException(nameof(checkpoint));
        if (!Enum.IsDefined(checkpoint.Status)) throw new NotSupportedException("This workflow state is not supported.");
        if (!Enum.IsDefined(checkpoint.Control)) throw new NotSupportedException("This workflow control is not supported.");
        if (checkpoint.Budget is { } budget && (budget.MaxModelCalls < 1 || budget.MaxToolCalls < 0 ||
            budget.MaxTotalTokens is <= 0 || budget.MaxExecutionTime is { } time && time <= TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(checkpoint), "Workflow budgets must allow a model call and cannot be negative.");
        if (checkpoint.Usage is { } usage && (usage.ModelCalls < 0 || usage.ToolCalls < 0 || usage.InterruptedModelCalls < 0 ||
            usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.CacheReadTokens < 0 || usage.CacheWriteTokens < 0))
            throw new ArgumentOutOfRangeException(nameof(checkpoint), "Workflow usage cannot be negative.");
        ValidateAttempts(checkpoint);
        if ((checkpoint.ToolExecutions ?? []).Select(x => x.ExecutionId).Distinct(StringComparer.Ordinal).Count() != (checkpoint.ToolExecutions?.Count ?? 0))
            throw new WorkflowConflictException("Tool execution ids must be unique within a workflow.");
        foreach (var tool in checkpoint.ToolExecutions ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tool.ExecutionId);
            if (!Enum.IsDefined(tool.State)) throw new NotSupportedException("This tool execution state is not supported.");
            if (tool.State == WorkflowToolState.Completed && tool.Result is null)
                throw new WorkflowConflictException("A completed tool execution requires an outcome.");
            if (tool.State == WorkflowToolState.WaitingForChild && string.IsNullOrWhiteSpace(tool.ChildRunId))
                throw new WorkflowConflictException("A delegated tool execution requires its child run identity.");
        }
        var requests = checkpoint.HumanRequests ?? [];
        if (requests.Select(x => x.RequestId).Distinct(StringComparer.Ordinal).Count() != requests.Count ||
            requests.Select(x => x.SourceKey).Distinct(StringComparer.Ordinal).Count() != requests.Count)
            throw new WorkflowConflictException("Human request ids and source keys must be unique within a workflow.");
        foreach (var request in requests)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
            if (!Enum.IsDefined(request.Kind)) throw new NotSupportedException("This human request kind is not supported.");
            if (request.Kind == WorkflowHumanRequestKind.ToolApproval &&
                (string.IsNullOrWhiteSpace(request.ExecutionId) || string.IsNullOrWhiteSpace(request.OperationFingerprint) ||
                request.SourceKey != "tool:" + request.ExecutionId ||
                !(checkpoint.ToolExecutions?.Any(x => x.ExecutionId == request.ExecutionId) ?? false)))
                throw new WorkflowConflictException("A tool approval must identify a recorded operation and its fingerprint.");
            if (request.Response is { } response)
            {
                if (response.CallerId != checkpoint.Request.Caller.Id)
                    throw new WorkflowConflictException("A human response must belong to the original workflow caller.");
                if (request.Kind == WorkflowHumanRequestKind.Input ? string.IsNullOrWhiteSpace(response.Answer) || response.Approved is not null
                    : response.Approved is null || response.Answer is not null)
                    throw new WorkflowConflictException("A human response must match the request kind.");
            }
        }
    }

    private static void ValidateUpdate(WorkflowCheckpoint previous, WorkflowCheckpoint next)
    {
        if (previous.RootRunId != next.RootRunId || previous.ParentRunId != next.ParentRunId ||
            previous.TaskId != next.TaskId || previous.Request.SessionKey != next.Request.SessionKey || previous.CreatedAt != next.CreatedAt ||
            previous.SchemaVersion != next.SchemaVersion)
            throw new WorkflowConflictException("Workflow identity cannot change.");
        if (previous.Control == WorkflowControl.Cancel && next.Control != WorkflowControl.Cancel)
            throw new WorkflowConflictException("Permanent workflow cancellation cannot be removed.");
        if (Serialize(previous.Budget) != Serialize(next.Budget))
            throw new WorkflowConflictException("A workflow budget cannot change after acceptance.");
        var comparableRequest = next.Request;
        if (previous.InitialTranscript is null)
        {
            // First initialization may freeze active memories and derive ownership
            // from the accepted caller; no other accepted input may change.
            if (next.Request.ToolPolicy.SenderIsOwner != previous.Request.ToolPolicy.SenderIsOwner &&
                next.Request.ToolPolicy.SenderIsOwner != previous.Request.Caller.IsOwner)
                throw new WorkflowConflictException("Tool ownership must match the accepted caller.");
            comparableRequest = next.Request with
            {
                ActiveMemories = previous.Request.ActiveMemories,
                ToolPolicy = next.Request.ToolPolicy with { SenderIsOwner = previous.Request.ToolPolicy.SenderIsOwner }
            };
        }
        if (Serialize(previous.Request) != Serialize(comparableRequest))
            throw new WorkflowConflictException("The accepted workflow request cannot change.");
        if (previous.InitialTranscript is not null && Serialize(previous.InitialTranscript) != Serialize(next.InitialTranscript))
            throw new WorkflowConflictException("The initial workflow transcript cannot change.");
        if (previous.Result is not null && Serialize(previous.Result) != Serialize(next.Result))
            throw new WorkflowConflictException("A confirmed workflow result cannot be removed or changed.");
        var previousTools = previous.ToolExecutions ?? [];
        var nextTools = next.ToolExecutions ?? [];
        if (nextTools.Count < previousTools.Count)
            throw new WorkflowConflictException("Recorded tool executions cannot be removed.");
        for (var i = 0; i < previousTools.Count; i++)
        {
            var tool = previousTools[i];
            var replacement = nextTools[i];
            if (tool.ExecutionId != replacement.ExecutionId || Serialize(tool.Call) != Serialize(replacement.Call) ||
                (tool.Result is not null && Serialize(tool.Result) != Serialize(replacement.Result)) ||
                (tool.ReconciliationNote is not null && tool.ReconciliationNote != replacement.ReconciliationNote) ||
                (tool.ChildRunId is not null && tool.ChildRunId != replacement.ChildRunId) ||
                !ValidToolTransition(tool.State, replacement.State))
                throw new WorkflowConflictException("Recorded tool intent and confirmed results cannot be removed or changed.");
        }
        var previousRequests = previous.HumanRequests ?? [];
        var nextRequests = next.HumanRequests ?? [];
        if (nextRequests.Count < previousRequests.Count)
            throw new WorkflowConflictException("Recorded human requests cannot be removed.");
        for (var i = 0; i < previousRequests.Count; i++)
        {
            var request = previousRequests[i];
            var replacement = nextRequests[i];
            if (Serialize(request with { Response = null }) != Serialize(replacement with { Response = null }) ||
                request.Response is not null && Serialize(request.Response) != Serialize(replacement.Response))
                throw new WorkflowConflictException("Recorded human requests and accepted responses cannot be removed or changed.");
        }
        var calls = previous.ModelCalls ?? [];
        if ((next.ModelCalls?.Count ?? 0) < calls.Count)
            throw new WorkflowConflictException("Recorded model calls cannot be removed.");
        for (var i = 0; i < calls.Count; i++)
            if (calls[i].Fingerprint != next.ModelCalls![i].Fingerprint || calls[i].SystemPrompt != next.ModelCalls[i].SystemPrompt ||
                (calls[i].Response is not null && Serialize(calls[i].Response) != Serialize(next.ModelCalls[i].Response)))
                throw new WorkflowConflictException("Recorded model calls and responses cannot be changed.");
        ValidateAttemptUpdates(previous, next);
        var oldUsage = previous.Usage ?? new WorkflowUsage();
        var usage = next.Usage ?? new WorkflowUsage();
        if (usage.ModelCalls < oldUsage.ModelCalls || usage.ToolCalls < oldUsage.ToolCalls ||
            usage.InputTokens < oldUsage.InputTokens || usage.OutputTokens < oldUsage.OutputTokens ||
            usage.CacheReadTokens < oldUsage.CacheReadTokens || usage.CacheWriteTokens < oldUsage.CacheWriteTokens ||
            usage.InterruptedModelCalls < oldUsage.InterruptedModelCalls)
            throw new WorkflowConflictException("Consumed workflow usage cannot decrease.");
    }

    private static void ValidateLineage(IReadOnlyList<WorkflowCheckpoint> existing, WorkflowCheckpoint proposed)
    {
        if (proposed.ParentRunId is null)
        {
            if (proposed.RootRunId != proposed.RunId)
                throw new WorkflowConflictException("A workflow without a parent must identify itself as its root.");
            return;
        }
        if (proposed.RootRunId == proposed.RunId)
            throw new WorkflowConflictException("A child workflow cannot identify itself as its root.");
        var root = existing.SingleOrDefault(x => x.RunId == proposed.RootRunId)
            ?? throw new WorkflowConflictException("A child workflow requires an existing root workflow.");
        var parent = existing.SingleOrDefault(x => x.RunId == proposed.ParentRunId)
            ?? throw new WorkflowConflictException("A child workflow requires an existing parent workflow.");
        if (root.RootRunId != root.RunId || parent.RootRunId != root.RunId)
            throw new WorkflowConflictException("The parent and child must belong to the same root workflow.");
        if (proposed.Request.Caller.Id != root.Request.Caller.Id || proposed.Request.Caller.IsOwner != root.Request.Caller.IsOwner)
            throw new WorkflowConflictException("The child caller must match the root workflow caller and ownership level.");
    }

    private static void ValidateSharedBudget(IReadOnlyList<WorkflowCheckpoint> existing, WorkflowCheckpoint proposed)
    {
        var root = proposed.RootRunId == proposed.RunId ? proposed
            : existing.SingleOrDefault(x => x.RunId == proposed.RootRunId)
                ?? throw new WorkflowConflictException("A child workflow requires an existing root workflow.");
        if (root.RootRunId != root.RunId)
            throw new WorkflowConflictException("The root workflow identity is invalid.");
        var budget = root.Budget ?? new WorkflowBudget();
        long modelCalls = proposed.Usage?.ModelCalls ?? 0;
        long toolCalls = proposed.Usage?.ToolCalls ?? 0;
        foreach (var workflow in existing.Where(x => x.RootRunId == proposed.RootRunId && x.RunId != proposed.RunId))
        {
            modelCalls += workflow.Usage?.ModelCalls ?? 0;
            toolCalls += workflow.Usage?.ToolCalls ?? 0;
        }
        if (modelCalls > budget.MaxModelCalls || toolCalls > budget.MaxToolCalls)
            throw new WorkflowBudgetExceededException("The shared workflow model or tool call budget is exhausted.");
        var localBudget = proposed.Budget ?? new WorkflowBudget();
        if ((proposed.Usage?.ModelCalls ?? 0) > localBudget.MaxModelCalls || (proposed.Usage?.ToolCalls ?? 0) > localBudget.MaxToolCalls)
            throw new WorkflowBudgetExceededException("The workflow model or tool call budget is exhausted.");
    }

    private static bool ValidToolTransition(WorkflowToolState previous, WorkflowToolState next) => previous == next ||
        previous == WorkflowToolState.Planned && next is WorkflowToolState.Started or WorkflowToolState.WaitingForChild or WorkflowToolState.Completed ||
        previous is WorkflowToolState.Started or WorkflowToolState.WaitingForChild && next == WorkflowToolState.Completed;

    private static void ValidateExecutionAdmission(IReadOnlyList<WorkflowCheckpoint> existing,
        WorkflowCheckpoint? previous, WorkflowCheckpoint proposed)
    {
        var oldUsage = previous?.Usage ?? new WorkflowUsage();
        var usage = proposed.Usage ?? new WorkflowUsage();
        var admitted = previous is null && proposed.ParentRunId is not null ||
            usage.ModelCalls > oldUsage.ModelCalls || usage.ToolCalls > oldUsage.ToolCalls ||
            (proposed.ToolExecutions ?? []).Any(tool => tool.State is WorkflowToolState.Started or WorkflowToolState.WaitingForChild &&
                !(previous?.ToolExecutions?.Any(old => old.ExecutionId == tool.ExecutionId && old.State == tool.State) ?? false));
        if (!admitted) return;
        var root = existing.SingleOrDefault(x => x.RunId == proposed.RootRunId);
        // Read the persisted control rather than a caller's proposed control so
        // a single write cannot clear a pause and admit an effect together.
        var control = proposed.Control == WorkflowControl.Cancel || root?.Control == WorkflowControl.Cancel
            ? WorkflowControl.Cancel : proposed.Control == WorkflowControl.Pause || root?.Control == WorkflowControl.Pause
                ? WorkflowControl.Pause : WorkflowControl.None;
        if (control == WorkflowControl.Cancel)
            throw new WorkflowCancelledException("This workflow has been permanently cancelled.");
        if (control == WorkflowControl.Pause)
            throw new WorkflowSuspendedException(WorkflowStatus.Paused, "This workflow is paused.");
    }

    private static void ValidateControlBoundWrites(IReadOnlyList<WorkflowCheckpoint> existing,
        WorkflowCheckpoint previous, WorkflowCheckpoint proposed)
    {
        var rootControl = existing.SingleOrDefault(x => x.RunId == proposed.RootRunId)?.Control;
        var cancelled = rootControl == WorkflowControl.Cancel || proposed.Control == WorkflowControl.Cancel;
        var newResponse = (proposed.HumanRequests ?? []).Any(request => request.Response is not null &&
            !(previous.HumanRequests?.Any(old => old.RequestId == request.RequestId && old.Response is not null) ?? false));
        if (cancelled && newResponse)
            throw new WorkflowCancelledException("Cancelled workflows cannot accept human responses.");
        // Serialize completion against root control commands, even when the
        // finishing invocation is a descendant with its own revision/lease.
        // Recording model/tool receipts and reconciling outcomes remains valid.
        if (previous.Result is null && proposed.Result?.Status is AgentRunStatus.Succeeded or AgentRunStatus.BudgetExhausted)
        {
            if (cancelled)
                throw new WorkflowCancelledException("This workflow has been permanently cancelled.");
            if (rootControl == WorkflowControl.Pause || proposed.Control == WorkflowControl.Pause)
                throw new WorkflowSuspendedException(WorkflowStatus.Paused, "This workflow is paused.");
        }
    }

    private static int FindClarificationCall(WorkflowCheckpoint checkpoint)
    {
        var calls = checkpoint.ModelCalls ?? [];
        var lastCompleted = -1;
        for (var i = calls.Count - 1; i >= 0; i--)
        {
            if (calls[i].Response is not { } response) continue;
            if (lastCompleted < 0) lastCompleted = i;
            var text = string.Concat(response.Where(x => x.Kind == "text").Select(x => x.Text));
            if (calls[i].SystemPrompt?.Contains("routing planner", StringComparison.OrdinalIgnoreCase) == true ||
                text.Contains("needs_clarification", StringComparison.Ordinal)) return i;
        }
        return Math.Max(0, lastCompleted);
    }

    private static bool IsActive(WorkflowCheckpoint checkpoint) => checkpoint.LeaseOwner is not null || WorkflowIdentity.HasUnknownEffects(checkpoint) ||
        checkpoint.Result is null && checkpoint.Control != WorkflowControl.Cancel && checkpoint.Status is not
            (WorkflowStatus.Succeeded or WorkflowStatus.Failed or WorkflowStatus.BudgetExhausted);
    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static WorkflowCheckpoint Deserialize(string payload, bool allowLegacy = false)
    {
        var checkpoint = JsonSerializer.Deserialize<WorkflowCheckpoint>(payload, JsonOptions)
            ?? throw new InvalidDataException("The workflow checkpoint is missing.");
        Validate(checkpoint, allowLegacy);
        return checkpoint;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { MaxDepth = 256 };
        options.Converters.Add(new RunIdConverter());
        options.Converters.Add(new SessionKeyConverter());
        return options;
    }

    private sealed class RunIdConverter : JsonConverter<RunId>
    {
        public override RunId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, RunId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }

    private sealed class SessionKeyConverter : JsonConverter<SessionKey>
    {
        public override SessionKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, SessionKey value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _database.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
