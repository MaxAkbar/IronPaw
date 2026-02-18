using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.Skills.ExampleAddin;

namespace IronPaw.Skills.ExampleAddin.Tests;

public sealed class ExampleSkillAddinTests
{
    [Fact]
    public void CreateSkills_ReturnsExpectedSkills()
    {
        var addin = new ExampleSkillAddin();
        var skills = addin.CreateSkills();
        var names = skills.Select(skill => skill.Descriptor.Name).OrderBy(name => name).ToArray();

        Assert.Equal(["slugify", "text_stats"], names);
    }

    [Fact]
    public async Task TextStatsSkill_ReturnsExpectedCounts()
    {
        var addin = new ExampleSkillAddin();
        var skill = addin.CreateSkills().Single(skill => skill.Descriptor.Name == "text_stats");
        var call = new SkillCall(
            SkillCallId: "s1",
            SkillName: "text_stats",
            Arguments: JsonSerializer.SerializeToElement(new { text = "alpha beta\ngamma" }));

        var result = await skill.ExecuteAsync(call, CreateContext(), CancellationToken.None);
        Assert.False(result.IsError);

        using var payload = JsonDocument.Parse(result.Content);
        Assert.Equal(16, payload.RootElement.GetProperty("characters").GetInt32());
        Assert.Equal(3, payload.RootElement.GetProperty("words").GetInt32());
        Assert.Equal(2, payload.RootElement.GetProperty("lines").GetInt32());
    }

    [Fact]
    public async Task SlugifySkill_RespectsSeparator()
    {
        var addin = new ExampleSkillAddin();
        var skill = addin.CreateSkills().Single(skill => skill.Descriptor.Name == "slugify");
        var call = new SkillCall(
            SkillCallId: "s2",
            SkillName: "slugify",
            Arguments: JsonSerializer.SerializeToElement(new { text = "Hello, Skill Addin!", separator = "_" }));

        var result = await skill.ExecuteAsync(call, CreateContext(), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal("hello_skill_addin", result.Content);
    }

    private static SkillExecutionContext CreateContext()
    {
        return new SkillExecutionContext(
            RunId: new RunId("run"),
            SessionKey: new SessionKey("session"),
            Provider: "local",
            Model: "rule");
    }
}
