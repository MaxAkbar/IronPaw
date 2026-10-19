using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpDB.Engine;
using CSharpDB.Storage.StorageEngine;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence.CSharpDb;

/// <summary>Shared application projections. Each operation owns the sidecar lock and database
/// only until its transaction has committed and its connection has closed.</summary>
public sealed class CSharpDbApplicationStore : ISessionStore, ITranscriptStore, IMemoryStore, IRunHistoryStore
{
    private readonly CSharpDbApplicationStoreOptions _options;
    private readonly TimeProvider _clock;
    private readonly Func<CancellationToken, Task>? _beforeImportCommit;
    internal static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public CSharpDbApplicationStore(CSharpDbApplicationStoreOptions options, TimeProvider? timeProvider = null)
        : this(options, timeProvider, null) { }

    internal CSharpDbApplicationStore(CSharpDbApplicationStoreOptions options, TimeProvider? timeProvider,
        Func<CancellationToken, Task>? beforeImportCommit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        if (options.MaxGlobalMemories < 1 || options.MaxSessionMemories < 1 || options.MaxMemoryContentChars < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Memory limits must be positive.");
        _options = options with { DatabasePath = Path.GetFullPath(options.DatabasePath),
            LegacyJsonStateRoot = Path.GetFullPath(options.LegacyJsonStateRoot ?? Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!) };
        _clock = timeProvider ?? TimeProvider.System;
        _beforeImportCommit = beforeImportCommit;
    }

    public Task<SessionEntry?> GetAsync(SessionKey key, CancellationToken ct) => OperationAsync(async db =>
        (await ReadRowsAsync(db, "session", key.Value, ct)).Select(r => Deserialize<SessionEntry>(r.Payload)).SingleOrDefault(), ct);

    public Task UpsertAsync(SessionKey key, Func<SessionEntry?, SessionEntry> mutate, CancellationToken ct) => OperationAsync(async db =>
    {
        var rows = await ReadRowsAsync(db, "session", key.Value, ct);
        var updated = mutate(rows.Count == 0 ? null : Deserialize<SessionEntry>(rows.Single().Payload))
            ?? throw new InvalidOperationException("Session mutation returned null.");
        await PutAsync(db, rows.SingleOrDefault(), "session", key.Value, key.Value,
            updated with { UpdatedAt = _clock.GetUtcNow() }, ct);
        return true;
    }, ct);

    public Task<IReadOnlyDictionary<SessionKey, SessionEntry>> LoadAllAsync(CancellationToken ct) => OperationAsync<IReadOnlyDictionary<SessionKey, SessionEntry>>(async db =>
        (await ReadRowsAsync(db, "session", null, ct)).ToDictionary(r => new SessionKey(r.Scope), r => Deserialize<SessionEntry>(r.Payload)), ct);

    public Task<IReadOnlyList<ChatMessage>> LoadAsync(SessionKey key, CancellationToken ct) => OperationAsync<IReadOnlyList<ChatMessage>>(async db =>
        (await ReadRowsAsync(db, "transcript", key.Value, ct)).Select(r => Deserialize<ChatMessage>(r.Payload)).ToArray(), ct);

    public Task AppendAsync(SessionKey key, IEnumerable<ChatMessage> messages, CancellationToken ct) => WriteTranscriptAsync(key, messages, false, ct);
    public Task ReplaceAsync(SessionKey key, IEnumerable<ChatMessage> messages, CancellationToken ct) => WriteTranscriptAsync(key, messages, true, ct);

    private Task WriteTranscriptAsync(SessionKey key, IEnumerable<ChatMessage> messages, bool replace, CancellationToken ct) => OperationAsync(async db =>
    {
        if (replace)
            await ExecuteAsync(db, $"DELETE FROM application_records WHERE kind = 'transcript' AND scope = {Quote(key.Value)}", ct);
        // Enumeration is inside the transaction: cancellation or a failed producer also rolls back deletion.
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(message);
            await InsertAsync(db, "transcript", key.Value, "", message, ct);
        }
        return true;
    }, ct);

