using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using Microsoft.Data.Sqlite;

namespace IronPaw.AgentCore.Persistence.Sqlite;

public sealed class SqliteSessionStore : ISessionStore
{
    private readonly string _connectionString;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false
    };

    public SqliteSessionStore(SqliteStoreOptions options)
    {
        _connectionString = SqliteSchemaInitializer.BuildConnectionString(options.DatabasePath);
    }

    public async Task<SessionEntry?> GetAsync(SessionKey key, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, updated_at, transcript_path, provider_override, model_override,
                   compaction_count, input_tokens, output_tokens, total_tokens,
                   total_tokens_fresh, diagnostics_json
            FROM sessions
            WHERE session_id = @sessionId
            """;
        command.Parameters.AddWithValue("@sessionId", key.Value);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return ReadSessionEntry(reader);
    }

    public async Task UpsertAsync(SessionKey key, Func<SessionEntry?, SessionEntry> mutate, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Read existing
        SessionEntry? existing = null;
        await using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.Transaction = (SqliteTransaction)transaction;
            selectCmd.CommandText = """
                SELECT session_id, updated_at, transcript_path, provider_override, model_override,
                       compaction_count, input_tokens, output_tokens, total_tokens,
                       total_tokens_fresh, diagnostics_json
                FROM sessions
                WHERE session_id = @sessionId
                """;
            selectCmd.Parameters.AddWithValue("@sessionId", key.Value);

            await using var reader = await selectCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                existing = ReadSessionEntry(reader);
            }
        }

        var updated = mutate(existing) with { UpdatedAt = DateTimeOffset.UtcNow };

        // Upsert
        await using var upsertCmd = connection.CreateCommand();
        upsertCmd.Transaction = (SqliteTransaction)transaction;
        upsertCmd.CommandText = """
            INSERT INTO sessions (session_id, updated_at, transcript_path, provider_override,
                model_override, compaction_count, input_tokens, output_tokens,
                total_tokens, total_tokens_fresh, diagnostics_json)
            VALUES (@sessionId, @updatedAt, @transcriptPath, @providerOverride,
                @modelOverride, @compactionCount, @inputTokens, @outputTokens,
                @totalTokens, @totalTokensFresh, @diagnosticsJson)
            ON CONFLICT(session_id) DO UPDATE SET
                updated_at = @updatedAt,
                transcript_path = @transcriptPath,
                provider_override = @providerOverride,
                model_override = @modelOverride,
                compaction_count = @compactionCount,
                input_tokens = @inputTokens,
                output_tokens = @outputTokens,
                total_tokens = @totalTokens,
                total_tokens_fresh = @totalTokensFresh,
                diagnostics_json = @diagnosticsJson
            """;

        BindSessionParameters(upsertCmd, updated);
        await upsertCmd.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyDictionary<SessionKey, SessionEntry>> LoadAllAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, updated_at, transcript_path, provider_override, model_override,
                   compaction_count, input_tokens, output_tokens, total_tokens,
                   total_tokens_fresh, diagnostics_json
            FROM sessions
            """;

        var result = new Dictionary<SessionKey, SessionEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var entry = ReadSessionEntry(reader);
            result[new SessionKey(entry.SessionId)] = entry;
        }

        return result;
    }

    private SessionEntry ReadSessionEntry(SqliteDataReader reader)
    {
        var diagnosticsJson = reader.IsDBNull(10) ? null : reader.GetString(10);
        IReadOnlyDictionary<string, object?>? diagnostics = null;
        if (!string.IsNullOrWhiteSpace(diagnosticsJson))
        {
            diagnostics = JsonSerializer.Deserialize<Dictionary<string, object?>>(diagnosticsJson, _jsonOptions);
        }

        return new SessionEntry(
            SessionId: reader.GetString(0),
            UpdatedAt: DateTimeOffset.Parse(reader.GetString(1)),
            TranscriptPath: reader.IsDBNull(2) ? null : reader.GetString(2),
            ProviderOverride: reader.IsDBNull(3) ? null : reader.GetString(3),
            ModelOverride: reader.IsDBNull(4) ? null : reader.GetString(4),
            CompactionCount: reader.GetInt32(5),
            InputTokens: reader.GetInt64(6),
            OutputTokens: reader.GetInt64(7),
            TotalTokens: reader.GetInt64(8),
            TotalTokensFresh: reader.GetInt32(9) != 0,
            Diagnostics: diagnostics);
    }

    private void BindSessionParameters(SqliteCommand command, SessionEntry entry)
    {
        command.Parameters.AddWithValue("@sessionId", entry.SessionId);
        command.Parameters.AddWithValue("@updatedAt", entry.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("@transcriptPath", (object?)entry.TranscriptPath ?? DBNull.Value);
        command.Parameters.AddWithValue("@providerOverride", (object?)entry.ProviderOverride ?? DBNull.Value);
        command.Parameters.AddWithValue("@modelOverride", (object?)entry.ModelOverride ?? DBNull.Value);
        command.Parameters.AddWithValue("@compactionCount", entry.CompactionCount);
        command.Parameters.AddWithValue("@inputTokens", entry.InputTokens);
        command.Parameters.AddWithValue("@outputTokens", entry.OutputTokens);
        command.Parameters.AddWithValue("@totalTokens", entry.TotalTokens);
        command.Parameters.AddWithValue("@totalTokensFresh", entry.TotalTokensFresh ? 1 : 0);
        command.Parameters.AddWithValue("@diagnosticsJson",
            entry.Diagnostics is null
                ? DBNull.Value
                : JsonSerializer.Serialize(entry.Diagnostics, _jsonOptions));
    }
}
