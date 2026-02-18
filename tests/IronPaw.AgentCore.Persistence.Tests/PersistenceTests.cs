using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;

namespace IronPaw.AgentCore.Persistence.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task FileSessionStore_UpsertAndGet_RoundTripsData()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new FileSessionStore(new FileSessionStoreOptions(
                StoreFilePath: Path.Combine(root, "session-store.json"),
                CacheTtl: TimeSpan.FromSeconds(1)));
            var key = new SessionKey("session-a");

            await store.UpsertAsync(
                key,
                _ => new SessionEntry(
                    SessionId: key.Value,
                    UpdatedAt: DateTimeOffset.UtcNow,
                    ProviderOverride: "local",
                    ModelOverride: "rule-based-v1",
                    Diagnostics: new Dictionary<string, object?> { ["phase"] = "initial" }),
                CancellationToken.None);

            var entry = await store.GetAsync(key, CancellationToken.None);
            Assert.NotNull(entry);
            Assert.Equal("local", entry!.ProviderOverride);
            Assert.Equal("rule-based-v1", entry.ModelOverride);
            Assert.Equal("initial", entry.Diagnostics!["phase"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FileSessionStore_ReturnsClonedEntries()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new FileSessionStore(new FileSessionStoreOptions(
                StoreFilePath: Path.Combine(root, "session-store.json"),
                CacheTtl: TimeSpan.FromSeconds(10)));
            var key = new SessionKey("session-clone");

            await store.UpsertAsync(
                key,
                _ => new SessionEntry(
                    SessionId: key.Value,
                    UpdatedAt: DateTimeOffset.UtcNow,
                    Diagnostics: new Dictionary<string, object?> { ["value"] = "original" }),
                CancellationToken.None);

            var first = await store.GetAsync(key, CancellationToken.None);
            var mutable = Assert.IsType<Dictionary<string, object?>>(first!.Diagnostics);
            mutable["value"] = "mutated";

            var second = await store.GetAsync(key, CancellationToken.None);
            Assert.Equal("original", second!.Diagnostics!["value"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task JsonTranscriptStore_AppendLoadAndReplace_Works()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new JsonTranscriptStore(root);
            var key = new SessionKey("transcript-a");

            await store.AppendAsync(
                key,
                [
                    new ChatMessage(ChatRole.User, "hello"),
                    new ChatMessage(ChatRole.Assistant, "world")
                ],
                CancellationToken.None);

            var loaded = await store.LoadAsync(key, CancellationToken.None);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(ChatRole.User, loaded[0].Role);
            Assert.Equal("hello", loaded[0].Content);

            await store.ReplaceAsync(
                key,
                [new ChatMessage(ChatRole.Assistant, "replaced")],
                CancellationToken.None);

            var replaced = await store.LoadAsync(key, CancellationToken.None);
            Assert.Single(replaced);
            Assert.Equal("replaced", replaced[0].Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task JsonTranscriptStore_RejectsSessionKeysWithoutSafeCharacters()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new JsonTranscriptStore(root);
            var key = new SessionKey("***");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.AppendAsync(key, [new ChatMessage(ChatRole.User, "test")], CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task JsonTranscriptStore_NormalizesSessionFileName()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new JsonTranscriptStore(root);
            var key = new SessionKey("abc/../../def");
            await store.AppendAsync(key, [new ChatMessage(ChatRole.User, "x")], CancellationToken.None);

            var expectedPath = Path.Combine(root, "abcdef.jsonl");
            Assert.True(File.Exists(expectedPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
