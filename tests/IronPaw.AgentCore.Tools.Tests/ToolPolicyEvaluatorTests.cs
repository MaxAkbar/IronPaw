using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class ToolPolicyEvaluatorTests
{
    [Fact]
    public void Apply_DelegationPreservesRestrictionsFromEveryAncestor()
    {
        var root = new ToolSelectionPolicy(ToolProfile.Full, SenderIsOwner: true,
            AllowList: ["read_file", "write_file", "current_time"], DenyList: ["write_file"]);
        var parent = new ToolSelectionPolicy(ToolProfile.Coding, SenderIsOwner: true,
            DenyList: ["read_file"], InheritedPolicy: root);
        var child = new ToolSelectionPolicy(ToolProfile.Full, SenderIsOwner: true,
            SubagentDepth: 2, InheritedPolicy: parent);
        var tools = BuiltInToolFactory.CreateDefault(Path.GetTempPath());
        Assert.Equal("current_time", Assert.Single(ToolPolicyEvaluator.Apply(tools, child)).Descriptor.Name);
    }

    [Fact]
    public void Apply_CodingProfile_AllowsFsSystemAndSkills()
    {
        var tools = new ITool[]
        {
            new StubTool("fs", "group:fs"),
            new StubTool("runtime", "group:runtime"),
            new StubTool("msg", "group:messaging"),
            new StubTool("web", "group:web"),
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
        Assert.DoesNotContain("web", names);
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

    [Fact]
    public void Apply_FullProfile_IncludesAgentGroup()
    {
        var tools = new ITool[]
        {
            new StubTool("invoke_agent", "group:agents"),
            new StubTool("list_agents", "group:agents"),
            new StubTool("system", "group:system")
        };

        var result = ToolPolicyEvaluator.Apply(tools, new ToolSelectionPolicy(ToolProfile.Full, SenderIsOwner: true));
        var names = result.Select(t => t.Descriptor.Name).ToArray();

        Assert.Contains("invoke_agent", names);
        Assert.Contains("list_agents", names);
        Assert.Contains("system", names);
    }

    [Fact]
    public void Apply_FullProfile_IncludesWebGroup()
    {
        var tools = new ITool[]
        {
            new StubTool("web_navigate", "group:web"),
            new StubTool("system", "group:system")
        };

        var result = ToolPolicyEvaluator.Apply(tools, new ToolSelectionPolicy(ToolProfile.Full, SenderIsOwner: true));
        var names = result.Select(t => t.Descriptor.Name).ToArray();

        Assert.Contains("web_navigate", names);
        Assert.Contains("system", names);
    }

    [Fact]
    public void Apply_WebProfile_IncludesWebMessagingAndSystemOnly()
    {
        var tools = new ITool[]
        {
            new StubTool("web_navigate", "group:web"),
            new StubTool("send_agent_message", "group:messaging"),
            new StubTool("current_time", "group:system"),
            new StubTool("read_file", "group:fs"),
            new StubTool("invoke_agent", "group:agents")
        };

        var result = ToolPolicyEvaluator.Apply(tools, new ToolSelectionPolicy(ToolProfile.Web, SenderIsOwner: true));
        var names = result.Select(t => t.Descriptor.Name).ToArray();

        Assert.Contains("web_navigate", names);
        Assert.Contains("send_agent_message", names);
        Assert.Contains("current_time", names);
        Assert.DoesNotContain("read_file", names);
        Assert.DoesNotContain("invoke_agent", names);
    }

    [Fact]
    public void Apply_CodingProfile_IncludesAgentGroup()
    {
        var tools = new ITool[]
        {
            new StubTool("invoke_agent", "group:agents"),
            new StubTool("system", "group:system")
        };

        var result = ToolPolicyEvaluator.Apply(tools, new ToolSelectionPolicy(ToolProfile.Coding, SenderIsOwner: true));
        var names = result.Select(t => t.Descriptor.Name).ToArray();

        Assert.Contains("invoke_agent", names);
        Assert.Contains("system", names);
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
