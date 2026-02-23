using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class InMemoryAgentRegistryTests
{
    [Fact]
    public void Register_And_Get_ReturnsSameDefinition()
    {
        var registry = new InMemoryAgentRegistry();
        var agent = CreateAgent("coder");

        registry.Register(agent);

        var retrieved = registry.Get("coder");
        Assert.NotNull(retrieved);
        Assert.Equal("coder", retrieved.AgentId);
        Assert.Equal(agent.DisplayName, retrieved.DisplayName);
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var registry = new InMemoryAgentRegistry();

        Assert.Null(registry.Get("nonexistent"));
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgent("Coder"));

        Assert.NotNull(registry.Get("coder"));
        Assert.NotNull(registry.Get("CODER"));
    }

    [Fact]
    public void GetAll_ReturnsAllRegistered()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgent("coder"));
        registry.Register(CreateAgent("reviewer"));

        var all = registry.GetAll();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Register_SameId_OverwritesPrevious()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgent("coder", displayName: "Coder v1"));
        registry.Register(CreateAgent("coder", displayName: "Coder v2"));

        var all = registry.GetAll();
        Assert.Single(all);
        Assert.Equal("Coder v2", all[0].DisplayName);
    }

    private static AgentDefinition CreateAgent(string id, string displayName = "Test Agent")
    {
        return new AgentDefinition(
            AgentId: id,
            DisplayName: displayName,
            Description: $"A test agent: {id}",
            SystemPrompt: "You are a test agent.",
            ToolPolicy: new ToolSelectionPolicy(ToolProfile.Coding));
    }
}
