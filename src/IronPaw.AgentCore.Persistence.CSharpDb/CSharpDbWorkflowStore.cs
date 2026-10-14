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
public sealed class CSharpDbWorkflowStore : IWorkflowStore, IAsyncDisposable, IDisposable
{
    private const int SchemaVersion = 1;
    private readonly Database _database;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private CSharpDbWorkflowStore(Database database) => _database = database;

    public static async Task<CSharpDbWorkflowStore> OpenAsync(string databasePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var database = await Database.OpenAsync(path, new DatabaseOptions
        {
            StorageEngineOptions = new StorageEngineOptions { PrimaryFileShare = FileShare.None }
        }, ct).ConfigureAwait(false);
        var store = new CSharpDbWorkflowStore(database);
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
                ValidateSharedBudget(existing, checkpoint);
                var id = await NextIdAsync("workflows", ct).ConfigureAwait(false);
                await ExecuteAsync($"INSERT INTO workflows VALUES ({id}, {Quote(checkpoint.RunId)}, {Quote(Serialize(checkpoint))})", ct).ConfigureAwait(false);
                await AppendJournalAsync(checkpoint, "accepted", ct).ConfigureAwait(false);
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
                if (IsActive(checkpoint) && existing.Any(x => x.RunId != checkpoint.RunId &&
                    x.Request.SessionKey == checkpoint.Request.SessionKey && IsActive(x)))
                    throw new WorkflowConflictException("This session already has another unfinished workflow.");
                ValidateSharedBudget(existing, checkpoint);
                saved = checkpoint with { Revision = checked(expectedRevision + 1), UpdatedAt = DateTimeOffset.UtcNow };
                await WriteCheckpointAsync(saved, ct).ConfigureAwait(false);
                await AppendJournalAsync(saved, eventType, ct).ConfigureAwait(false);
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
        // Validate every version before mutating any checkpoint. Exclusive acquisition
        // guarantees that a persisted lease cannot belong to another live store.
        var checkpoints = await ReadAllAsync(ct).ConfigureAwait(false);
        var interrupted = checkpoints.Where(x => x.LeaseOwner is not null || x.Status == WorkflowStatus.Running).ToArray();
        if (interrupted.Length == 0) return;
        await TransactionAsync(async () =>
        {
            foreach (var checkpoint in interrupted)
            {
                var unknown = checkpoint.ToolExecutions?.Any(x => x.Result is null) == true;
                var recovered = checkpoint with
                {
                    Status = unknown ? WorkflowStatus.NeedsReconciliation : WorkflowStatus.Interrupted,
                    LeaseOwner = null,
                    Revision = checked(checkpoint.Revision + 1),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await WriteCheckpointAsync(recovered, ct).ConfigureAwait(false);
                await AppendJournalAsync(recovered, "recovered", ct).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<WorkflowCheckpoint>> ReadAllAsync(CancellationToken ct)
    {
        var checkpoints = new List<WorkflowCheckpoint>();
        var query = await _database.ExecuteAsync("SELECT payload FROM workflows ORDER BY id", ct).ConfigureAwait(false);
        try
        {
            while (await query.MoveNextAsync(ct).ConfigureAwait(false)) checkpoints.Add(Deserialize(query.Current[0].AsText));
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

    private static void Validate(WorkflowCheckpoint checkpoint)
    {
        if (checkpoint.SchemaVersion != SchemaVersion)
            throw new NotSupportedException("This workflow checkpoint schema version is not supported.");
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.RootRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.Request.SessionKey.Value);
        if (checkpoint.Revision < 0) throw new ArgumentOutOfRangeException(nameof(checkpoint));
        if (!Enum.IsDefined(checkpoint.Status)) throw new NotSupportedException("This workflow state is not supported.");
        if (checkpoint.Budget is { } budget && (budget.MaxModelCalls < 1 || budget.MaxToolCalls < 0))
            throw new ArgumentOutOfRangeException(nameof(checkpoint), "Workflow budgets must allow a model call and cannot be negative.");
        if (checkpoint.Usage is { } usage && (usage.ModelCalls < 0 || usage.ToolCalls < 0 || usage.InterruptedModelCalls < 0 ||
            usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.CacheReadTokens < 0 || usage.CacheWriteTokens < 0))
            throw new ArgumentOutOfRangeException(nameof(checkpoint), "Workflow usage cannot be negative.");
        if ((checkpoint.ToolExecutions ?? []).Select(x => x.ExecutionId).Distinct(StringComparer.Ordinal).Count() != (checkpoint.ToolExecutions?.Count ?? 0))
            throw new WorkflowConflictException("Tool execution ids must be unique within a workflow.");
    }

    private static void ValidateUpdate(WorkflowCheckpoint previous, WorkflowCheckpoint next)
    {
        if (previous.RootRunId != next.RootRunId || previous.ParentRunId != next.ParentRunId ||
            previous.TaskId != next.TaskId || previous.Request.SessionKey != next.Request.SessionKey || previous.CreatedAt != next.CreatedAt)
            throw new WorkflowConflictException("Workflow identity cannot change.");
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
        foreach (var tool in previous.ToolExecutions ?? [])
        {
            var replacement = next.ToolExecutions?.SingleOrDefault(x => x.ExecutionId == tool.ExecutionId);
            if (replacement is null || Serialize(tool.Call) != Serialize(replacement.Call) ||
                (tool.Result is not null && Serialize(tool.Result) != Serialize(replacement.Result)))
                throw new WorkflowConflictException("Recorded tool intent and confirmed results cannot be removed or changed.");
        }
        var calls = previous.ModelCalls ?? [];
        if ((next.ModelCalls?.Count ?? 0) < calls.Count)
            throw new WorkflowConflictException("Recorded model calls cannot be removed.");
        for (var i = 0; i < calls.Count; i++)
            if (calls[i].Fingerprint != next.ModelCalls![i].Fingerprint || calls[i].SystemPrompt != next.ModelCalls[i].SystemPrompt ||
                (calls[i].Response is not null && Serialize(calls[i].Response) != Serialize(next.ModelCalls[i].Response)))
                throw new WorkflowConflictException("Recorded model calls and responses cannot be changed.");
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

    private static bool IsActive(WorkflowCheckpoint checkpoint) => checkpoint.Result is null && checkpoint.Status is not
        (WorkflowStatus.Succeeded or WorkflowStatus.Failed or WorkflowStatus.BudgetExhausted);
    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static WorkflowCheckpoint Deserialize(string payload)
    {
        var checkpoint = JsonSerializer.Deserialize<WorkflowCheckpoint>(payload, JsonOptions)
            ?? throw new InvalidDataException("The workflow checkpoint is missing.");
        Validate(checkpoint);
        return checkpoint;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions();
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
