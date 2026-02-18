using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed class ToolResultTruncator : IToolResultTruncator
{
    private readonly double _maxToolResultShare;

    public ToolResultTruncator(double maxToolResultShare = 0.35)
    {
        _maxToolResultShare = Math.Clamp(maxToolResultShare, 0.1, 0.8);
    }

    public IReadOnlyList<ChatMessage> Truncate(IReadOnlyList<ChatMessage> messages, int contextWindowChars)
    {
        var totalBudget = Math.Max(256, (int)(contextWindowChars * _maxToolResultShare));

        // First pass: check whether any truncation is needed at all.
        var totalToolChars = messages
            .Where(m => m.Role is ChatRole.Tool)
            .Sum(m => m.Content.Length);

        if (totalToolChars <= totalBudget)
        {
            return messages;
        }

        // Second pass: enforce the budget. Allocate fairly across all tool results;
        // if one result is larger than its share, cap it and redistribute the savings.
        // Simple approach: truncate the largest results first until we're within budget.
        var toolMessages = messages
            .Select((m, i) => (Message: m, Index: i))
            .Where(x => x.Message.Role is ChatRole.Tool)
            .OrderByDescending(x => x.Message.Content.Length)
            .ToList();

        var overBudget = totalToolChars - totalBudget;
        var truncations = new Dictionary<int, int>(); // original index → new length

        foreach (var (msg, idx) in toolMessages)
        {
            if (overBudget <= 0) break;
            var cut = Math.Min(msg.Content.Length, overBudget);
            var newLength = Math.Max(0, msg.Content.Length - cut);
            truncations[idx] = newLength;
            overBudget -= cut;
        }

        if (truncations.Count == 0)
        {
            return messages;
        }

        var output = new List<ChatMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (truncations.TryGetValue(i, out var newLen))
            {
                var truncated = message.Content[..newLen];
                output.Add(message with
                {
                    Content = $"{truncated}\n\n[tool result truncated to {newLen} chars]"
                });
            }
            else
            {
                output.Add(message);
            }
        }

        return output;
    }
}
