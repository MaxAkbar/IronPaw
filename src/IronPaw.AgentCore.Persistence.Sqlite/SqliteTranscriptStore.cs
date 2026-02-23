using IronPaw.AgentCore.Abstractions;
using Microsoft.Data.Sqlite;

namespace IronPaw.AgentCore.Persistence.Sqlite;

public sealed class SqliteTranscriptStore : ITranscriptStore
{
    private readonly string _connectionString;

    public SqliteTranscriptStore(SqliteStoreOptions options)
    {
        _connectionString = SqliteSchemaInitializer.BuildConnectionString(options.DatabasePath);
    }

    public async Task<IReadOnlyList<ChatMessage>> LoadAsync(SessionKey key, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT role, content, tool_call_id, tool_name
            FROM transcript_messages
            WHERE session_id = @sessionId
            ORDER BY seq
            """;
        command.Parameters.AddWithValue("@sessionId", key.Value);

        var messages = new List<ChatMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            messages.Add(new ChatMessage(
                Role: Enum.Parse<ChatRole>(reader.GetString(0)),
                Content: reader.GetString(1),
                ToolCallId: reader.IsDBNull(2) ? null : reader.GetString(2),
                ToolName: reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return messages;
    }

    public async Task AppendAsync(SessionKey key, IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Get current max seq
        long nextSeq;
        await using (var seqCmd = connection.CreateCommand())
        {
            seqCmd.Transaction = (SqliteTransaction)transaction;
            seqCmd.CommandText = """
                SELECT COALESCE(MAX(seq), -1) FROM transcript_messages WHERE session_id = @sessionId
                """;
            seqCmd.Parameters.AddWithValue("@sessionId", key.Value);
            var maxSeq = (long)(await seqCmd.ExecuteScalarAsync(ct))!;
            nextSeq = maxSeq + 1;
        }

        await using var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = (SqliteTransaction)transaction;
        insertCmd.CommandText = """
            INSERT INTO transcript_messages (session_id, seq, role, content, tool_call_id, tool_name)
            VALUES (@sessionId, @seq, @role, @content, @toolCallId, @toolName)
            """;

        var pSessionId = insertCmd.Parameters.Add("@sessionId", SqliteType.Text);
        var pSeq = insertCmd.Parameters.Add("@seq", SqliteType.Integer);
        var pRole = insertCmd.Parameters.Add("@role", SqliteType.Text);
        var pContent = insertCmd.Parameters.Add("@content", SqliteType.Text);
        var pToolCallId = insertCmd.Parameters.Add("@toolCallId", SqliteType.Text);
        var pToolName = insertCmd.Parameters.Add("@toolName", SqliteType.Text);

        foreach (var message in messages)
        {
            pSessionId.Value = key.Value;
            pSeq.Value = nextSeq++;
            pRole.Value = message.Role.ToString();
            pContent.Value = message.Content;
            pToolCallId.Value = (object?)message.ToolCallId ?? DBNull.Value;
            pToolName.Value = (object?)message.ToolName ?? DBNull.Value;
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task ReplaceAsync(SessionKey key, IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Delete existing
        await using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.Transaction = (SqliteTransaction)transaction;
            deleteCmd.CommandText = "DELETE FROM transcript_messages WHERE session_id = @sessionId";
            deleteCmd.Parameters.AddWithValue("@sessionId", key.Value);
            await deleteCmd.ExecuteNonQueryAsync(ct);
        }

        // Insert fresh
        await using var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = (SqliteTransaction)transaction;
        insertCmd.CommandText = """
            INSERT INTO transcript_messages (session_id, seq, role, content, tool_call_id, tool_name)
            VALUES (@sessionId, @seq, @role, @content, @toolCallId, @toolName)
            """;

        var pSessionId = insertCmd.Parameters.Add("@sessionId", SqliteType.Text);
        var pSeq = insertCmd.Parameters.Add("@seq", SqliteType.Integer);
        var pRole = insertCmd.Parameters.Add("@role", SqliteType.Text);
        var pContent = insertCmd.Parameters.Add("@content", SqliteType.Text);
        var pToolCallId = insertCmd.Parameters.Add("@toolCallId", SqliteType.Text);
        var pToolName = insertCmd.Parameters.Add("@toolName", SqliteType.Text);

        long seq = 0;
        foreach (var message in messages)
        {
            pSessionId.Value = key.Value;
            pSeq.Value = seq++;
            pRole.Value = message.Role.ToString();
            pContent.Value = message.Content;
            pToolCallId.Value = (object?)message.ToolCallId ?? DBNull.Value;
            pToolName.Value = (object?)message.ToolName ?? DBNull.Value;
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}