    public Task<IReadOnlyList<MemoryEntry>> LoadGlobalAsync(CancellationToken ct) => LoadMemoriesAsync("", ct);
    public Task<IReadOnlyList<MemoryEntry>> LoadSessionAsync(SessionKey sessionKey, CancellationToken ct) => LoadMemoriesAsync(sessionKey.Value, ct);
    private Task<IReadOnlyList<MemoryEntry>> LoadMemoriesAsync(string scope, CancellationToken ct) => OperationAsync<IReadOnlyList<MemoryEntry>>(async db =>
        (await ReadRowsAsync(db, "memory", scope, ct)).Select(r => Deserialize<MemoryEntry>(r.Payload)).ToArray(), ct);

    public Task<MemoryEntry> AddAsync(MemoryEntry entry, CancellationToken ct) => OperationAsync(async db =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Id);
        ArgumentNullException.ThrowIfNull(entry.Content);
        if (!Enum.IsDefined(entry.Scope)) throw new ArgumentException("Unknown memory scope.", nameof(entry));
        var scope = entry.Scope == MemoryScope.Global ? "" : new SessionKey(entry.SessionId!).Value;
        var final = entry with { Content = entry.Content[..Math.Min(entry.Content.Length, _options.MaxMemoryContentChars)] };
        var rows = await ReadRowsAsync(db, "memory", scope, ct);
        var limit = entry.Scope == MemoryScope.Global ? _options.MaxGlobalMemories : _options.MaxSessionMemories;
        foreach (var row in rows.Take(Math.Max(0, rows.Count - limit + 1))) await DeleteAsync(db, row, ct);
        await InsertAsync(db, "memory", scope, entry.Id, final, ct);
        return final;
    }, ct);

    public Task<bool> RemoveAsync(string memoryId, CancellationToken ct) => OperationAsync(async db =>
    {
        var rows = (await ReadRowsAsync(db, "memory", null, ct)).Where(r => r.Key.Equals(memoryId, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var row in rows) await DeleteAsync(db, row, ct);
        return rows.Length > 0;
    }, ct);

    public Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, MemoryScope? scope, SessionKey? sessionKey, CancellationToken ct) =>
        OperationAsync<IReadOnlyList<MemoryEntry>>(async db => (await ReadRowsAsync(db, "memory", null, ct))
            .Select(r => Deserialize<MemoryEntry>(r.Payload))
            .Where(m => (scope is null || m.Scope == scope) &&
                (m.Scope == MemoryScope.Global || sessionKey.HasValue && m.SessionId == sessionKey.Value.Value) &&
                m.Content.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray(), ct);

    public Task RecordAsync(RunSummary summary, CancellationToken ct) => OperationAsync(async db =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary.RunId);
        var row = (await ReadRowsAsync(db, "history", null, ct)).SingleOrDefault(r => r.Key.Equals(summary.RunId, StringComparison.OrdinalIgnoreCase));
        await PutAsync(db, row, "history", summary.SessionId, summary.RunId, summary, ct);
        return true;
    }, ct);

    Task<IReadOnlyList<RunSummary>> IRunHistoryStore.LoadAllAsync(CancellationToken ct) => ReadHistoryAsync(null, ct);
    public Task<IReadOnlyList<RunSummary>> LoadBySessionAsync(SessionKey sessionKey, CancellationToken ct) => ReadHistoryAsync(sessionKey.Value, ct);
    private Task<IReadOnlyList<RunSummary>> ReadHistoryAsync(string? session, CancellationToken ct) => OperationAsync<IReadOnlyList<RunSummary>>(async db =>
        (await ReadRowsAsync(db, "history", null, ct)).Where(r => session is null || r.Scope.Equals(session, StringComparison.OrdinalIgnoreCase))
            .Select(r => Deserialize<RunSummary>(r.Payload)).ToArray(), ct);
    public Task<RunSummary?> GetAsync(string runId, CancellationToken ct) => OperationAsync(async db =>
        (await ReadRowsAsync(db, "history", null, ct)).Where(r => r.Key.Equals(runId, StringComparison.OrdinalIgnoreCase))
            .Select(r => Deserialize<RunSummary>(r.Payload)).SingleOrDefault(), ct);

    internal Task<AgentMessage> SendMessageAsync(AgentMessageSendRequest request, CancellationToken ct) => OperationAsync(async db =>
    {
        await CleanupMessagesAsync(db, ct);
        var message = new AgentMessage(Guid.NewGuid().ToString("n"), request.SessionKey, request.ScopeRunId,
            request.SenderAgentId, request.RecipientAgentId, request.Content, _clock.GetUtcNow(), request.CorrelationId);
        await InsertAsync(db, "message", request.SessionKey.Value, message.MessageId, new InboxMessage(message, false), ct);
        return message;
    }, ct);

    internal Task<IReadOnlyList<AgentMessage>> ReadMessagesAsync(AgentMessageReadRequest request, CancellationToken ct) => OperationAsync<IReadOnlyList<AgentMessage>>(async db =>
    {
        await CleanupMessagesAsync(db, ct);
        var matches = (await ReadRowsAsync(db, "message", request.SessionKey.Value, ct))
            .Select(r => (Row: r, Entry: Deserialize<InboxMessage>(r.Payload)))
            .Where(x => !x.Entry.Read && x.Entry.Message.ScopeRunId == request.ScopeRunId &&
                x.Entry.Message.RecipientAgentId == request.RecipientAgentId &&
                (request.SenderAgentId is null || x.Entry.Message.SenderAgentId == request.SenderAgentId) &&
                (request.CorrelationId is null || x.Entry.Message.CorrelationId == request.CorrelationId))
            .Take(Math.Clamp(request.MaxMessages, 1, 100)).ToArray();
        if (request.MarkAsRead)
            foreach (var (row, entry) in matches) await PutAsync(db, row, "message", row.Scope, row.Key, entry with { Read = true }, ct);
        return matches.Select(x => x.Entry.Message).ToArray();
    }, ct);

    private async Task CleanupMessagesAsync(Database db, CancellationToken ct)
    {
        if (_options.MessageTtlDays <= 0) return;
        var cutoff = _clock.GetUtcNow().AddDays(-_options.MessageTtlDays);
        foreach (var row in await ReadRowsAsync(db, "message", null, ct))
            if (Deserialize<InboxMessage>(row.Payload).Message.SentAt < cutoff) await DeleteAsync(db, row, ct);
    }

    // Always enter on a worker, even when the engine completes its first awaits synchronously.
    // Construction is lazy; workflow inspection never opens or migrates application data.
    private Task<T> OperationAsync<T>(Func<Database, Task<T>> operation, CancellationToken ct) => Task.Run(async () =>
    {
        await using var fileLock = await ApplicationFileLock.AcquireAsync(_options.DatabasePath, ct);
        await using var db = await Database.OpenAsync(_options.DatabasePath, new DatabaseOptions
        { StorageEngineOptions = new StorageEngineOptions { PrimaryFileShare = FileShare.None } }, ct);
        await ExecuteAsync(db, "CREATE TABLE IF NOT EXISTS application_schema (id INTEGER PRIMARY KEY, version INTEGER)", ct);
        var hasVersion = false;
        await using (var version = await db.ExecuteAsync("SELECT version FROM application_schema WHERE id = 1", ct))
        {
            hasVersion = await version.MoveNextAsync(ct);
            if (hasVersion)
            {
                if (version.Current[0].AsInteger != 1) throw new InvalidDataException("Unsupported application.cdb schema. Use a compatible IronPaw version.");
            }
        }
        if (!hasVersion) await ExecuteAsync(db, "INSERT INTO application_schema VALUES (1, 1)", ct);
        await ExecuteAsync(db, "CREATE TABLE IF NOT EXISTS application_records (id INTEGER PRIMARY KEY, kind TEXT, scope TEXT, record_key TEXT, payload TEXT)", ct);
        await ImportOnceAsync(db, ct);
        await db.BeginTransactionAsync(ct);
        try
        {
            var result = await operation(db);
            ct.ThrowIfCancellationRequested();
            await db.CommitAsync(ct);
            return result;
        }
        catch { await db.RollbackAsync(CancellationToken.None); throw; }
    }, ct);

    private async Task ImportOnceAsync(Database db, CancellationToken ct)
    {
        if ((await ReadRowsAsync(db, "metadata", null, ct)).Any(r => r.Key == "json-import-v1")) return;
        var snapshot = await ApplicationJsonImport.ReadAsync(_options.LegacyJsonStateRoot!, ct);
        await db.BeginTransactionAsync(ct);
        try
        {
            foreach (var item in snapshot.Records) await InsertAsync(db, item.Kind, item.Scope, item.Key, item.Value, ct);
            if (_beforeImportCommit is not null) await _beforeImportCommit(ct);
            await snapshot.VerifyUnchangedAsync(ct);
            await InsertAsync(db, "metadata", "", "json-import-v1", new { CompletedAt = _clock.GetUtcNow(), SourceRoot = _options.LegacyJsonStateRoot }, ct);
            ct.ThrowIfCancellationRequested();
            await db.CommitAsync(ct);
        }
        catch { await db.RollbackAsync(CancellationToken.None); throw; }
    }

    internal sealed record Row(long Id, string Scope, string Key, string Payload);
    private sealed record InboxMessage(AgentMessage Message, bool Read);
    internal static async Task<List<Row>> ReadRowsAsync(Database db, string kind, string? scope, CancellationToken ct)
    {
        var sql = $"SELECT id, scope, record_key, payload FROM application_records WHERE kind = {Quote(kind)}" +
            (scope is null ? "" : $" AND scope = {Quote(scope)}") + " ORDER BY id";
        await using var result = await db.ExecuteAsync(sql, ct);
        var rows = new List<Row>();
        while (await result.MoveNextAsync(ct)) rows.Add(new(result.Current[0].AsInteger, result.Current[1].AsText, result.Current[2].AsText, result.Current[3].AsText));
        return rows;
    }
    internal static async Task InsertAsync<T>(Database db, string kind, string scope, string key, T value, CancellationToken ct)
    {
        long id = 1;
        await using (var top = await db.ExecuteAsync("SELECT id FROM application_records ORDER BY id DESC LIMIT 1", ct))
            if (await top.MoveNextAsync(ct)) id = checked(top.Current[0].AsInteger + 1);
        await ExecuteAsync(db, $"INSERT INTO application_records VALUES ({id}, {Quote(kind)}, {Quote(scope)}, {Quote(key)}, {Quote(JsonSerializer.Serialize(value, JsonOptions))})", ct);
    }
    private static Task PutAsync<T>(Database db, Row? row, string kind, string scope, string key, T value, CancellationToken ct) => row is null
        ? InsertAsync(db, kind, scope, key, value, ct)
        : ExecuteAsync(db, $"UPDATE application_records SET scope = {Quote(scope)}, record_key = {Quote(key)}, payload = {Quote(JsonSerializer.Serialize(value, JsonOptions))} WHERE id = {row.Id}", ct);
    private static Task DeleteAsync(Database db, Row row, CancellationToken ct) => ExecuteAsync(db, $"DELETE FROM application_records WHERE id = {row.Id}", ct);
    internal static async Task ExecuteAsync(Database db, string sql, CancellationToken ct) { await using var result = await db.ExecuteAsync(sql, ct); }
    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    internal static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidDataException("Missing application record.");
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { MaxDepth = 256 };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new SessionKeyConverter());
        options.Converters.Add(new RunIdConverter());
        return options;
    }
    private sealed class SessionKeyConverter : JsonConverter<SessionKey>
    {
        public override SessionKey Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, SessionKey value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }
    private sealed class RunIdConverter : JsonConverter<RunId>
    {
        public override RunId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, RunId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }
}
