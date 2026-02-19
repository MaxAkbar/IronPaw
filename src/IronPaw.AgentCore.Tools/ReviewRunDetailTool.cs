using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class ReviewRunDetailTool : ITool
{
    private readonly IRunHistoryStore _runHistoryStore;

    public ReviewRunDetailTool(IRunHistoryStore runHistoryStore)
    {
        _runHistoryStore = runHistoryStore;
        Descriptor = new ToolDescriptor(
            Name: "review_run_detail",
            Description: "Retrieves the full details of a specific task run by its run ID.",
            ParametersSchema: JsonSchemaBuilder.BuildObjectSchema(
            [
                new("run_id", "string", "The run ID to look up.", Required: true)
            ]),
            Group: "group:system");
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        if (!call.Arguments.TryGetProperty("run_id", out var runIdEl) ||
            runIdEl.ValueKind != JsonValueKind.String)
        {
            return ToolResult.Error("Missing required string argument: run_id");
        }

        var runId = runIdEl.GetString()!.Trim();
        if (string.IsNullOrWhiteSpace(runId))
        {
            return ToolResult.Error("run_id cannot be empty.");
        }

        var summary = await _runHistoryStore.GetAsync(runId, ct);
        if (summary is null)
        {
            return ToolResult.Error($"Run '{runId}' was not found.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            run_id = summary.RunId,
            session_id = summary.SessionId,
            started_at = summary.StartedAt.ToString("O"),
            completed_at = summary.CompletedAt.ToString("O"),
            user_prompt = summary.UserPrompt,
            final_assistant_message = summary.FinalAssistantMessage,
            rounds = summary.Rounds,
            total_tokens = summary.TotalTokens,
            tools_used = summary.ToolsUsed
        });

        return ToolResult.Success(payload);
    }
}
