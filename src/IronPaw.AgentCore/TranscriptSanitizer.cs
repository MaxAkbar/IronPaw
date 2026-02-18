using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed class TranscriptSanitizer : ITranscriptSanitizer
{
    public IReadOnlyList<ChatMessage> Repair(IReadOnlyList<ChatMessage> messages)
    {
        var sanitized = new List<ChatMessage>(messages.Count);
        var pendingToolCalls = new HashSet<string>(StringComparer.Ordinal);

        // Track where each orphaned tool-call message landed in the sanitized list
        // so we can insert synthetic results immediately after it.
        var callPositions = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            if (message.Role is ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                pendingToolCalls.Add(message.ToolCallId!);
                callPositions[message.ToolCallId!] = sanitized.Count;
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

        // Insert synthetic results immediately after each orphaned tool-call message.
        // Process in descending position order so earlier insertions don't shift later indices.
        var orphans = pendingToolCalls
            .Select(id => (
                Id: id,
                Position: callPositions.TryGetValue(id, out var pos) ? pos : sanitized.Count - 1))
            .OrderByDescending(x => x.Position)
            .ToList();

        foreach (var (id, position) in orphans)
        {
            sanitized.Insert(
                position + 1,
                new ChatMessage(
                    Role: ChatRole.Tool,
                    Content: "Synthetic tool error: missing tool result was repaired.",
                    ToolCallId: id,
                    ToolName: "unknown"));
        }

        return sanitized;
    }
}
