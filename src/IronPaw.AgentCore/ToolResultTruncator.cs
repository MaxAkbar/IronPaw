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
        var maxChars = Math.Max(256, (int)(contextWindowChars * _maxToolResultShare));
        var output = new List<ChatMessage>(messages.Count);

        foreach (var message in messages)
        {
            if (message.Role is not ChatRole.Tool || message.Content.Length <= maxChars)
            {
                output.Add(message);
                continue;
            }

            var truncated = message.Content[..maxChars];
            output.Add(message with
            {
                Content = $"{truncated}\n\n[tool result truncated to {maxChars} chars]"
            });
        }

        return output;
    }
}
