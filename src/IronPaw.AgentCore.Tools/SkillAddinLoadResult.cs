using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed record LoadedSkillInfo(
    string AddinId,
    string SkillName,
    string ToolName,
    string Description);

public sealed record SkillAddinLoadResult(
    IReadOnlyList<ITool> SkillTools,
    IReadOnlyList<LoadedSkillInfo> LoadedSkills,
    IReadOnlyList<string> Diagnostics)
{
    public int AddinsLoaded => LoadedSkills
        .Select(skill => skill.AddinId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public int SkillsLoaded => LoadedSkills.Count;
}
