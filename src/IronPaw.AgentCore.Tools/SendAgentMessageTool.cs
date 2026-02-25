using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class SendAgentMessageTool : ITool
{
    private readonly IAgentMessageBus _messageBus;
    private readonly IAgentRegistry _agentRegistry;

    public SendAgentMessageTool(IAgentMessageBus messageBus, IAgentRegistry agentRegistry)
    {
        _messageBus = messageBus;
        _agentRegistry = agentRegistry;

        Descriptor = new ToolDescriptor(
            Name: "send_agent_message",
            Description: "Sends a message to another agent within the current scoped run/session.",
            ParametersSchema: JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    recipient_agent_id = new
                    {
                        type = "string",
                        description = "The target agent ID."
                    },
                    message = new
                    {
                        type = "string",
                        description = "Message content to deliver."
                    },
                    correlation_id = new
                    {
                        type = "string",
                        description = "Optional correlation identifier for request/response workflows."
                    }
                },
                required = new[] { "recipient_agent_id", "message" }
            }),
            Group: "group:messaging");
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.AgentId))
        {
            return ToolResult.Error("send_agent_message requires a named agent context.");
        }

        if (!call.Arguments.TryGetProperty("recipient_agent_id", out var recipientNode) ||
            recipientNode.ValueKind is not JsonValueKind.String)
        {
            return ToolResult.Error("Missing required parameter: recipient_agent_id");
        }

        if (!call.Arguments.TryGetProperty("message", out var messageNode) ||
            messageNode.ValueKind is not JsonValueKind.String)
        {
            return ToolResult.Error("Missing required parameter: message");
        }

        var senderAgentId = context.AgentId.Trim();
        var recipientAgentId = recipientNode.GetString()?.Trim();
        var message = messageNode.GetString()?.Trim();
        var correlationId = TryGetOptionalString(call.Arguments, "correlation_id");

        if (string.IsNullOrWhiteSpace(recipientAgentId))
        {
            return ToolResult.Error("recipient_agent_id cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return ToolResult.Error("message cannot be empty.");
        }

        var recipient = _agentRegistry.Get(recipientAgentId);
        if (recipient is null)
        {
            return ToolResult.Error($"Agent '{recipientAgentId}' is not registered.");
        }

        if (!recipient.CanReceiveMessagesFrom(senderAgentId))
        {
            return ToolResult.Error(
                $"Agent '{senderAgentId}' is not authorized to message '{recipientAgentId}'.");
        }

        var delivered = await _messageBus.SendAsync(
            new AgentMessageSendRequest(
                SessionKey: context.ScopeSessionKey,
                ScopeRunId: context.ScopeRunId,
                SenderAgentId: senderAgentId,
                RecipientAgentId: recipientAgentId,
                Content: message,
                CorrelationId: correlationId),
            ct);

        var payload = JsonSerializer.Serialize(new
        {
            message_id = delivered.MessageId,
            sender_agent_id = delivered.SenderAgentId,
            recipient_agent_id = delivered.RecipientAgentId,
            scope_session_id = delivered.SessionKey.Value,
            scope_run_id = delivered.ScopeRunId.Value,
            sent_at = delivered.SentAt.ToString("O"),
            correlation_id = delivered.CorrelationId
        });

        return ToolResult.Success(
            payload,
            details: new Dictionary<string, object?>
            {
                ["observability_event_type"] = "message.sent",
                ["message_id"] = delivered.MessageId,
                ["sender_agent_id"] = delivered.SenderAgentId,
                ["recipient_agent_id"] = delivered.RecipientAgentId,
                ["scope_session_id"] = delivered.SessionKey.Value,
                ["scope_run_id"] = delivered.ScopeRunId.Value,
                ["correlation_id"] = delivered.CorrelationId
            });
    }

    private static string? TryGetOptionalString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var node) || node.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (node.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        var value = node.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
