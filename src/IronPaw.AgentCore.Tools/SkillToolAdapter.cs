using System.Text.RegularExpressions;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

internal sealed class SkillToolAdapter : ITool
{
    private static readonly Regex InvalidNameCharacterRegex = new("[^a-z0-9_]", RegexOptions.Compiled);
    private readonly ISkill _skill;

    public SkillToolAdapter(ISkill skill, string addinId, string toolName)
    {
        _skill = skill;
        AddinId = addinId;
        Descriptor = new ToolDescriptor(
            Name: toolName,
            Description: $"Skill ({addinId}): {skill.Descriptor.Description}",
            ParametersSchema: skill.Descriptor.InputSchema.Clone(),
            Group: "group:skills");
    }

    public string AddinId { get; }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        try
        {
            var skillCall = new SkillCall(
                SkillCallId: call.ToolCallId,
                SkillName: _skill.Descriptor.Name,
                Arguments: call.Arguments.Clone());
            var skillContext = new SkillExecutionContext(
                context.RunId,
                context.SessionKey,
                context.Provider,
                context.Model);

            var result = await _skill.ExecuteAsync(skillCall, skillContext, ct);
            return new ToolResult(result.IsError, result.Content, result.Details);
        }
        catch (Exception ex)
        {
            return ToolResult.Error(
                $"Skill '{_skill.Descriptor.Name}' failed: {ex.Message}",
                new Dictionary<string, object?>
                {
                    ["skill"] = _skill.Descriptor.Name,
                    ["addin"] = AddinId,
                    ["error"] = ex.ToString()
                });
        }
    }

    public static string BuildToolName(string skillName)
    {
        var normalized = skillName.Trim().ToLowerInvariant();
        normalized = normalized.Replace('-', '_').Replace(' ', '_');
        normalized = InvalidNameCharacterRegex.Replace(normalized, "_");
        normalized = normalized.Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "unnamed";
        }

        return $"skill_{normalized}";
    }
}
