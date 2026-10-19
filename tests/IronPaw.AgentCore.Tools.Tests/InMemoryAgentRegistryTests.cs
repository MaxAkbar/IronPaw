using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tools.Tests;

[TestClass]
public sealed class InMemoryAgentRegistryTests
{
    [TestMethod]
    public void Register_And_Get_ReturnsSameDefinition()
    {
        var registry = new InMemoryAgentRegistry();
        var agent = CreateAgent("coder");

        registry.Register(agent);

        var retrieved = registry.Get("coder");
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("coder", retrieved.AgentId);
        Assert.AreEqual(agent.DisplayName, retrieved.DisplayName);
    }

    [TestMethod]
    public void Get_UnknownId_ReturnsNull()
    {
        var registry = new InMemoryAgentRegistry();

        Assert.IsNull(registry.Get("nonexistent"));
    }

    [TestMethod]
    public void Get_IsCaseInsensitive()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgent("Coder"));

        Assert.IsNotNull(registry.Get("coder"));
        Assert.IsNotNull(registry.Get("CODER"));
    }

    [TestMethod]
    public void GetAll_ReturnsAllRegistered()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgent("coder"));
        registry.Register(CreateAgent("reviewer"));

        var all = registry.GetAll();
        Assert.AreEqual(2, all.Count);
    }

    [TestMethod]
    public void Register_SameId_OverwritesPrevious()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgent("coder", displayName: "Coder v1"));
        registry.Register(CreateAgent("coder", displayName: "Coder v2"));

        var all = registry.GetAll();
        Assert.ContainsSingle(all);
        Assert.AreEqual("Coder v2", all[0].DisplayName);
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
