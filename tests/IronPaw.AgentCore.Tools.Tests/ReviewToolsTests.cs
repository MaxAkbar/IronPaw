using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class ReviewToolsTests
{
    [Fact]
    public async Task ReviewRunsTool_ListsRecentRuns()
    {
        var (store, root) = CreateStore();
        try
        {
            await store.RecordAsync(CreateSummary("r1", "s1", "First prompt"), CancellationToken.None);
            await store.RecordAsync(CreateSummary("r2", "s1", "Second prompt"), CancellationToken.None);

            var tool = new ReviewRunsTool(store);
            var call = CreateToolCall("review_runs", new { last_n = 5 });

            var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

            Assert.False(result.IsError);
            var parsed = JsonDocument.Parse(result.Content);
            Assert.Equal(2, parsed.RootElement.EnumerateArray().Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReviewRunsTool_FiltersBySession()
    {
        var (store, root) = CreateStore();
        try
        {
            await store.RecordAsync(CreateSummary("r1", "session-a", "Prompt A"), CancellationToken.None);
            await store.RecordAsync(CreateSummary("r2", "session-b", "Prompt B"), CancellationToken.None);

            var tool = new ReviewRunsTool(store);
            var call = CreateToolCall("review_runs", new { session_id = "session-a" });

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
    public async Task ReviewRunDetailTool_ReturnsFullDetail()
    {
        var (store, root) = CreateStore();
        try
        {
            await store.RecordAsync(CreateSummary("run-detail-test", "s1", "Detailed prompt"), CancellationToken.None);

            var tool = new ReviewRunDetailTool(store);
            var call = CreateToolCall("review_run_detail", new { run_id = "run-detail-test" });

            var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

            Assert.False(result.IsError);
            var parsed = JsonDocument.Parse(result.Content);
            Assert.Equal("Detailed prompt", parsed.RootElement.GetProperty("user_prompt").GetString());
            Assert.Equal("run-detail-test", parsed.RootElement.GetProperty("run_id").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReviewRunDetailTool_ReturnsErrorForMissingRun()
    {
        var (store, root) = CreateStore();
        try
        {
            var tool = new ReviewRunDetailTool(store);
            var call = CreateToolCall("review_run_detail", new { run_id = "nonexistent" });

            var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);
            Assert.True(result.IsError);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (JsonRunHistoryStore Store, string Root) CreateStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return (new JsonRunHistoryStore(Path.Combine(root, "run-history.jsonl")), root);
    }

    private static ToolExecutionContext CreateContext()
    {
        return new ToolExecutionContext(
            RunId.New(),
            new SessionKey("test-session"),
            "local",
            "rule-based-v1");
    }

    private static ToolCall CreateToolCall(string name, object args)
    {
        return new ToolCall(
            Guid.NewGuid().ToString("n"),
            name,
            JsonSerializer.SerializeToElement(args));
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
}
