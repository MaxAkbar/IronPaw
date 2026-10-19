using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.AgentCore.Persistence.CSharpDb.Tests;

public sealed class ApplicationStoreParityTests
{
    // --- Session Store ---

    [Fact]
    public async Task CSharpDbApplicationStore_UpsertAndGet_RoundTripsData()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var store = new CSharpDbApplicationStore(options);
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
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task CSharpDbApplicationStore_UpsertMutatesExisting()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var store = new CSharpDbApplicationStore(options);
            var key = new SessionKey("session-mutate");

            await store.UpsertAsync(
                key,
                _ => new SessionEntry(SessionId: key.Value, UpdatedAt: DateTimeOffset.UtcNow, InputTokens: 100),
                CancellationToken.None);

            await store.UpsertAsync(
                key,
                existing => existing! with { InputTokens = existing.InputTokens + 50 },
                CancellationToken.None);

            var entry = await store.GetAsync(key, CancellationToken.None);
            Assert.NotNull(entry);
            Assert.Equal(150, entry!.InputTokens);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task CSharpDbApplicationStore_LoadAll_ReturnsAllSessions()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var store = new CSharpDbApplicationStore(options);

            await store.UpsertAsync(new SessionKey("a"), _ => new SessionEntry("a", DateTimeOffset.UtcNow), CancellationToken.None);
            await store.UpsertAsync(new SessionKey("b"), _ => new SessionEntry("b", DateTimeOffset.UtcNow), CancellationToken.None);

            var all = await store.LoadAllAsync(CancellationToken.None);
            Assert.Equal(2, all.Count);
            Assert.Contains(new SessionKey("a"), all.Keys);
            Assert.Contains(new SessionKey("b"), all.Keys);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task CSharpDbApplicationStore_GetNonExistent_ReturnsNull()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var store = new CSharpDbApplicationStore(options);

