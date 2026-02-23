using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class ToolPolicyEvaluatorTests
{
    [Fact]
    public void Apply_CodingProfile_AllowsFsSystemAndSkills()
    {
        var tools = new ITool[]
        {
            new StubTool("fs", "group:fs"),
            new StubTool("runtime", "group:runtime"),
            new StubTool("msg", "group:messaging"),
            new StubTool("system", "group:system"),
            new StubTool("skill", "group:skills")
        };

        var result = ToolPolicyEvaluator.Apply(tools, new ToolSelectionPolicy(ToolProfile.Coding, SenderIsOwner: true));
        var names = result.Select(tool => tool.Descriptor.Name).ToArray();

        Assert.Contains("fs", names);
        Assert.Contains("system", names);
        Assert.Contains("skill", names);
        Assert.DoesNotContain("runtime", names);
        Assert.DoesNotContain("msg", names);
    }

    [Fact]
    public void Apply_RespectsOwnerOnlyAllowDenyAndSubagentDepth()
    {
        var tools = new ITool[]
        {
            new StubTool("runtime", "group:runtime"),
            new StubTool("fs_owner", "group:fs", ownerOnly: true),
            new StubTool("fs_public", "group:fs")
        };

        var result = ToolPolicyEvaluator.Apply(
            tools,
            new ToolSelectionPolicy(
                Profile: ToolProfile.Full,
                SenderIsOwner: false,
                AllowList: ["runtime", "fs_owner", "fs_public"],
                DenyList: ["runtime"],
                SubagentDepth: 1));

        var names = result.Select(tool => tool.Descriptor.Name).ToArray();
        Assert.Contains("fs_public", names);
        Assert.DoesNotContain("runtime", names);
        Assert.DoesNotContain("fs_owner", names);
    }

    [Fact]
    public void BuiltInToolFactory_AddsAdditionalTools()
    {
        var additional = new[] { new StubTool("custom", "group:skills") };
        var result = BuiltInToolFactory.CreateDefault(Path.GetTempPath(), additional);

        Assert.Contains(result, tool => tool.Descriptor.Name == "custom");
    }

    [Fact]
    public void Apply_NonOwner_ExcludesOwnerOnlyBuiltInTools()
    {
        var tools = BuiltInToolFactory.CreateDefault(Path.GetTempPath());
        var policy = new ToolSelectionPolicy(
            Profile: ToolProfile.Full,
            SenderIsOwner: false);

        var result = ToolPolicyEvaluator.Apply(tools, policy);
        var names = result.Select(t => t.Descriptor.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("write_file", names);
        Assert.Contains("read_file", names);
        Assert.Contains("list_files", names);
        Assert.Contains("current_time", names);
    }

    [Fact]
    public void Apply_Owner_IncludesOwnerOnlyBuiltInTools()
    {
        var tools = BuiltInToolFactory.CreateDefault(Path.GetTempPath());
        var policy = new ToolSelectionPolicy(
            Profile: ToolProfile.Full,
            SenderIsOwner: true);

        var result = ToolPolicyEvaluator.Apply(tools, policy);
        var names = result.Select(t => t.Descriptor.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("write_file", names);
        Assert.Contains("read_file", names);
    }

    private sealed class StubTool : ITool
    {
        public StubTool(string name, string group, bool ownerOnly = false)
        {
            Descriptor = new ToolDescriptor(
                Name: name,
                Description: name,
                ParametersSchema: JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
                Group: group,
                OwnerOnly: ownerOnly);
        }

        public ToolDescriptor Descriptor { get; }

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
        {
            return Task.FromResult(ToolResult.Success("ok"));
        }
    }
}
