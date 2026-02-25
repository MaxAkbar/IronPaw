using Microsoft.Data.Sqlite;

namespace IronPaw.AgentCore.Persistence.Sqlite;

public static class SqliteSchemaInitializer
{
    public static void Initialize(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = Schema;
        command.ExecuteNonQuery();
    }

    public static string BuildConnectionString(string databasePath)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    private const string Schema = """
        PRAGMA journal_mode=WAL;

        CREATE TABLE IF NOT EXISTS sessions (
            session_id         TEXT PRIMARY KEY,
            updated_at         TEXT NOT NULL,
            transcript_path    TEXT,
            provider_override  TEXT,
            model_override     TEXT,
            compaction_count   INTEGER NOT NULL DEFAULT 0,
            input_tokens       INTEGER NOT NULL DEFAULT 0,
            output_tokens      INTEGER NOT NULL DEFAULT 0,
            total_tokens       INTEGER NOT NULL DEFAULT 0,
            total_tokens_fresh INTEGER NOT NULL DEFAULT 1,
            diagnostics_json   TEXT
        );

        CREATE TABLE IF NOT EXISTS transcript_messages (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id   TEXT    NOT NULL,
            seq          INTEGER NOT NULL,
            role         TEXT    NOT NULL,
            content      TEXT    NOT NULL,
            tool_call_id TEXT,
            tool_name    TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_transcript_session
            ON transcript_messages(session_id, seq);

        CREATE TABLE IF NOT EXISTS memories (
            id         TEXT PRIMARY KEY,
            content    TEXT NOT NULL,
            scope      TEXT NOT NULL,
            created_at TEXT NOT NULL,
            session_id TEXT,
            category   TEXT,
            source     TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_memories_scope
            ON memories(scope);
        CREATE INDEX IF NOT EXISTS idx_memories_session
            ON memories(session_id);

        CREATE TABLE IF NOT EXISTS run_history (
            run_id                  TEXT PRIMARY KEY,
            session_id              TEXT NOT NULL,
            started_at              TEXT NOT NULL,
            completed_at            TEXT NOT NULL,
            user_prompt             TEXT NOT NULL,
            final_assistant_message TEXT NOT NULL,
            rounds                  INTEGER NOT NULL,
            total_tokens            INTEGER NOT NULL,
            tools_used_json         TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_run_history_session
            ON run_history(session_id);

        CREATE TABLE IF NOT EXISTS agent_messages (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            message_id         TEXT NOT NULL UNIQUE,
            session_id         TEXT NOT NULL,
            scope_run_id       TEXT NOT NULL,
            sender_agent_id    TEXT NOT NULL,
            recipient_agent_id TEXT NOT NULL,
            content            TEXT NOT NULL,
            correlation_id     TEXT,
            sent_at            TEXT NOT NULL,
            read_at            TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_agent_messages_inbox
            ON agent_messages(session_id, scope_run_id, recipient_agent_id, read_at, id);
        CREATE INDEX IF NOT EXISTS idx_agent_messages_sent_at
            ON agent_messages(sent_at);
        """;
}
