using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class ReadAgentMessagesTool : ITool
{
    private const int DefaultMaxMessages = 20;
    private const int MaxReadLimit = 100;
    private readonly IAgentMessageBus _messageBus;

    public ReadAgentMessagesTool(IAgentMessageBus messageBus)
    {
        _messageBus = messageBus;

        Descriptor = new ToolDescriptor(
            Name: "read_agent_messages",
            Description: "Reads pending messages addressed to the current agent in the active scope.",
            ParametersSchema: JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    max_messages = new
                    {
                        type = "integer",
                        description = "Maximum number of messages to read (1-100). Defaults to 20."
                    },
                    mark_as_read = new
                    {
                        type = "boolean",
                        description = "When true (default), consumed messages are removed from the queue."
                    }
                },
                required = Array.Empty<string>()
            }),
            Group: "group:messaging");
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.AgentId))
        {
            return ToolResult.Error("read_agent_messages requires a named agent context.");
        }

        var maxMessages = DefaultMaxMessages;
        if (call.Arguments.TryGetProperty("max_messages", out var maxNode))
        {
            if (maxNode.ValueKind is not JsonValueKind.Number || !maxNode.TryGetInt32(out maxMessages))
            {
                return ToolResult.Error("max_messages must be an integer.");
            }
        }

        maxMessages = Math.Clamp(maxMessages, 1, MaxReadLimit);

        var markAsRead = true;
        if (call.Arguments.TryGetProperty("mark_as_read", out var markNode))
        {
            if (markNode.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                return ToolResult.Error("mark_as_read must be a boolean.");
            }

            markAsRead = markNode.GetBoolean();
        }

        var messages = await _messageBus.ReadAsync(
            new AgentMessageReadRequest(
                SessionKey: context.ScopeSessionKey,
                ScopeRunId: context.ScopeRunId,
                RecipientAgentId: context.AgentId.Trim(),
                MaxMessages: maxMessages,
                MarkAsRead: markAsRead),
            ct);

        var payload = JsonSerializer.Serialize(
            messages.Select(message => new
            {
                message_id = message.MessageId,
                sender_agent_id = message.SenderAgentId,
                recipient_agent_id = message.RecipientAgentId,
                content = message.Content,
                sent_at = message.SentAt.ToString("O"),
                correlation_id = message.CorrelationId,
                scope_session_id = message.SessionKey.Value,
                scope_run_id = message.ScopeRunId.Value
            }));

        return ToolResult.Success(
            payload,
            details: new Dictionary<string, object?>
            {
                ["observability_event_type"] = "message.received",
                ["recipient_agent_id"] = context.AgentId.Trim(),
                ["scope_session_id"] = context.ScopeSessionKey.Value,
                ["scope_run_id"] = context.ScopeRunId.Value,
                ["message_count"] = messages.Count,
                ["mark_as_read"] = markAsRead
            });
    }
}
