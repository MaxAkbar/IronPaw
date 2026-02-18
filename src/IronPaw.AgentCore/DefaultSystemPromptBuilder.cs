using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed class DefaultSystemPromptBuilder : ISystemPromptBuilder
{
    public string BuildPrompt(AgentRunRequest request, IReadOnlyList<ITool> tools)
    {
        var lines = new List<string>
        {
            "You are IronPaw, a reliable coding assistant.",
            "Use tools when needed, but keep responses concise and actionable.",
            $"Current local time: {DateTimeOffset.Now:O}",
            "Tooling rules:",
            "- Execute only declared tools.",
            "- Report tool failures explicitly.",
            "- Keep filesystem edits within the current workspace.",
            "",
            "Available tools:"
        };

        if (tools.Count == 0)
        {
            lines.Add("- none");
        }
        else
        {
            foreach (var tool in tools)
            {
                lines.Add($"- {tool.Descriptor.Name}: {tool.Descriptor.Description}");
            }
        }

        return request.SystemPromptOverride ?? string.Join(Environment.NewLine, lines);
    }
}