            var entry = await store.GetAsync(new SessionKey("nope"), CancellationToken.None);
            Assert.Null(entry);
        }
        finally
        {
            cleanup();
        }
    }

    // --- Transcript Store ---

    [Fact]
    public async Task CSharpDbApplicationStore_AppendLoadAndReplace_Works()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var store = new CSharpDbApplicationStore(options);
            var key = new SessionKey("transcript-a");

            await store.AppendAsync(key, [
                new ChatMessage(ChatRole.User, "hello"),
                new ChatMessage(ChatRole.Assistant, "world")
            ], CancellationToken.None);

            var loaded = await store.LoadAsync(key, CancellationToken.None);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(ChatRole.User, loaded[0].Role);
            Assert.Equal("hello", loaded[0].Content);

            await store.ReplaceAsync(key, [new ChatMessage(ChatRole.Assistant, "replaced")], CancellationToken.None);

            var replaced = await store.LoadAsync(key, CancellationToken.None);
            Assert.Single(replaced);
            Assert.Equal("replaced", replaced[0].Content);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task CSharpDbApplicationStore_AppendPreservesOrder()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var store = new CSharpDbApplicationStore(options);
            var key = new SessionKey("order-test");

            await store.AppendAsync(key, [new ChatMessage(ChatRole.User, "first")], CancellationToken.None);
            await store.AppendAsync(key, [new ChatMessage(ChatRole.Assistant, "second")], CancellationToken.None);
            await store.AppendAsync(key, [new ChatMessage(ChatRole.User, "third")], CancellationToken.None);

            var loaded = await store.LoadAsync(key, CancellationToken.None);
            Assert.Equal(3, loaded.Count);
            Assert.Equal("first", loaded[0].Content);
            Assert.Equal("second", loaded[1].Content);
            Assert.Equal("third", loaded[2].Content);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task CSharpDbApplicationStore_PreservesToolCallFields()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var store = new CSharpDbApplicationStore(options);
            var key = new SessionKey("tool-fields");

            await store.AppendAsync(key, [
                new ChatMessage(ChatRole.Tool, "result data", "call-123", "read_file")
            ], CancellationToken.None);

            var loaded = await store.LoadAsync(key, CancellationToken.None);
            Assert.Single(loaded);
            Assert.Equal(ChatRole.Tool, loaded[0].Role);
            Assert.Equal("call-123", loaded[0].ToolCallId);
            Assert.Equal("read_file", loaded[0].ToolName);
        }
        finally
        {
            cleanup();
        }
    }

    // --- Memory Store ---

    [Fact]
    public async Task CSharpDbApplicationStore_AddAndLoadGlobal_Works()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var store = new CSharpDbApplicationStore(options);

            var entry = new MemoryEntry(
                Id: Guid.NewGuid().ToString("n"),
                Content: "remember this",
                Scope: MemoryScope.Global,
                CreatedAt: DateTimeOffset.UtcNow,
                Source: "test");

            await store.AddAsync(entry, CancellationToken.None);

            var globalMemories = await store.LoadGlobalAsync(CancellationToken.None);
            Assert.Single(globalMemories);
            Assert.Equal("remember this", globalMemories[0].Content);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task CSharpDbApplicationStore_AddAndLoadSession_Works()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var store = new CSharpDbApplicationStore(options);
            var sessionKey = new SessionKey("session-mem");

            var entry = new MemoryEntry(
                Id: Guid.NewGuid().ToString("n"),
                Content: "session memory",
                Scope: MemoryScope.Session,
                CreatedAt: DateTimeOffset.UtcNow,
                SessionId: sessionKey.Value,
                Source: "test");

            await store.AddAsync(entry, CancellationToken.None);

            var sessionMemories = await store.LoadSessionAsync(sessionKey, CancellationToken.None);
            Assert.Single(sessionMemories);
            Assert.Equal("session memory", sessionMemories[0].Content);

            // Should not appear in global
            var globalMemories = await store.LoadGlobalAsync(CancellationToken.None);
            Assert.Empty(globalMemories);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task CSharpDbApplicationStore_Remove_Works()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var store = new CSharpDbApplicationStore(options);
            var id = Guid.NewGuid().ToString("n");

            await store.AddAsync(new MemoryEntry(id, "to forget", MemoryScope.Global, DateTimeOffset.UtcNow), CancellationToken.None);

            var removed = await store.RemoveAsync(id, CancellationToken.None);
            Assert.True(removed);

            var globalMemories = await store.LoadGlobalAsync(CancellationToken.None);
            Assert.Empty(globalMemories);

            var removedAgain = await store.RemoveAsync(id, CancellationToken.None);
            Assert.False(removedAgain);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task CSharpDbApplicationStore_EnforcesCapacityLimit()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath, MaxGlobalMemories: 3);
            var store = new CSharpDbApplicationStore(options);

            for (var i = 0; i < 5; i++)
            {
                await store.AddAsync(
                    new MemoryEntry(
                        Id: Guid.NewGuid().ToString("n"),
                        Content: $"memory-{i}",
                        Scope: MemoryScope.Global,
                        CreatedAt: DateTimeOffset.UtcNow.AddMinutes(i)),
                    CancellationToken.None);
            }

            var memories = await store.LoadGlobalAsync(CancellationToken.None);
            Assert.Equal(3, memories.Count);
            // Oldest should have been evicted
            Assert.Equal("memory-2", memories[0].Content);
            Assert.Equal("memory-3", memories[1].Content);
            Assert.Equal("memory-4", memories[2].Content);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task CSharpDbApplicationStore_Search_FindsMatches()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var store = new CSharpDbApplicationStore(options);

            await store.AddAsync(new MemoryEntry("1", "The quick brown fox", MemoryScope.Global, DateTimeOffset.UtcNow), CancellationToken.None);
            await store.AddAsync(new MemoryEntry("2", "Lazy dog sleeps", MemoryScope.Global, DateTimeOffset.UtcNow), CancellationToken.None);
            await store.AddAsync(new MemoryEntry("3", "Fox jumps high", MemoryScope.Global, DateTimeOffset.UtcNow), CancellationToken.None);

            var results = await store.SearchAsync("fox", null, null, CancellationToken.None);
            Assert.Equal(2, results.Count);
        }
        finally
        {
            cleanup();
        }
    }

    // --- Run History Store ---

    [Fact]
    public async Task ApplicationHistory_RecordAndLoad_Works()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var store = (IRunHistoryStore)new CSharpDbApplicationStore(options);

            var summary = new RunSummary(
                RunId: "run-1",
                SessionId: "default",
                StartedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
                CompletedAt: DateTimeOffset.UtcNow,
                UserPrompt: "hello",
                FinalAssistantMessage: "world",
                Rounds: 3,
                TotalTokens: 1500,
                ToolsUsed: ["read_file", "write_file"]);

            await store.RecordAsync(summary, CancellationToken.None);

            var all = await store.LoadAllAsync(CancellationToken.None);
            Assert.Single(all);
            Assert.Equal("run-1", all[0].RunId);
            Assert.Equal("default", all[0].SessionId);
            Assert.Equal(3, all[0].Rounds);
            Assert.Equal(1500, all[0].TotalTokens);
            Assert.Equal(2, all[0].ToolsUsed.Count);
            Assert.Contains("read_file", all[0].ToolsUsed);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task ApplicationHistory_GetById_Works()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var store = (IRunHistoryStore)new CSharpDbApplicationStore(options);

            await store.RecordAsync(new RunSummary("run-a", "s1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "p1", "r1", 1, 100, []), CancellationToken.None);
            await store.RecordAsync(new RunSummary("run-b", "s1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "p2", "r2", 2, 200, []), CancellationToken.None);

            var found = await store.GetAsync("run-b", CancellationToken.None);
            Assert.NotNull(found);
            Assert.Equal("p2", found!.UserPrompt);

            var notFound = await store.GetAsync("run-missing", CancellationToken.None);
            Assert.Null(notFound);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task ApplicationHistory_LoadBySession_Filters()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var store = (IRunHistoryStore)new CSharpDbApplicationStore(options);

            await store.RecordAsync(new RunSummary("r1", "s1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "p1", "r1", 1, 100, []), CancellationToken.None);
            await store.RecordAsync(new RunSummary("r2", "s2", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "p2", "r2", 1, 100, []), CancellationToken.None);
            await store.RecordAsync(new RunSummary("r3", "s1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "p3", "r3", 1, 100, []), CancellationToken.None);

            var s1Runs = await store.LoadBySessionAsync(new SessionKey("s1"), CancellationToken.None);
            Assert.Equal(2, s1Runs.Count);

            var s2Runs = await store.LoadBySessionAsync(new SessionKey("s2"), CancellationToken.None);
            Assert.Single(s2Runs);
        }
        finally
        {
            cleanup();
        }
    }

    // --- Agent Message Bus ---

    [Fact]
    public async Task ApplicationMessages_SendAndRead_PersistsAcrossInstances()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var registry = new TestAgentRegistry(
            [
                CreateAgent("codegen", ["reviewer", "codegen"]),
                CreateAgent("reviewer", ["codegen", "reviewer"])
            ]);

            var senderBus = new CSharpDbAgentMessageBus(new CSharpDbApplicationStore(options), registry);
            var session = new SessionKey("s-msg");
            var runId = new RunId("r-msg");

            var sent = await senderBus.SendAsync(
                new AgentMessageSendRequest(
                    SessionKey: session,
                    ScopeRunId: runId,
                    SenderAgentId: "codegen",
                    RecipientAgentId: "reviewer",
                    Content: "Please review the latest patch.",
                    CorrelationId: "corr-123"),
                CancellationToken.None);

            // Recreate bus instance to verify persistence-backed durability.
            var receiverBus = new CSharpDbAgentMessageBus(new CSharpDbApplicationStore(options), registry);
            var inbox = await receiverBus.ReadAsync(
                new AgentMessageReadRequest(
                    SessionKey: session,
                    ScopeRunId: runId,
                    RecipientAgentId: "reviewer"),
                CancellationToken.None);

            Assert.Single(inbox);
            Assert.Equal(sent.MessageId, inbox[0].MessageId);
            Assert.Equal("codegen", inbox[0].SenderAgentId);
            Assert.Equal("reviewer", inbox[0].RecipientAgentId);
            Assert.Equal("Please review the latest patch.", inbox[0].Content);
            Assert.Equal("corr-123", inbox[0].CorrelationId);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task ApplicationMessages_UnauthorizedSender_Throws()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var registry = new TestAgentRegistry(
            [
                CreateAgent("reviewer", ["codegen"])
            ]);
            var bus = new CSharpDbAgentMessageBus(new CSharpDbApplicationStore(options), registry);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                bus.SendAsync(
                    new AgentMessageSendRequest(
                        SessionKey: new SessionKey("s-msg"),
                        ScopeRunId: new RunId("r-msg"),
                        SenderAgentId: "intruder",
                        RecipientAgentId: "reviewer",
                        Content: "malicious ping"),
                    CancellationToken.None));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task ApplicationMessages_ReadWithoutMarkAsRead_DoesNotConsume()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var registry = new TestAgentRegistry(
            [
                CreateAgent("codegen", ["reviewer", "codegen"]),
                CreateAgent("reviewer", ["codegen", "reviewer"])
            ]);

            var bus = new CSharpDbAgentMessageBus(new CSharpDbApplicationStore(options), registry);
            var session = new SessionKey("s-msg");
            var runId = new RunId("r-msg");

            await bus.SendAsync(
                new AgentMessageSendRequest(
                    SessionKey: session,
                    ScopeRunId: runId,
                    SenderAgentId: "codegen",
                    RecipientAgentId: "reviewer",
                    Content: "message one"),
                CancellationToken.None);

            var peek = await bus.ReadAsync(
                new AgentMessageReadRequest(
                    SessionKey: session,
                    ScopeRunId: runId,
                    RecipientAgentId: "reviewer",
                    MaxMessages: 10,
                    MarkAsRead: false),
                CancellationToken.None);
            Assert.Single(peek);

            var consume = await bus.ReadAsync(
                new AgentMessageReadRequest(
                    SessionKey: session,
                    ScopeRunId: runId,
                    RecipientAgentId: "reviewer"),
                CancellationToken.None);
            Assert.Single(consume);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task ApplicationMessages_ReadWithSenderAndCorrelationFilters_Works()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            var options = new CSharpDbApplicationStoreOptions(dbPath);
            var registry = new TestAgentRegistry(
            [
                CreateAgent("codegen", ["reviewer", "codegen"]),
                CreateAgent("reviewer", ["codegen", "reviewer", "analyst"]),
                CreateAgent("analyst", ["reviewer", "analyst"])
            ]);

            var bus = new CSharpDbAgentMessageBus(new CSharpDbApplicationStore(options), registry);
            var session = new SessionKey("s-msg");
            var runId = new RunId("r-msg");

            await bus.SendAsync(
                new AgentMessageSendRequest(
                    SessionKey: session,
                    ScopeRunId: runId,
                    SenderAgentId: "codegen",
                    RecipientAgentId: "reviewer",
                    Content: "codegen/corr-1",
                    CorrelationId: "corr-1"),
                CancellationToken.None);
            await bus.SendAsync(
                new AgentMessageSendRequest(
                    SessionKey: session,
                    ScopeRunId: runId,
                    SenderAgentId: "analyst",
                    RecipientAgentId: "reviewer",
                    Content: "analyst/corr-1",
                    CorrelationId: "corr-1"),
                CancellationToken.None);
            await bus.SendAsync(
                new AgentMessageSendRequest(
                    SessionKey: session,
                    ScopeRunId: runId,
                    SenderAgentId: "codegen",
                    RecipientAgentId: "reviewer",
                    Content: "codegen/corr-2",
                    CorrelationId: "corr-2"),
                CancellationToken.None);

            var filtered = await bus.ReadAsync(
                new AgentMessageReadRequest(
                    SessionKey: session,
                    ScopeRunId: runId,
                    RecipientAgentId: "reviewer",
                    MarkAsRead: false,
                    SenderAgentId: "codegen",
                    CorrelationId: "corr-1"),
                CancellationToken.None);

            Assert.Single(filtered);
            Assert.Equal("codegen", filtered[0].SenderAgentId);
            Assert.Equal("corr-1", filtered[0].CorrelationId);
            Assert.Equal("codegen/corr-1", filtered[0].Content);
        }
        finally
        {
            cleanup();
        }
    }

    // --- Helpers ---

    private static (string dbPath, Action cleanup) CreateTempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "application.cdb");
        return (dbPath, () =>
        {
            Directory.Delete(dir, recursive: true);
        });
    }

    private static AgentDefinition CreateAgent(string id, IReadOnlyCollection<string> allowedSenders)
    {
        return new AgentDefinition(
            AgentId: id,
            DisplayName: id,
            Description: $"Agent {id}",
            SystemPrompt: $"You are {id}.",
            ToolPolicy: new ToolSelectionPolicy(ToolProfile.Coding),
            AllowedMessageSenders: allowedSenders);
    }

    private sealed class TestAgentRegistry : IAgentRegistry
    {
        private readonly Dictionary<string, AgentDefinition> _agents;

        public TestAgentRegistry(IEnumerable<AgentDefinition> definitions)
        {
            _agents = definitions.ToDictionary(definition => definition.AgentId, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<AgentDefinition> GetAll() => _agents.Values.ToList();

        public AgentDefinition? Get(string agentId) => _agents.GetValueOrDefault(agentId);

        public void Register(AgentDefinition definition) => _agents[definition.AgentId] = definition;
    }
}
