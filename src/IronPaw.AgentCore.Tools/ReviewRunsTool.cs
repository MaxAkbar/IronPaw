using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class ReviewRunsTool : ITool
{
    private readonly IRunHistoryStore _runHistoryStore;

    public ReviewRunsTool(IRunHistoryStore runHistoryStore)
    {
        _runHistoryStore = runHistoryStore;
        Descriptor = new ToolDescriptor(
            Name: "review_runs",
            Description: "Lists recent task run summaries. Optionally filter by session.",
            ParametersSchema: JsonSchemaBuilder.BuildObjectSchema(
            [
                new("session_id", "string", "Optional session ID to filter runs by.", Required: false),
                new("last_n", "integer", "Number of recent runs to return (default: 5).", Required: false)
            ]),
            Group: "group:system");
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        var lastN = 5;
        if (call.Arguments.TryGetProperty("last_n", out var lastNEl) &&
            lastNEl.ValueKind == JsonValueKind.Number)
        {
            lastN = lastNEl.GetInt32();
            if (lastN < 1)
            {
                lastN = 1;
            }
            else if (lastN > 50)
            {
                lastN = 50;
            }
        }

        IReadOnlyList<RunSummary> runs;
        if (call.Arguments.TryGetProperty("session_id", out var sessionEl) &&
            sessionEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(sessionEl.GetString()))
        {
            runs = await _runHistoryStore.LoadBySessionAsync(new SessionKey(sessionEl.GetString()!), ct);
        }
        else
        {
            runs = await _runHistoryStore.LoadAllAsync(ct);
        }

        var recent = runs
            .OrderByDescending(r => r.StartedAt)
            .Take(lastN)
            .Select(r => new
            {
                run_id = r.RunId,
                session_id = r.SessionId,
                started_at = r.StartedAt.ToString("O"),
                prompt = r.UserPrompt.Length > 100 ? r.UserPrompt[..100] + "..." : r.UserPrompt,
                rounds = r.Rounds,
                total_tokens = r.TotalTokens,
                tools_used = r.ToolsUsed
            });

        return ToolResult.Success(JsonSerializer.Serialize(recent));
    }
}
