using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;

namespace IronPaw.AgentCore.Persistence.Tests;

[TestClass]
public sealed class PersistenceTests
{
    [TestMethod]
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
            Assert.IsNotNull(entry);
            Assert.AreEqual("local", entry!.ProviderOverride);
            Assert.AreEqual("rule-based-v1", entry.ModelOverride);
            Assert.AreEqual("initial", entry.Diagnostics!["phase"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
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
            var mutable = Assert.IsExactInstanceOfType<Dictionary<string, object?>>(first!.Diagnostics);
            mutable["value"] = "mutated";

            var second = await store.GetAsync(key, CancellationToken.None);
            Assert.AreEqual("original", second!.Diagnostics!["value"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
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
            Assert.AreEqual(2, loaded.Count);
            Assert.AreEqual(ChatRole.User, loaded[0].Role);
            Assert.AreEqual("hello", loaded[0].Content);

            await store.ReplaceAsync(
                key,
                [new ChatMessage(ChatRole.Assistant, "replaced")],
                CancellationToken.None);

            var replaced = await store.LoadAsync(key, CancellationToken.None);
            Assert.ContainsSingle(replaced);
            Assert.AreEqual("replaced", replaced[0].Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("a:b", "ab")]
    [DataRow("Default", "default")]
    [DataRow("abc/../../def", "abcdef")]
    [DataRow("***", "con")]
    public async Task JsonTranscriptStore_DistinctOpaqueKeysRemainIsolated(string first, string second)
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new JsonTranscriptStore(root);
            await store.AppendAsync(new SessionKey(first), [new ChatMessage(ChatRole.User, "first")], CancellationToken.None);
            await store.ReplaceAsync(new SessionKey(second), [new ChatMessage(ChatRole.User, "second")], CancellationToken.None);
            Assert.AreEqual("first", Assert.ContainsSingle(await store.LoadAsync(new SessionKey(first), CancellationToken.None)).Content);
            Assert.AreEqual("second", Assert.ContainsSingle(await store.LoadAsync(new SessionKey(second), CancellationToken.None)).Content);
            Assert.AreEqual(2, Directory.GetFiles(root, "*.jsonl").Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task JsonTranscriptStore_PreservesConventionalLegacyFile()
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "default.jsonl"),
                "{\"Role\":\"User\",\"Content\":\"legacy\"}\n");
            var store = new JsonTranscriptStore(root);
            Assert.AreEqual("legacy", Assert.ContainsSingle(await store.LoadAsync(new SessionKey("default"), CancellationToken.None)).Content);
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                store.LoadAsync(new SessionKey("de:fault"), CancellationToken.None));
            Assert.Contains("ambiguous legacy", error.Message);
            Assert.AreEqual("legacy", Assert.ContainsSingle(await store.LoadAsync(new SessionKey("default"), CancellationToken.None)).Content);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task FileSessionStore_IndependentCachedInstancesPreserveUpdates()
    {
        var root = CreateTempDirectory();
        try
        {
            var options = new FileSessionStoreOptions(Path.Combine(root, "sessions.json"), TimeSpan.FromMinutes(5));
            var first = new FileSessionStore(options);
            var second = new FileSessionStore(options);
            await first.UpsertAsync(new SessionKey("a"), _ => new SessionEntry("a", DateTimeOffset.UtcNow), CancellationToken.None);
            await second.LoadAllAsync(CancellationToken.None);
            await first.UpsertAsync(new SessionKey("b"), _ => new SessionEntry("b", DateTimeOffset.UtcNow), CancellationToken.None);
            await second.UpsertAsync(new SessionKey("c"), _ => new SessionEntry("c", DateTimeOffset.UtcNow), CancellationToken.None);
            var fresh = new FileSessionStore(options with { CacheTtl = TimeSpan.Zero });
            Assert.AreEqual(3, (await fresh.LoadAllAsync(CancellationToken.None)).Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task FileSessionStore_ConcurrentIndependentWritersPreserveAllUpdates()
    {
        var root = CreateTempDirectory();
        try
        {
            var options = new FileSessionStoreOptions(Path.Combine(root, "sessions.json"), TimeSpan.FromMinutes(5));
            await Task.WhenAll(Enumerable.Range(0, 20).Select(async index =>
            {
                var key = new SessionKey($"session-{index}");
                var store = new FileSessionStore(options);
                await store.UpsertAsync(key, _ => new SessionEntry(key.Value, DateTimeOffset.UtcNow), CancellationToken.None);
            }));
            Assert.AreEqual(20, (await new FileSessionStore(options).LoadAllAsync(CancellationToken.None)).Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
