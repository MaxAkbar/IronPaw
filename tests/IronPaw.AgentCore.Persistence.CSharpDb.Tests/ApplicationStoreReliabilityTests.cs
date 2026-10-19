using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.AgentCore.Persistence.CSharpDb.Tests;

public sealed class ApplicationStoreReliabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IronPaw-application-tests", Guid.NewGuid().ToString("N"));
    private static readonly CancellationToken Ct = CancellationToken.None;
    private string DatabasePath => Path.Combine(_root, "application.cdb");
    private CSharpDbApplicationStore Store(TimeProvider? clock = null) => new(new(DatabasePath), clock);
    public ApplicationStoreReliabilityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ReopenPreservesAllProjectionsAndReturnedSessionsAreDetached()
    {
        var key = new SessionKey("世界/CaseSensitive");
        var store = Store();
        await store.UpsertAsync(key, _ => new(key.Value, DateTimeOffset.UtcNow,
            Diagnostics: new Dictionary<string, object?> { ["nested"] = new { value = "saved" } }), Ct);
        await store.AppendAsync(key, [new(ChatRole.Tool, "result", "call", "read_file")], Ct);
        await store.AddAsync(new("memory", "saved", MemoryScope.Session, DateTimeOffset.UtcNow, key.Value), Ct);
        await store.RecordAsync(Summary("run", key.Value), Ct);
        var reopened = Store();
        Assert.Equal("saved", ((JsonElement)(await reopened.GetAsync(key, Ct))!.Diagnostics!["nested"]!).GetProperty("value").GetString());
        Assert.Equal("read_file", Assert.Single(await reopened.LoadAsync(key, Ct)).ToolName);
        Assert.Single(await reopened.LoadSessionAsync(key, Ct));
        Assert.NotNull(await reopened.GetAsync("run", Ct));
        var returned = (await reopened.GetAsync(key, Ct))!;
        ((IDictionary<string, object?>)returned.Diagnostics!)["nested"] = "changed";
        Assert.IsType<JsonElement>((await reopened.GetAsync(key, Ct))!.Diagnostics!["nested"]);
        Assert.Null(await reopened.GetAsync(new SessionKey("世界/casesensitive"), Ct));
    }

    [Fact]
    public async Task ConcurrentSessionMutationsAcrossInstancesHaveNoLostUpdates()
    {
        var key = new SessionKey("counter");
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Store().UpsertAsync(key,
            old => (old ?? new(key.Value, DateTimeOffset.UtcNow)) with { TotalTokens = (old?.TotalTokens ?? 0) + 1 }, Ct)));
        Assert.Equal(24, (await Store().GetAsync(key, Ct))!.TotalTokens);
    }

    [Fact]
    public async Task CancellationWhileWaitingForLockDoesNotInvokeMutationOrKeepLock()
    {
        await Store().LoadAllAsync(Ct);
        await using (var held = new FileStream(DatabasePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var invoked = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().UpsertAsync(new("cancelled"), _ =>
            { invoked = true; return new("cancelled", DateTimeOffset.UtcNow); }, cancellation.Token));
            Assert.False(invoked);
        }
        Assert.Empty(await Store().LoadAllAsync(Ct));
    }

    [Fact]
    public async Task LockContentionTimesOutAfterThirtySecondsWithActionableError()
    {
        await Store().LoadAllAsync(Ct);
        await using var held = new FileStream(DatabasePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var elapsed = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => Store().LoadAllAsync(Ct)).WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(30));
        Assert.Contains("30 seconds", error.Message);
        Assert.Contains(DatabasePath + ".lock", error.Message);
    }

    [Fact]
    public async Task FailedAndCancelledTranscriptReplacementRollBackAndMaintainAppendOrder()
    {
        var key = new SessionKey("transcript");
        await Store().AppendAsync(key, [new(ChatRole.User, "original")], Ct);
        IEnumerable<ChatMessage> Failing()
        { yield return new(ChatRole.User, "partial"); throw new InvalidOperationException("producer failure"); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().ReplaceAsync(key, Failing(), Ct));
        using var cancellation = new CancellationTokenSource();
        IEnumerable<ChatMessage> Cancelling()
        { yield return new(ChatRole.User, "partial"); cancellation.Cancel(); yield return new(ChatRole.Assistant, "cancelled"); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().ReplaceAsync(key, Cancelling(), cancellation.Token));
        Assert.Equal("original", Assert.Single(await Store().LoadAsync(key, Ct)).Content);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Store().AppendAsync(key,
            [new(ChatRole.User, $"{i}:first"), new(ChatRole.Assistant, $"{i}:second")], Ct)));
        var all = await Store().LoadAsync(key, Ct);
        for (var i = 1; i < all.Count; i += 2)
            Assert.Equal(all[i].Content.Split(':')[0], all[i + 1].Content.Split(':')[0]);
    }

    [Theory]
    [InlineData(AgentRunStatus.Succeeded)]
    [InlineData(AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.Cancelled)]
    [InlineData(AgentRunStatus.RoundLimitReached)]
    [InlineData(AgentRunStatus.AwaitingInput)]
    [InlineData(AgentRunStatus.BudgetExhausted)]
    public async Task HistoryIsIdempotentAndRetainsStatusAndError(AgentRunStatus status)
    {
        IRunHistoryStore history = Store();
        var summary = Summary("run", "session") with { Status = status, Error = "diagnostic" };
        await history.RecordAsync(summary with { Error = "earlier" }, Ct);
        await history.RecordAsync(summary, Ct);
        Assert.Single(await history.LoadAllAsync(Ct));
        foreach (var value in new[] { await history.GetAsync("RUN", Ct), Assert.Single(await history.LoadBySessionAsync(new("SESSION"), Ct)) })
        { Assert.Equal(status, value!.Status); Assert.Equal("diagnostic", value.Error); }
    }

    [Fact]
    public async Task DefaultMemoryLimitsSearchAndSessionIsolationArePreserved()
    {
        var store = Store();
        for (var i = 0; i < 101; i++) await store.AddAsync(new($"g{i}", new string('x', 600), MemoryScope.Global, DateTimeOffset.UtcNow), Ct);
        for (var i = 0; i < 51; i++) await store.AddAsync(new($"s{i}", "Find me", MemoryScope.Session, DateTimeOffset.UtcNow, "one"), Ct);
        await store.AddAsync(new("other", "Find me", MemoryScope.Session, DateTimeOffset.UtcNow, "two"), Ct);
        var global = await store.LoadGlobalAsync(Ct);
        Assert.Equal(100, global.Count); Assert.Equal("g1", global[0].Id); Assert.All(global, m => Assert.Equal(500, m.Content.Length));
        var session = await store.LoadSessionAsync(new("one"), Ct);
        Assert.Equal(50, session.Count); Assert.Equal("s1", session[0].Id);
        Assert.Empty(await store.SearchAsync("find", null, null, Ct));
        Assert.Equal(50, (await store.SearchAsync("FIND", null, new SessionKey("one"), Ct)).Count);
        Assert.True(await store.RemoveAsync("OTHER", Ct));
        Assert.Empty(await store.LoadSessionAsync(new("two"), Ct));
    }

    [Fact]
    public async Task MessageConsumptionIsAtomicWithScopeFiltersReadStateAndExpiration()
    {
        var clock = new Clock(); var store = Store(clock);
        var registry = new Registry();
        var bus = new CSharpDbAgentMessageBus(store, registry);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bus.SendAsync(new(new("s"), new("r"), "blocked", "recipient", "no"), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.SendAsync(new(new("s"), new("r"), "sender", "missing", "no"), Ct));
        await bus.SendAsync(new(new("s"), new("r"), " sender ", " recipient ", " expiring ", " tag "), Ct);
        clock.Now += TimeSpan.FromDays(31);
        for (var i = 0; i < 12; i++) await bus.SendAsync(new(new("s"), new("r"), "sender", "recipient", $"{i}", "tag"), Ct);
        await bus.SendAsync(new(new("other"), new("r"), "sender", "recipient", "other session"), Ct);
        await bus.SendAsync(new(new("s"), new("other"), "sender", "recipient", "other run"), Ct);
        var request = new AgentMessageReadRequest(new("s"), new("r"), "recipient", MaxMessages: 100, SenderAgentId: "sender", CorrelationId: " tag ");
        Assert.Equal(12, (await bus.ReadAsync(request with { MarkAsRead = false }, Ct)).Count);
        Assert.Empty(await bus.ReadAsync(request with { SenderAgentId = "other" }, Ct));
        var consumed = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => new CSharpDbAgentMessageBus(Store(clock), registry).ReadAsync(request, Ct)));
        Assert.Equal(12, consumed.Sum(x => x.Count));
        Assert.Equal(12, consumed.SelectMany(x => x).Select(x => x.MessageId).Distinct().Count());
        Assert.Empty(await bus.ReadAsync(request, Ct));
        Assert.Equal("other session", Assert.Single(await bus.ReadAsync(request with { SessionKey = new("other"), CorrelationId = null }, Ct)).Content);
        Assert.Equal("other run", Assert.Single(await bus.ReadAsync(request with { ScopeRunId = new("other"), CorrelationId = null }, Ct)).Content);
    }

    [Fact]
    public async Task FullJsonImportPreservesUnicodeValuesOrderingLimitsAndSourceHashesOnce()
    {
        var key = new SessionKey("会話/Case💡");
        var sessions = Path.Combine(_root, "sessions");
        var old = new FileSessionStore(new(Path.Combine(_root, "session-store.json"), TimeSpan.Zero));
        await old.UpsertAsync(key, _ => new(key.Value, DateTimeOffset.UtcNow, "legacy.jsonl", "provider", "model", 3, 11, 12, 23, false,
            new Dictionary<string, object?> { ["evidence"] = "元の値" }), Ct);
        var original = (await old.GetAsync(key, Ct))!;
        await new JsonTranscriptStore(sessions).AppendAsync(key, [new(ChatRole.User, "first"), new(ChatRole.Tool, "second", "call", "tool")], Ct);
        var memory = new FileMemoryStore(new(Path.Combine(_root, "memories.json"), sessions, 200, 100, 1000));
        for (var i = 0; i < 102; i++) await memory.AddAsync(new($"g{i}", new string('文', 700), MemoryScope.Global, DateTimeOffset.UtcNow), Ct);
        for (var i = 0; i < 52; i++) await memory.AddAsync(new($"s{i}", $"session {i}", MemoryScope.Session, DateTimeOffset.UtcNow, key.Value), Ct);
        var run = Summary("run", key.Value) with { Status = AgentRunStatus.Failed, Error = "保存した診断" };
        await new JsonRunHistoryStore(Path.Combine(_root, "run-history.jsonl")).RecordAsync(run, Ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "ironpaw.db"), "preserve original SQLite bytes", Ct);
        var hashes = Hashes();
        var store = Store();
        Assert.Equal(original.UpdatedAt, (await store.GetAsync(key, Ct))!.UpdatedAt);
        Assert.Equal(original.TranscriptPath, (await store.GetAsync(key, Ct))!.TranscriptPath);
        Assert.Equal(new[] { "first", "second" }, (await store.LoadAsync(key, Ct)).Select(x => x.Content));
        Assert.Equal(102, (await store.LoadGlobalAsync(Ct)).Count);
        Assert.Equal("g0", (await store.LoadGlobalAsync(Ct))[0].Id);
        Assert.Equal("g101", (await store.LoadGlobalAsync(Ct))[^1].Id);
        Assert.All(await store.LoadGlobalAsync(Ct), m => Assert.Equal(700, m.Content.Length));
        Assert.Equal(52, (await store.LoadSessionAsync(key, Ct)).Count);
        Assert.Equal(run.Error, (await store.GetAsync("run", Ct))!.Error);
        Assert.Equal(hashes, Hashes());
        // The marker prevents later stale JSON edits or corruption from blocking/reimporting data.
        await File.WriteAllTextAsync(Path.Combine(_root, "session-store.json"), "broken after migration", Ct);
        Assert.Single(await Store().LoadAllAsync(Ct));
        Assert.Equal(102, (await Store().LoadGlobalAsync(Ct)).Count);
        await store.AddAsync(new("new", "new", MemoryScope.Global, DateTimeOffset.UtcNow), Ct);
        Assert.Equal(100, (await store.LoadGlobalAsync(Ct)).Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MessageExpirationOnReadAndDisabledRetention(bool expire)
    {
        var clock = new Clock(); var registry = new Registry();
        var store = new CSharpDbApplicationStore(new(DatabasePath, MessageTtlDays: expire ? 30 : 0), clock);
        var bus = new CSharpDbAgentMessageBus(store, registry);
        await bus.SendAsync(new(new("s"), new("r"), "sender", "recipient", "older"), Ct);
        clock.Now += TimeSpan.FromDays(31);
        var messages = await bus.ReadAsync(new(new("s"), new("r"), "recipient"), Ct);
        if (expire) Assert.Empty(messages);
        else Assert.Equal("older", Assert.Single(messages).Content);
    }

    [Fact]
    public async Task LegacyHistoryDefaultsAndEmptyStartupMarkerAreCompatible()
    {
        var json = JsonSerializer.SerializeToElement(Summary("legacy", "s"));
        var fields = json.EnumerateObject().Where(p => p.Name is not "Status" and not "Error").ToDictionary(p => p.Name, p => p.Value);
        await File.WriteAllTextAsync(Path.Combine(_root, "run-history.jsonl"), JsonSerializer.Serialize(fields), Ct);
        var summary = (await Store().GetAsync("legacy", Ct))!;
        Assert.Equal(AgentRunStatus.Succeeded, summary.Status); Assert.Null(summary.Error);
        // Starting with no session file is still a completed import.
        await File.WriteAllTextAsync(Path.Combine(_root, "session-store.json"), "broken", Ct);
        Assert.Empty(await Store().LoadAllAsync(Ct));
    }

    [Theory]
    [InlineData("session-store.json", "{")]
    [InlineData("session-store.json", "{\"a\":{},\"a\":{}}")]
    [InlineData("memories.json", "[{\"Id\":\"m\"}]")]
    [InlineData("run-history.jsonl", "null")]
    [InlineData("sessions/~unknown.jsonl", "{\"Role\":\"User\",\"Content\":\"x\"}")]
    [InlineData("sessions/unknown.json", "[]")]
    [InlineData("sessions/one.memories.json", "[{\"Id\":\"m\",\"Content\":\"x\",\"Scope\":\"Session\",\"CreatedAt\":\"2026-01-01T00:00:00Z\",\"SessionId\":\"two\"}]")]
    public async Task MalformedOrUnidentifiedSourcesFailAtomicallyAndCanBeRepaired(string relative, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "memories.json"), "[]", Ct);
        var path = Path.Combine(_root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Ct);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Store().LoadAllAsync(Ct));
        Assert.Contains("Original files are unchanged", error.Message);
        Assert.Equal(content, await File.ReadAllTextAsync(path, Ct));
        File.Delete(path);
        Assert.Empty(await Store().LoadAllAsync(Ct));
        Assert.Empty(await Store().LoadGlobalAsync(Ct));
    }

    [Theory]
    [InlineData("modify")]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("cancel")]
    public async Task ChangingOrInterruptedImportLeavesNoRowsOrCompletionMarker(string mode)
    {
        var path = Path.Combine(_root, "session-store.json");
        await new FileSessionStore(new(path, TimeSpan.Zero)).UpsertAsync(new("old"), _ => new("old", DateTimeOffset.UtcNow), Ct);
        using var cancellation = new CancellationTokenSource();
        var store = new CSharpDbApplicationStore(new(DatabasePath), null, async _ =>
        {
            switch (mode)
            {
                case "modify": await File.WriteAllTextAsync(path, "{}", Ct); break;
                case "remove": File.Delete(path); break;
                case "add": Directory.CreateDirectory(Path.Combine(_root, "sessions")); await File.WriteAllTextAsync(Path.Combine(_root, "sessions", "added.jsonl"), "", Ct); break;
                case "cancel": cancellation.Cancel(); break;
            }
        });
        if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAllAsync(cancellation.Token));
        else await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAllAsync(Ct));
        await File.WriteAllTextAsync(path, "{}", Ct);
        Assert.Empty(await Store().LoadAllAsync(Ct));
    }

    [Fact]
    public async Task AmbiguousLegacySanitizedFilenameIsRejectedWithoutChangingSources()
    {
        var key = new SessionKey("a/b");
        await new FileSessionStore(new(Path.Combine(_root, "session-store.json"), TimeSpan.Zero)).UpsertAsync(key, _ => new(key.Value, DateTimeOffset.UtcNow), Ct);
        Directory.CreateDirectory(Path.Combine(_root, "sessions"));
        await File.WriteAllTextAsync(Path.Combine(_root, "sessions", "ab.jsonl"), "", Ct);
        var hashes = Hashes();
        Assert.Contains("ambiguous legacy filename", (await Assert.ThrowsAsync<InvalidDataException>(() => Store().LoadAllAsync(Ct))).Message);
        Assert.Equal(hashes, Hashes());
    }

    [Fact]
    public async Task SeparateProcessesShareAtomicSessionUpdates()
    {
        using var first = Child("application-writer", DatabasePath, "8");
        using var second = Child("application-writer", DatabasePath, "8");
        await Task.WhenAll(first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)), second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(0, first.ExitCode); Assert.Equal(0, second.ExitCode);
        Assert.Equal(16, (await Store().GetAsync(new SessionKey("process-counter"), Ct))!.TotalTokens);
    }

    [Theory]
    [InlineData("application-held-mutation")]
    [InlineData("application-held-import")]
    public async Task ProcessTerminationReleasesLockAndRollsBackUncommittedWrites(string mode)
    {
        if (mode == "application-held-import")
            await new FileSessionStore(new(Path.Combine(_root, "session-store.json"), TimeSpan.Zero)).UpsertAsync(new("imported"), _ => new("imported", DateTimeOffset.UtcNow), Ct);
        else await Store().UpsertAsync(new("stable"), _ => new("stable", DateTimeOffset.UtcNow), Ct);
        using var process = Child(mode, DatabasePath);
        try
        {
            Assert.Equal("application-held", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await process.StandardOutput.ReadToEndAsync(); await process.StandardError.ReadToEndAsync();
            var sessions = await Store().LoadAllAsync(Ct);
            Assert.Single(sessions);
            Assert.Contains(new SessionKey(mode == "application-held-import" ? "imported" : "stable"), sessions.Keys);
            Assert.Null(await Store().GetAsync(new SessionKey("uncommitted"), Ct));
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    internal static Process Child(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(ApplicationStoreReliabilityTests).Assembly.Location);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }
    private Dictionary<string, string> Hashes() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .Where(p => p.EndsWith(".json") || p.EndsWith(".jsonl") || p.EndsWith(".db"))
        .Order(StringComparer.Ordinal).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    private static RunSummary Summary(string id, string session) => new(id, session, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "prompt", "response", 1, 12, ["read_file"]);
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Registry : IAgentRegistry
    {
        private readonly AgentDefinition _agent = new("recipient", "Recipient", "Recipient", "prompt", new(ToolProfile.Messaging), AllowedMessageSenders: ["sender"]);
        public AgentDefinition? Get(string id) => id == _agent.AgentId ? _agent : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [_agent];
        public void Register(AgentDefinition definition) => throw new NotSupportedException();
    }
    public void Dispose() => Directory.Delete(_root, true);
}
