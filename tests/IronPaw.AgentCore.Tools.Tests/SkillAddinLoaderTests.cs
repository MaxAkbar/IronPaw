using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class SkillAddinLoaderTests
{
    [Fact]
    public async Task LoadFromDirectories_LoadsExampleAddinAndExecutesSkillTool()
    {
        var directory = CreateTempDirectory();
        try
        {
            var sourceAssemblyPath = typeof(IronPaw.Skills.ExampleAddin.ExampleSkillAddin).Assembly.Location;
            var destinationAssemblyPath = Path.Combine(directory, "ExampleAddin.dll");
            File.Copy(sourceAssemblyPath, destinationAssemblyPath, overwrite: true);

            var loadResult = SkillAddinLoader.LoadFromDirectories([directory]);

            Assert.Equal(2, loadResult.SkillsLoaded);
            Assert.Contains(loadResult.LoadedSkills, skill => skill.SkillName == "slugify");
            Assert.Contains(loadResult.LoadedSkills, skill => skill.SkillName == "text_stats");

            var slugifyTool = loadResult.SkillTools.Single(tool => tool.Descriptor.Name == "skill_slugify");
            var toolCall = new ToolCall(
                ToolCallId: "call-1",
                ToolName: slugifyTool.Descriptor.Name,
                Arguments: JsonSerializer.SerializeToElement(new { text = "Hello, Test World!" }));
            var context = new ToolExecutionContext(new RunId("run-1"), new SessionKey("session-1"), "local", "rule");

            var result = await slugifyTool.ExecuteAsync(toolCall, context, CancellationToken.None);
            Assert.False(result.IsError);
            Assert.Equal("hello-test-world", result.Content);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadFromDirectories_MultipleAssembliesWithSameSkills_AssignsUniqueToolNames()
    {
        var directory = CreateTempDirectory();
        try
        {
            var sourceAssemblyPath = typeof(IronPaw.Skills.ExampleAddin.ExampleSkillAddin).Assembly.Location;
            File.Copy(sourceAssemblyPath, Path.Combine(directory, "ExampleAddin1.dll"), overwrite: true);
            File.Copy(sourceAssemblyPath, Path.Combine(directory, "ExampleAddin2.dll"), overwrite: true);

            var loadResult = SkillAddinLoader.LoadFromDirectories([directory]);
            var toolNames = loadResult.LoadedSkills.Select(skill => skill.ToolName).ToArray();

            Assert.Equal(4, loadResult.SkillsLoaded);
            Assert.Equal(toolNames.Length, toolNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Contains("skill_slugify", toolNames);
            Assert.Contains("skill_slugify_2", toolNames);
            Assert.Contains("skill_text_stats", toolNames);
            Assert.Contains("skill_text_stats_2", toolNames);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
