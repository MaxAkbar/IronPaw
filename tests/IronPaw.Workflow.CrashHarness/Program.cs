using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;

namespace IronPaw.Workflow.CrashHarness;

public enum CrashPoint { AfterCompletedTool, AfterEffectBeforeToolResult }

public static class CrashFixture
{
    public static string DatabasePath(string root) => Path.Combine(root, "workflows.cdb");
    public static string ReadyPath(string root) => Path.Combine(root, "ready.txt");
    public static string EffectsPath(string root) => Path.Combine(root, "effects.txt");
    public static AgentRunRequest Request { get; } = new(new SessionKey("process-crash"), "Record the effect then report success",
        "local", "crash-test", new ToolSelectionPolicy(ToolProfile.Coding), CallerIdentity.Authenticated("crash-caller", "Crash caller"),
        SystemPromptOverride: "Stable subprocess crash recovery prompt", Budget: new WorkflowBudget(4, 2));

    public static AgentOrchestrator CreateOrchestrator(string root, IWorkflowStore store,
        SessionLaneScheduler scheduler, ILlmClient client, ITool tool) => new(scheduler,
        new FileSessionStore(new FileSessionStoreOptions(Path.Combine(root, "sessions.json"), TimeSpan.Zero)),
        new JsonTranscriptStore(Path.Combine(root, "transcripts")), new InMemoryToolRegistry([tool]),
        new ToolExecutor(), new ToolSchemaNormalizer(), client, new TranscriptSanitizer(), new ToolResultTruncator(),
        new DefaultSystemPromptBuilder(), workflowStore: store);

    internal static async Task SignalAsync(string root, RunId runId, CancellationToken ct)
    {
        // The signal is published last: the parent can kill only after the effect
        // and/or durable checkpoint under test has reached the requested boundary.
        var signal = ReadyPath(root);
        await File.WriteAllTextAsync(signal + ".tmp", runId.Value, ct);
        File.Move(signal + ".tmp", signal);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
}

public sealed class CrashEffectTool(string root, CrashPoint? point = null) : ITool
{
    public int CallCount { get; private set; }
    public ToolDescriptor Descriptor { get; } = new("crash_effect", "Records one visible effect",
        JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}"""), "group:system");

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        CallCount++;
        var payload = Encoding.UTF8.GetBytes($"{context.RunId.Value}:{context.ToolExecutionId}{Environment.NewLine}");
        await using (var file = new FileStream(CrashFixture.EffectsPath(root), FileMode.Append, FileAccess.Write,
            FileShare.Read, 4096, FileOptions.WriteThrough))
        {
            await file.WriteAsync(payload, ct);
            file.Flush(flushToDisk: true);
        }
        if (point == CrashPoint.AfterEffectBeforeToolResult) await CrashFixture.SignalAsync(root, context.RunId, ct);
        return ToolResult.Success("effect recorded");
    }
}

public sealed class CrashModel(string root) : ILlmClient
{
    private int _calls;
    public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        if (++_calls == 1)
        {
            yield return new UsageDelta(7, 2, null, null);
            yield return new ToolCallDelta("first-effect", "crash_effect", JsonSerializer.SerializeToElement(new { }));
            yield return new CompletedDelta();
            yield break;
        }
        await CrashFixture.SignalAsync(root, request.RunId, ct);
    }
}

public sealed class RecoveryModel : ILlmClient
{
    public int CallCount { get; private set; }
    public List<LlmRequest> Requests { get; } = [];
    public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        CallCount++;
        Requests.Add(request);
        yield return new UsageDelta(3, 1, null, null);
        yield return new AssistantTextDelta("recovered successfully");
        yield return new CompletedDelta();
    }
}

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var root = args[0];
        var point = Enum.Parse<CrashPoint>(args[1]);
        await using var store = await CSharpDbWorkflowStore.OpenAsync(CrashFixture.DatabasePath(root));
        using var scheduler = new SessionLaneScheduler();
        var orchestrator = CrashFixture.CreateOrchestrator(root, store, scheduler, new CrashModel(root), new CrashEffectTool(root, point));
        await orchestrator.RunAsync(CrashFixture.Request, NullAgentEventSink.Instance, CancellationToken.None);
    }
}
