using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;

namespace IronPaw.AgentCore.Persistence.Tests;

[TestClass]
public sealed class JsonRunHistoryStoreTests
{
    [TestMethod]
    public async Task RecordAndLoadAll_RoundTrips()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new JsonRunHistoryStore(Path.Combine(root, "run-history.jsonl"));
            var summary = CreateSummary("run1", "session1", "Hello world");

            await store.RecordAsync(summary, CancellationToken.None);

            var loaded = await store.LoadAllAsync(CancellationToken.None);
            Assert.ContainsSingle(loaded);
            Assert.AreEqual("run1", loaded[0].RunId);
            Assert.AreEqual("Hello world", loaded[0].UserPrompt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LoadBySession_FiltersCorrectly()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new JsonRunHistoryStore(Path.Combine(root, "run-history.jsonl"));
            await store.RecordAsync(CreateSummary("r1", "session-a", "Prompt A"), CancellationToken.None);
            await store.RecordAsync(CreateSummary("r2", "session-b", "Prompt B"), CancellationToken.None);
            await store.RecordAsync(CreateSummary("r3", "session-a", "Prompt C"), CancellationToken.None);

            var sessionA = await store.LoadBySessionAsync(new SessionKey("session-a"), CancellationToken.None);
            Assert.AreEqual(2, sessionA.Count);

            var sessionB = await store.LoadBySessionAsync(new SessionKey("session-b"), CancellationToken.None);
            Assert.ContainsSingle(sessionB);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetByRunId_ReturnsCorrectEntry()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new JsonRunHistoryStore(Path.Combine(root, "run-history.jsonl"));
            await store.RecordAsync(CreateSummary("run-abc", "s1", "First"), CancellationToken.None);
            await store.RecordAsync(CreateSummary("run-def", "s1", "Second"), CancellationToken.None);

            var result = await store.GetAsync("run-def", CancellationToken.None);
            Assert.IsNotNull(result);
            Assert.AreEqual("Second", result!.UserPrompt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetByRunId_ReturnsNullForMissing()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new JsonRunHistoryStore(Path.Combine(root, "run-history.jsonl"));
            var result = await store.GetAsync("nonexistent", CancellationToken.None);
            Assert.IsNull(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LoadAll_ReturnsEmptyForNewStore()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new JsonRunHistoryStore(Path.Combine(root, "run-history.jsonl"));
            var loaded = await store.LoadAllAsync(CancellationToken.None);
            Assert.IsEmpty(loaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RunSummary CreateSummary(string runId, string sessionId, string prompt)
    {
        return new RunSummary(
            RunId: runId,
            SessionId: sessionId,
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow.AddSeconds(5),
            UserPrompt: prompt,
            FinalAssistantMessage: $"Response to: {prompt}",
            Rounds: 1,
            TotalTokens: 100,
            ToolsUsed: ["current_time"]);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
