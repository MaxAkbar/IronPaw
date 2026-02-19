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

        if (tools.Any(t => t.Descriptor.Name == "create_skill"))
        {
            AppendCreateSkillGuidance(lines);
        }

        return request.SystemPromptOverride ?? string.Join(Environment.NewLine, lines);
    }

    private static void AppendCreateSkillGuidance(List<string> lines)
    {
        lines.Add("");
        lines.Add("## Dynamic skill creation");
        lines.Add("");
        lines.Add("You can extend your own capabilities at runtime using the `create_skill` tool.");
        lines.Add("Use it when the user needs a capability that no existing tool provides,");
        lines.Add("such as data transformation, calculation, parsing, or encoding tasks.");
        lines.Add("");
        lines.Add("When to create a skill:");
        lines.Add("- The user asks you to do something no available tool can handle.");
        lines.Add("- You need to perform a specialised operation repeatedly in this session.");
        lines.Add("- The user explicitly asks you to create a new skill.");
        lines.Add("");
        lines.Add("When NOT to create a skill:");
        lines.Add("- An existing tool already covers the request.");
        lines.Add("- The task is a one-off text answer you can produce directly.");
        lines.Add("- The request is ambiguous — ask for clarification first.");
        lines.Add("");
        lines.Add("The `source_code` argument must be a complete C# source file implementing");
        lines.Add("`IronPaw.AgentCore.Abstractions.ISkillAddin`. Follow this template:");
        lines.Add("");
        lines.Add("```csharp");
        lines.Add("using System.Text.Json;");
        lines.Add("using IronPaw.AgentCore.Abstractions;");
        lines.Add("");
        lines.Add("public sealed class MyAddin : ISkillAddin");
        lines.Add("{");
        lines.Add("    public string AddinId => \"my.addin\";");
        lines.Add("    public string Description => \"Short description of the addin.\";");
        lines.Add("    public IReadOnlyList<ISkill> CreateSkills() => [new MySkill()];");
        lines.Add("}");
        lines.Add("");
        lines.Add("internal sealed class MySkill : ISkill");
        lines.Add("{");
        lines.Add("    public SkillDescriptor Descriptor { get; } = new(");
        lines.Add("        Name: \"my_skill\",");
        lines.Add("        Description: \"What this skill does.\",");
        lines.Add("        InputSchema: JsonSerializer.SerializeToElement(new");
        lines.Add("        {");
        lines.Add("            type = \"object\",");
        lines.Add("            properties = new { input = new { type = \"string\", description = \"The input.\" } },");
        lines.Add("            required = new[] { \"input\" }");
        lines.Add("        }));");
        lines.Add("");
        lines.Add("    public Task<SkillResult> ExecuteAsync(SkillCall call, SkillExecutionContext ctx, CancellationToken ct)");
        lines.Add("    {");
        lines.Add("        var input = call.Arguments.GetProperty(\"input\").GetString() ?? \"\";");
        lines.Add("        // --- implement logic here ---");
        lines.Add("        return Task.FromResult(SkillResult.Success(input));");
        lines.Add("    }");
        lines.Add("}");
        lines.Add("```");
        lines.Add("");
        lines.Add("After a successful `create_skill` call the new `skill_*` tools appear");
        lines.Add("in your available tools on the next round. You can then call them normally.");
    }
}
