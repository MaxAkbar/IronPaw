using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed class TranscriptSanitizer : ITranscriptSanitizer
{
    public IReadOnlyList<ChatMessage> Repair(IReadOnlyList<ChatMessage> messages)
    {
        var sanitized = new List<ChatMessage>(messages.Count);
        var pendingToolCalls = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            if (message.Role is ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                pendingToolCalls.Add(message.ToolCallId!);
                sanitized.Add(message);
                continue;
            }

            if (message.Role is ChatRole.Tool)
            {
                if (string.IsNullOrWhiteSpace(message.ToolCallId))
                {
                    continue;
                }

                if (!pendingToolCalls.Remove(message.ToolCallId))
                {
                    continue;
                }

                sanitized.Add(message);
                continue;
            }

            sanitized.Add(message);
        }

        foreach (var missingCallId in pendingToolCalls)
        {
            sanitized.Add(new ChatMessage(
                Role: ChatRole.Tool,
                Content: "Synthetic tool error: missing tool result was repaired.",
                ToolCallId: missingCallId,
                ToolName: "unknown"));
        }

        return sanitized;
    }
}
