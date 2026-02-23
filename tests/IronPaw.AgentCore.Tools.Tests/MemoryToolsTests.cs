using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class MemoryToolsTests
{
    [Fact]
    public async Task SaveMemoryTool_SavesGlobalMemory()
    {
        var (store, root) = CreateStore();
        try
        {
            var tool = new SaveMemoryTool(store);
            var context = CreateContext();
            var call = CreateToolCall("save_memory", new { content = "User likes dark mode" });

            var result = await tool.ExecuteAsync(call, context, CancellationToken.None);

            Assert.False(result.IsError);
            var memories = await store.LoadGlobalAsync(CancellationToken.None);
            Assert.Single(memories);
            Assert.Equal("User likes dark mode", memories[0].Content);
            Assert.Equal("agent", memories[0].Source);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveMemoryTool_SavesSessionMemory()
    {
        var (store, root) = CreateStore();
        try
        {
            var tool = new SaveMemoryTool(store);
            var context = CreateContext();
            var call = CreateToolCall("save_memory", new { content = "Working on auth", scope = "session" });

            var result = await tool.ExecuteAsync(call, context, CancellationToken.None);

            Assert.False(result.IsError);
            var sessionMemories = await store.LoadSessionAsync(context.SessionKey, CancellationToken.None);
            Assert.Single(sessionMemories);
            Assert.Equal("Working on auth", sessionMemories[0].Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveMemoryTool_ReturnsErrorForMissingContent()
    {
        var (store, root) = CreateStore();
        try
        {
            var tool = new SaveMemoryTool(store);
            var call = CreateToolCall("save_memory", new { category = "test" });

            var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);
            Assert.True(result.IsError);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecallMemoryTool_FindsMemories()
    {
        var (store, root) = CreateStore();
        try
        {
            await store.AddAsync(new MemoryEntry("m1", "User prefers TypeScript", MemoryScope.Global, DateTimeOffset.UtcNow), CancellationToken.None);
            await store.AddAsync(new MemoryEntry("m2", "Project uses PostgreSQL", MemoryScope.Global, DateTimeOffset.UtcNow), CancellationToken.None);

            var tool = new RecallMemoryTool(store);
            var call = CreateToolCall("recall_memory", new { query = "TypeScript" });

            var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

            Assert.False(result.IsError);
            var parsed = JsonDocument.Parse(result.Content);
            Assert.Single(parsed.RootElement.EnumerateArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ForgetMemoryTool_RemovesMemory()
    {
        var (store, root) = CreateStore();
        try
        {
            await store.AddAsync(new MemoryEntry("forget-me", "Temporary", MemoryScope.Global, DateTimeOffset.UtcNow), CancellationToken.None);

            var tool = new ForgetMemoryTool(store);
            var call = CreateToolCall("forget_memory", new { memory_id = "forget-me" });

            var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Empty(await store.LoadGlobalAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ForgetMemoryTool_ReturnsErrorForMissingId()
    {
        var (store, root) = CreateStore();
        try
        {
            var tool = new ForgetMemoryTool(store);
            var call = CreateToolCall("forget_memory", new { memory_id = "nonexistent" });

            var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);
            Assert.True(result.IsError);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (FileMemoryStore Store, string Root) CreateStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var store = new FileMemoryStore(new FileMemoryStoreOptions(
            GlobalMemoryPath: Path.Combine(root, "memories.json"),
            SessionMemoriesDirectory: Path.Combine(root, "sessions")));
        return (store, root);
    }

    private static ToolExecutionContext CreateContext()
    {
        return new ToolExecutionContext(
            RunId.New(),
            new SessionKey("test-session"),
            "local",
            "rule-based-v1",
            CallerIdentity.LocalOwner());
    }

    private static ToolCall CreateToolCall(string name, object args)
    {
        return new ToolCall(
            Guid.NewGuid().ToString("n"),
            name,
            JsonSerializer.SerializeToElement(args));
    }
}
