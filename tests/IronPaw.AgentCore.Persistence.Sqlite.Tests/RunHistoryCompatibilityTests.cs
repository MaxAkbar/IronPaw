using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace IronPaw.AgentCore.Persistence.Sqlite.Tests;

public sealed class RunHistoryCompatibilityTests
{
    [Theory]
    [InlineData(AgentRunStatus.Succeeded)]
    [InlineData(AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.Cancelled)]
    [InlineData(AgentRunStatus.RoundLimitReached)]
    [InlineData(AgentRunStatus.AwaitingInput)]
    public async Task StatusAndError_RoundTripThroughAllQueries(AgentRunStatus status)
    {
        using var database = new TestDatabase();
        SqliteSchemaInitializer.Initialize(database.ConnectionString);
        var store = new SqliteRunHistoryStore(new SqliteStoreOptions(database.Path));
        var summary = new RunSummary("run", "session", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "prompt", "response", 1, 10, ["read_file"], status, "diagnostic");
        await store.RecordAsync(summary, CancellationToken.None);
        foreach (var loaded in new[]
        {
            (await store.GetAsync("run", CancellationToken.None))!,
            Assert.Single(await store.LoadAllAsync(CancellationToken.None)),
            Assert.Single(await store.LoadBySessionAsync(new SessionKey("session"), CancellationToken.None))
        })
        {
            Assert.Equal(status, loaded.Status);
            Assert.Equal("diagnostic", loaded.Error);
        }
    }

    [Fact]
    public async Task Initialize_MigratesLegacyHistoryWithoutLosingRows()
    {
        using var database = new TestDatabase();
        using (var connection = new SqliteConnection(database.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE run_history (
                    run_id TEXT PRIMARY KEY, session_id TEXT NOT NULL, started_at TEXT NOT NULL,
                    completed_at TEXT NOT NULL, user_prompt TEXT NOT NULL, final_assistant_message TEXT NOT NULL,
                    rounds INTEGER NOT NULL, total_tokens INTEGER NOT NULL, tools_used_json TEXT NOT NULL);
                INSERT INTO run_history VALUES ('legacy', 'session', @now, @now, 'prompt', 'response', 1, 10, '[]');
                """;
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        SqliteSchemaInitializer.Initialize(database.ConnectionString);
        SqliteSchemaInitializer.Initialize(database.ConnectionString);
        var store = new SqliteRunHistoryStore(new SqliteStoreOptions(database.Path));
        var summary = (await store.GetAsync("legacy", CancellationToken.None))!;
        Assert.Equal("response", summary.FinalAssistantMessage);
        Assert.Equal(AgentRunStatus.Succeeded, summary.Status);
        Assert.Null(summary.Error);
        Assert.Single(await store.LoadAllAsync(CancellationToken.None));
    }

    [Fact]
    public void BundledNativeSqlite_IsNewerThanAdvisoryAffectedVersions()
    {
        using var database = new TestDatabase();
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var version = (string)command.ExecuteScalar()!;
        Assert.True(Version.Parse(version) >= new Version(3, 50, 2), $"Bundled native SQLite version: {version}");
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        public string Path => System.IO.Path.Combine(_root, "test.db");
        public string ConnectionString => SqliteSchemaInitializer.BuildConnectionString(Path);
        public TestDatabase() => Directory.CreateDirectory(_root);
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }
}
