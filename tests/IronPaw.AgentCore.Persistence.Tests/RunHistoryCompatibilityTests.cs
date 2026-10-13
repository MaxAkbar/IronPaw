using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence.Tests;

public sealed class RunHistoryCompatibilityTests
{
    [Fact]
    public async Task IndependentHistoryWritersPreserveEveryRecord()
    {
        var root = Path.Combine(Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "history.jsonl");
            await Task.WhenAll(Enumerable.Range(0, 20).Select(async index =>
            {
                var store = new JsonRunHistoryStore(path);
                var now = DateTimeOffset.UtcNow;
                await store.RecordAsync(new RunSummary($"run-{index}", "session", now, now, "prompt", "response", 1, 10, []), CancellationToken.None);
            }));
            var records = await new JsonRunHistoryStore(path).LoadAllAsync(CancellationToken.None);
            Assert.Equal(20, records.Count);
            Assert.Equal(20, records.Select(record => record.RunId).Distinct().Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LegacyJsonHistoryAndNewOutcomesRemainReadable()
    {
        var root = Path.Combine(Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "history.jsonl");
            var now = DateTimeOffset.UtcNow;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                RunId = "legacy", SessionId = "session", StartedAt = now, CompletedAt = now,
                UserPrompt = "prompt", FinalAssistantMessage = "response", Rounds = 1, TotalTokens = 10, ToolsUsed = new[] { "read_file" }
            }) + Environment.NewLine);
            var store = new JsonRunHistoryStore(path);
            await store.RecordAsync(new RunSummary("failed", "session", now, now, "new prompt", "failure", 1, 20, [],
                AgentRunStatus.Failed, "provider failure"), CancellationToken.None);
            var all = await store.LoadAllAsync(CancellationToken.None);
            Assert.Equal(AgentRunStatus.Succeeded, all[0].Status);
            Assert.Null(all[0].Error);
            Assert.Equal(AgentRunStatus.Failed, all[1].Status);
            Assert.Equal("provider failure", all[1].Error);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
