using IronPaw.AgentCore.Abstractions;
using Microsoft.Data.Sqlite;

namespace IronPaw.AgentCore.Persistence.Sqlite;

public sealed class SqliteMemoryStore : IMemoryStore
{
    private readonly string _connectionString;
    private readonly SqliteStoreOptions _options;

    public SqliteMemoryStore(SqliteStoreOptions options)
    {
        _options = options;
        _connectionString = SqliteSchemaInitializer.BuildConnectionString(options.DatabasePath);
    }

    public async Task<IReadOnlyList<MemoryEntry>> LoadGlobalAsync(CancellationToken ct)
    {
        return await LoadByScopeAsync("Global", sessionId: null, ct);
    }

    public async Task<IReadOnlyList<MemoryEntry>> LoadSessionAsync(SessionKey sessionKey, CancellationToken ct)
    {
        return await LoadByScopeAsync("Session", sessionKey.Value, ct);
    }

    public async Task<MemoryEntry> AddAsync(MemoryEntry entry, CancellationToken ct)
    {
        var truncatedContent = entry.Content.Length > _options.MaxMemoryContentChars
            ? entry.Content[.._options.MaxMemoryContentChars]
            : entry.Content;
        var finalEntry = entry with { Content = truncatedContent };

        var isGlobal = finalEntry.Scope == MemoryScope.Global;
        var maxEntries = isGlobal ? _options.MaxGlobalMemories : _options.MaxSessionMemories;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Enforce capacity: count current entries, delete oldest if at limit
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.Transaction = (SqliteTransaction)transaction;
            if (isGlobal)
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM memories WHERE scope = 'Global'";
            }
            else
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM memories WHERE scope = 'Session' AND session_id = @sessionId";
                countCmd.Parameters.AddWithValue("@sessionId", finalEntry.SessionId!);
            }

            var count = (long)(await countCmd.ExecuteScalarAsync(ct))!;
            if (count >= maxEntries)
            {
                var toDelete = count - maxEntries + 1;
                await using var deleteCmd = connection.CreateCommand();
                deleteCmd.Transaction = (SqliteTransaction)transaction;
                if (isGlobal)
                {
                    deleteCmd.CommandText = """
                        DELETE FROM memories WHERE id IN (
                            SELECT id FROM memories WHERE scope = 'Global'
                            ORDER BY created_at ASC LIMIT @limit
                        )
                        """;
                }
                else
                {
                    deleteCmd.CommandText = """
                        DELETE FROM memories WHERE id IN (
                            SELECT id FROM memories WHERE scope = 'Session' AND session_id = @sessionId
                            ORDER BY created_at ASC LIMIT @limit
                        )
                        """;
                    deleteCmd.Parameters.AddWithValue("@sessionId", finalEntry.SessionId!);
                }

                deleteCmd.Parameters.AddWithValue("@limit", toDelete);
                await deleteCmd.ExecuteNonQueryAsync(ct);
            }
        }

        // Insert
        await using var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = (SqliteTransaction)transaction;
        insertCmd.CommandText = """
            INSERT INTO memories (id, content, scope, created_at, session_id, category, source)
            VALUES (@id, @content, @scope, @createdAt, @sessionId, @category, @source)
            """;
        insertCmd.Parameters.AddWithValue("@id", finalEntry.Id);
        insertCmd.Parameters.AddWithValue("@content", finalEntry.Content);
        insertCmd.Parameters.AddWithValue("@scope", finalEntry.Scope.ToString());
        insertCmd.Parameters.AddWithValue("@createdAt", finalEntry.CreatedAt.ToString("O"));
        insertCmd.Parameters.AddWithValue("@sessionId", (object?)finalEntry.SessionId ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@category", (object?)finalEntry.Category ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@source", (object?)finalEntry.Source ?? DBNull.Value);

        await insertCmd.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);

        return finalEntry;
    }

    public async Task<bool> RemoveAsync(string memoryId, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memories WHERE id = @id";
        command.Parameters.AddWithValue("@id", memoryId);

        var affected = await command.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    public async Task<IReadOnlyList<MemoryEntry>> SearchAsync(
        string query,
        MemoryScope? scope,
        SessionKey? sessionKey,
        CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var conditions = new List<string> { "content LIKE @pattern" };
        if (scope == MemoryScope.Global)
        {
            conditions.Add("scope = 'Global'");
        }
        else if (scope == MemoryScope.Session)
        {
            conditions.Add("scope = 'Session'");
            if (sessionKey.HasValue)
            {
                conditions.Add("session_id = @sessionId");
            }
        }
        else if (sessionKey.HasValue)
        {
            // scope is null — search both, but filter session memories by session key
            conditions.Add("(scope = 'Global' OR (scope = 'Session' AND session_id = @sessionId))");
        }

        var sql = $"SELECT id, content, scope, created_at, session_id, category, source FROM memories WHERE {string.Join(" AND ", conditions)} ORDER BY created_at";

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@pattern", $"%{query}%");
        if (sessionKey.HasValue)
        {
            command.Parameters.AddWithValue("@sessionId", sessionKey.Value.Value);
        }

        var results = new List<MemoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadMemoryEntry(reader));
        }

        return results;
    }

    private async Task<IReadOnlyList<MemoryEntry>> LoadByScopeAsync(string scope, string? sessionId, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        if (sessionId is not null)
        {
            command.CommandText = """
                SELECT id, content, scope, created_at, session_id, category, source
                FROM memories
                WHERE scope = @scope AND session_id = @sessionId
                ORDER BY created_at
                """;
            command.Parameters.AddWithValue("@sessionId", sessionId);
        }
        else
        {
            command.CommandText = """
                SELECT id, content, scope, created_at, session_id, category, source
                FROM memories
                WHERE scope = @scope
                ORDER BY created_at
                """;
        }

        command.Parameters.AddWithValue("@scope", scope);

        var entries = new List<MemoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(ReadMemoryEntry(reader));
        }

        return entries;
    }

    private static MemoryEntry ReadMemoryEntry(SqliteDataReader reader)
    {
        return new MemoryEntry(
            Id: reader.GetString(0),
            Content: reader.GetString(1),
            Scope: Enum.Parse<MemoryScope>(reader.GetString(2)),
            CreatedAt: DateTimeOffset.Parse(reader.GetString(3)),
            SessionId: reader.IsDBNull(4) ? null : reader.GetString(4),
            Category: reader.IsDBNull(5) ? null : reader.GetString(5),
            Source: reader.IsDBNull(6) ? null : reader.GetString(6));
    }
}
