using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class RequestInputTool : IHumanInputTool
{
    public ToolDescriptor Descriptor { get; } = new("request_input",
        "Ask the user a question and suspend until their answer is supplied. Use for missing requirements or decisions.",
        JsonSerializer.SerializeToElement(new { type = "object", properties = new {
            question = new { type = "string", description = "A complete, concise question for the user." } },
            required = new[] { "question" } }), "group:system");

    public string GetQuestion(ToolCall call)
    {
        if (!call.Arguments.TryGetProperty("question", out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException("request_input requires a nonempty question.");
        return value.GetString()!;
    }

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct) =>
        Task.FromResult(ToolResult.Error("Human input requires durable workflow storage."));
}
