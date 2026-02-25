using IronPaw.AgentCore.Abstractions;
using Microsoft.Data.Sqlite;

namespace IronPaw.AgentCore.Persistence.Sqlite;

public sealed class SqliteAgentMessageBus : IAgentMessageBus
{
    private const int MaxReadLimit = 100;
    private readonly string _connectionString;
    private readonly IAgentRegistry _agentRegistry;
    private readonly int _messageTtlDays;

    public SqliteAgentMessageBus(SqliteStoreOptions options, IAgentRegistry agentRegistry)
    {
        _connectionString = SqliteSchemaInitializer.BuildConnectionString(options.DatabasePath);
        _agentRegistry = agentRegistry;
        _messageTtlDays = options.MessageTtlDays;
    }

    public async Task<AgentMessage> SendAsync(AgentMessageSendRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var senderAgentId = RequireAgentId(request.SenderAgentId, nameof(request.SenderAgentId));
        var recipientAgentId = RequireAgentId(request.RecipientAgentId, nameof(request.RecipientAgentId));
        var content = request.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Message content cannot be empty.", nameof(request.Content));
        }

        var recipient = _agentRegistry.Get(recipientAgentId);
        if (recipient is null)
        {
            throw new InvalidOperationException($"Recipient agent '{recipientAgentId}' is not registered.");
        }

        if (!recipient.CanReceiveMessagesFrom(senderAgentId))
        {
            throw new UnauthorizedAccessException(
                $"Agent '{senderAgentId}' is not authorized to message '{recipientAgentId}'.");
        }

        var message = new AgentMessage(
            MessageId: Guid.NewGuid().ToString("n"),
            SessionKey: request.SessionKey,
            ScopeRunId: request.ScopeRunId,
            SenderAgentId: senderAgentId,
            RecipientAgentId: recipientAgentId,
            Content: content,
            SentAt: DateTimeOffset.UtcNow,
            CorrelationId: NormalizeNullable(request.CorrelationId));

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await CleanupExpiredMessagesAsync(connection, transaction: null, ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_messages (
                message_id, session_id, scope_run_id, sender_agent_id, recipient_agent_id,
                content, correlation_id, sent_at, read_at)
            VALUES (
                @messageId, @sessionId, @scopeRunId, @senderAgentId, @recipientAgentId,
                @content, @correlationId, @sentAt, NULL)
            """;
        command.Parameters.AddWithValue("@messageId", message.MessageId);
        command.Parameters.AddWithValue("@sessionId", message.SessionKey.Value);
        command.Parameters.AddWithValue("@scopeRunId", message.ScopeRunId.Value);
        command.Parameters.AddWithValue("@senderAgentId", message.SenderAgentId);
        command.Parameters.AddWithValue("@recipientAgentId", message.RecipientAgentId);
        command.Parameters.AddWithValue("@content", message.Content);
        command.Parameters.AddWithValue("@correlationId", (object?)message.CorrelationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@sentAt", message.SentAt.ToString("O"));

        await command.ExecuteNonQueryAsync(ct);
        return message;
    }

    public async Task<IReadOnlyList<AgentMessage>> ReadAsync(AgentMessageReadRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var recipientAgentId = RequireAgentId(request.RecipientAgentId, nameof(request.RecipientAgentId));
        var senderFilter = NormalizeNullable(request.SenderAgentId);
        var correlationFilter = NormalizeNullable(request.CorrelationId);
        var max = Math.Clamp(request.MaxMessages, 1, MaxReadLimit);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var transaction = await connection.BeginTransactionAsync(ct);
        var sqlTransaction = (SqliteTransaction)transaction;

        await CleanupExpiredMessagesAsync(connection, sqlTransaction, ct);

        var entries = new List<InboxEntry>();
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = sqlTransaction;
            selectCommand.CommandText = """
                SELECT id, message_id, session_id, scope_run_id, sender_agent_id, recipient_agent_id,
                       content, correlation_id, sent_at
                FROM agent_messages
                WHERE session_id = @sessionId
                  AND scope_run_id = @scopeRunId
                  AND recipient_agent_id = @recipientAgentId
                  AND read_at IS NULL
                  AND (@senderAgentId IS NULL OR sender_agent_id = @senderAgentId)
                  AND (@correlationId IS NULL OR correlation_id = @correlationId)
                ORDER BY id
                LIMIT @maxMessages
                """;
            selectCommand.Parameters.AddWithValue("@sessionId", request.SessionKey.Value);
            selectCommand.Parameters.AddWithValue("@scopeRunId", request.ScopeRunId.Value);
            selectCommand.Parameters.AddWithValue("@recipientAgentId", recipientAgentId);
            selectCommand.Parameters.AddWithValue("@senderAgentId", (object?)senderFilter ?? DBNull.Value);
            selectCommand.Parameters.AddWithValue("@correlationId", (object?)correlationFilter ?? DBNull.Value);
            selectCommand.Parameters.AddWithValue("@maxMessages", max);

            await using var reader = await selectCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                entries.Add(new InboxEntry(
                    RowId: reader.GetInt64(0),
                    Message: new AgentMessage(
                        MessageId: reader.GetString(1),
                        SessionKey: new SessionKey(reader.GetString(2)),
                        ScopeRunId: new RunId(reader.GetString(3)),
                        SenderAgentId: reader.GetString(4),
                        RecipientAgentId: reader.GetString(5),
                        Content: reader.GetString(6),
                        SentAt: DateTimeOffset.Parse(reader.GetString(8)),
                        CorrelationId: reader.IsDBNull(7) ? null : reader.GetString(7))));
            }
        }

        if (request.MarkAsRead && entries.Count > 0)
        {
            var updateSql = new System.Text.StringBuilder();
            updateSql.Append("UPDATE agent_messages SET read_at = @readAt WHERE id IN (");
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                {
                    updateSql.Append(", ");
                }

                updateSql.Append($"@id{i}");
            }

            updateSql.Append(')');

            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = sqlTransaction;
            updateCommand.CommandText = updateSql.ToString();
            updateCommand.Parameters.AddWithValue("@readAt", DateTimeOffset.UtcNow.ToString("O"));

            for (var i = 0; i < entries.Count; i++)
            {
                updateCommand.Parameters.AddWithValue($"@id{i}", entries[i].RowId);
            }

            await updateCommand.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return entries.Select(entry => entry.Message).ToArray();
    }

    private static string RequireAgentId(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Agent id cannot be empty.", paramName);
        }

        return value.Trim();
    }

    private static string? NormalizeNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task CleanupExpiredMessagesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        if (_messageTtlDays <= 0)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_messageTtlDays).ToString("O");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM agent_messages WHERE sent_at < @cutoff";
        command.Parameters.AddWithValue("@cutoff", cutoff);
        await command.ExecuteNonQueryAsync(ct);
    }

    private readonly record struct InboxEntry(long RowId, AgentMessage Message);
}
