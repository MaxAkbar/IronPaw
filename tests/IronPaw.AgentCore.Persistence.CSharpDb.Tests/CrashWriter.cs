using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.AgentCore.Persistence.CSharpDb.Tests;

/// <summary>Child-process fixture: the test kills it without disposing the store.</summary>
internal static class CrashWriter
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length >= 2 && args[0].StartsWith("application-", StringComparison.Ordinal))
        {
            var application = new CSharpDbApplicationStore(new(args[1]));
            if (args[0] == "application-writer")
            {
                for (var i = 0; i < int.Parse(args[2]); i++)
                    await application.UpsertAsync(new("process-counter"), old =>
                        (old ?? new SessionEntry("process-counter", DateTimeOffset.UtcNow)) with { TotalTokens = (old?.TotalTokens ?? 0) + 1 }, CancellationToken.None);
                return 0;
            }
            if (args[0] == "application-held-import")
            {
                application = new(new(args[1]), null, async _ =>
                {
                    Console.WriteLine("application-held"); await Console.Out.FlushAsync();
                    await Task.Delay(Timeout.Infinite);
                });
                await application.LoadAllAsync(CancellationToken.None);
                return 0;
            }
            if (args[0] == "application-held-mutation")
            {
                await application.UpsertAsync(new("uncommitted"), _ =>
                {
                    Console.WriteLine("application-held"); Console.Out.Flush();
                    Thread.Sleep(Timeout.Infinite);
                    return new("uncommitted", DateTimeOffset.UtcNow);
                }, CancellationToken.None);
                return 0;
            }
            return 2;
        }
        if (args is ["evaluation-crash-writer", var evaluationPath, var phase])
        {
            await using var evaluations = await CSharpDbWorkflowStore.OpenAsync(evaluationPath);
            var owner = new CallerIdentity("owner", "Owner", true);
            await evaluations.CreateEvaluationAsync(new("campaign", "provider", "exact-model", "USD", 10m, "{}"), owner, CancellationToken.None);
            await evaluations.BeginEvaluationRequestAsync("campaign", new("paid", "trial", "direct", new string('a', 64), 4m), owner, CancellationToken.None);
            if (phase == "completed")
                await evaluations.CompleteEvaluationRequestAsync("campaign", "paid", new(true, 1m, 100, 20, "{\"durableResponse\":\"bounded response\"}"), owner, CancellationToken.None);
            else if (phase != "started") return 2;
            Console.WriteLine("evaluation-committed");
            await Console.Out.FlushAsync();
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        if (args is not ["crash-writer", var path]) return 2;
        await using var store = await CSharpDbWorkflowStore.OpenAsync(path);
        var request = new AgentRunRequest(new SessionKey("crash-session"), "objective", "fake", "fake",
            new ToolSelectionPolicy(ToolProfile.Full), new CallerIdentity("owner", "Owner", true));
        await store.CreateAsync(new WorkflowCheckpoint("crash-run", "crash-run", null, "crash-task", request,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Status: WorkflowStatus.Running, LeaseOwner: "crash-writer",
            Usage: new WorkflowUsage(2, 2, InputTokens: 17),
            ModelCalls: [new WorkflowModelCall("response", [new WorkflowModelDelta("text", "durable response")])],
            ToolExecutions:
            [
                new WorkflowToolExecution("confirmed", new ToolCall("call1", "fake", JsonSerializer.SerializeToElement(new { })), ToolResult.Success("durable receipt")),
                new WorkflowToolExecution("unknown", new ToolCall("call2", "fake", JsonSerializer.SerializeToElement(new { })))
            ]), CancellationToken.None);
        Console.WriteLine("committed");
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
}
