using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using Microsoft.Data.Sqlite;

namespace IronPaw.AgentCore.Persistence.Sqlite;

public sealed class SqliteRunHistoryStore : IRunHistoryStore
{
    private readonly string _connectionString;

    public SqliteRunHistoryStore(SqliteStoreOptions options)
    {
        _connectionString = SqliteSchemaInitializer.BuildConnectionString(options.DatabasePath);
    }

    public async Task RecordAsync(RunSummary summary, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO run_history (run_id, session_id, started_at, completed_at,
                user_prompt, final_assistant_message, rounds, total_tokens, tools_used_json)
            VALUES (@runId, @sessionId, @startedAt, @completedAt,
                @userPrompt, @finalMessage, @rounds, @totalTokens, @toolsUsedJson)
            """;

        command.Parameters.AddWithValue("@runId", summary.RunId);
        command.Parameters.AddWithValue("@sessionId", summary.SessionId);
        command.Parameters.AddWithValue("@startedAt", summary.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("@completedAt", summary.CompletedAt.ToString("O"));
        command.Parameters.AddWithValue("@userPrompt", summary.UserPrompt);
        command.Parameters.AddWithValue("@finalMessage", summary.FinalAssistantMessage);
        command.Parameters.AddWithValue("@rounds", summary.Rounds);
        command.Parameters.AddWithValue("@totalTokens", summary.TotalTokens);
        command.Parameters.AddWithValue("@toolsUsedJson", JsonSerializer.Serialize(summary.ToolsUsed));

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RunSummary>> LoadAllAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, session_id, started_at, completed_at,
                   user_prompt, final_assistant_message, rounds, total_tokens, tools_used_json
            FROM run_history
            ORDER BY started_at
            """;

        return await ReadRunSummariesAsync(command, ct);
    }

    public async Task<IReadOnlyList<RunSummary>> LoadBySessionAsync(SessionKey sessionKey, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, session_id, started_at, completed_at,
                   user_prompt, final_assistant_message, rounds, total_tokens, tools_used_json
            FROM run_history
            WHERE session_id = @sessionId
            ORDER BY started_at
            """;
        command.Parameters.AddWithValue("@sessionId", sessionKey.Value);

        return await ReadRunSummariesAsync(command, ct);
    }

    public async Task<RunSummary?> GetAsync(string runId, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, session_id, started_at, completed_at,
                   user_prompt, final_assistant_message, rounds, total_tokens, tools_used_json
            FROM run_history
            WHERE run_id = @runId
            """;
        command.Parameters.AddWithValue("@runId", runId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return ReadRunSummary(reader);
    }

    private static async Task<IReadOnlyList<RunSummary>> ReadRunSummariesAsync(SqliteCommand command, CancellationToken ct)
    {
        var results = new List<RunSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadRunSummary(reader));
        }

        return results;
    }

    private static RunSummary ReadRunSummary(SqliteDataReader reader)
    {
        var toolsUsedJson = reader.GetString(8);
        var toolsUsed = JsonSerializer.Deserialize<List<string>>(toolsUsedJson) ?? [];

        return new RunSummary(
            RunId: reader.GetString(0),
            SessionId: reader.GetString(1),
            StartedAt: DateTimeOffset.Parse(reader.GetString(2)),
            CompletedAt: DateTimeOffset.Parse(reader.GetString(3)),
            UserPrompt: reader.GetString(4),
            FinalAssistantMessage: reader.GetString(5),
            Rounds: reader.GetInt32(6),
            TotalTokens: reader.GetInt64(7),
            ToolsUsed: toolsUsed);
    }
}
