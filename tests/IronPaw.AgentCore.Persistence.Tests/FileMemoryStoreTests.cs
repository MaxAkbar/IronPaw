using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;

namespace IronPaw.AgentCore.Persistence.Tests;

public sealed class FileMemoryStoreTests
{
    [Fact]
    public async Task AddAndLoadGlobal_RoundTrips()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root);
            var entry = new MemoryEntry(
                Id: "mem1",
                Content: "User prefers tabs",
                Scope: MemoryScope.Global,
                CreatedAt: DateTimeOffset.UtcNow,
                Source: "user");

            await store.AddAsync(entry, CancellationToken.None);

            var loaded = await store.LoadGlobalAsync(CancellationToken.None);
            Assert.Single(loaded);
            Assert.Equal("User prefers tabs", loaded[0].Content);
            Assert.Equal(MemoryScope.Global, loaded[0].Scope);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddAndLoadSession_RoundTrips()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root);
            var sessionKey = new SessionKey("test-session");
            var entry = new MemoryEntry(
                Id: "mem2",
                Content: "Working on auth refactor",
                Scope: MemoryScope.Session,
                CreatedAt: DateTimeOffset.UtcNow,
                SessionId: sessionKey.Value,
                Source: "agent");

            await store.AddAsync(entry, CancellationToken.None);

            var sessionMemories = await store.LoadSessionAsync(sessionKey, CancellationToken.None);
            Assert.Single(sessionMemories);
            Assert.Equal("Working on auth refactor", sessionMemories[0].Content);

            var globalMemories = await store.LoadGlobalAsync(CancellationToken.None);
            Assert.Empty(globalMemories);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Remove_DeletesEntry()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root);
            var entry = new MemoryEntry(
                Id: "to-remove",
                Content: "Temporary fact",
                Scope: MemoryScope.Global,
                CreatedAt: DateTimeOffset.UtcNow);

            await store.AddAsync(entry, CancellationToken.None);
            Assert.Single(await store.LoadGlobalAsync(CancellationToken.None));

            var removed = await store.RemoveAsync("to-remove", CancellationToken.None);
            Assert.True(removed);
            Assert.Empty(await store.LoadGlobalAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Remove_ReturnsFalseForMissingId()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root);
            var removed = await store.RemoveAsync("nonexistent", CancellationToken.None);
            Assert.False(removed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Search_FindsBySubstring()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root);
            await store.AddAsync(new MemoryEntry("m1", "User prefers TypeScript", MemoryScope.Global, DateTimeOffset.UtcNow), CancellationToken.None);
            await store.AddAsync(new MemoryEntry("m2", "Project uses PostgreSQL", MemoryScope.Global, DateTimeOffset.UtcNow), CancellationToken.None);
            await store.AddAsync(new MemoryEntry("m3", "User likes dark mode", MemoryScope.Global, DateTimeOffset.UtcNow), CancellationToken.None);

            var results = await store.SearchAsync("user", null, null, CancellationToken.None);
            Assert.Equal(2, results.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Add_TruncatesLongContent()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, maxContentChars: 10);
            var entry = new MemoryEntry("long", "This is a very long memory entry that should be truncated", MemoryScope.Global, DateTimeOffset.UtcNow);
            var saved = await store.AddAsync(entry, CancellationToken.None);

            Assert.Equal(10, saved.Content.Length);
            Assert.Equal("This is a ", saved.Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Add_EvictsOldestWhenAtCapacity()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, maxGlobal: 2);
            await store.AddAsync(new MemoryEntry("first", "First entry", MemoryScope.Global, DateTimeOffset.UtcNow), CancellationToken.None);
            await store.AddAsync(new MemoryEntry("second", "Second entry", MemoryScope.Global, DateTimeOffset.UtcNow), CancellationToken.None);
            await store.AddAsync(new MemoryEntry("third", "Third entry", MemoryScope.Global, DateTimeOffset.UtcNow), CancellationToken.None);

            var loaded = await store.LoadGlobalAsync(CancellationToken.None);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("Second entry", loaded[0].Content);
            Assert.Equal("Third entry", loaded[1].Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadGlobal_ReturnsEmptyForNewStore()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root);
            var loaded = await store.LoadGlobalAsync(CancellationToken.None);
            Assert.Empty(loaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("a:b", "ab")]
    [InlineData("Default", "default")]
    public async Task SessionMemories_DistinctKeysRemainIsolated(string first, string second)
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root);
            await store.AddAsync(new MemoryEntry("first", "first memory", MemoryScope.Session,
                DateTimeOffset.UtcNow, SessionId: first), CancellationToken.None);
            await store.AddAsync(new MemoryEntry("second", "second memory", MemoryScope.Session,
                DateTimeOffset.UtcNow, SessionId: second), CancellationToken.None);
            Assert.Equal("first", Assert.Single(await store.LoadSessionAsync(new SessionKey(first), CancellationToken.None)).Id);
            Assert.Equal("second", Assert.Single(await store.LoadSessionAsync(new SessionKey(second), CancellationToken.None)).Id);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static FileMemoryStore CreateStore(string root, int maxGlobal = 100, int maxSession = 50, int maxContentChars = 500)
    {
        return new FileMemoryStore(new FileMemoryStoreOptions(
            GlobalMemoryPath: Path.Combine(root, "memories.json"),
            SessionMemoriesDirectory: Path.Combine(root, "sessions"),
            MaxGlobalMemories: maxGlobal,
            MaxSessionMemories: maxSession,
            MaxMemoryContentChars: maxContentChars));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
