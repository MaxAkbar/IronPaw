using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class InvokeAgentToolTests
{
    [Fact]
    public async Task ExecuteAsync_UnknownAgent_ReturnsError()
    {
        var registry = new InMemoryAgentRegistry();
        var orchestrator = new StubOrchestrator("unused");
        var tool = new InvokeAgentTool(registry, orchestrator);

        var call = CreateToolCall(new { agent_id = "nonexistent", task = "do something" });
        var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ExceedsMaxDepth_ReturnsError()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder"));
        var orchestrator = new StubOrchestrator("unused");
        var tool = new InvokeAgentTool(registry, orchestrator, maxDepth: 2);

        // SubagentDepth = 2, maxDepth = 2 -> childDepth = 3 > maxDepth
        var context = CreateContext(subagentDepth: 2);
        var call = CreateToolCall(new { agent_id = "coder", task = "do something" });
        var result = await tool.ExecuteAsync(call, context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("depth", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Success_ReturnsChildFinalMessage()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder"));
        var orchestrator = new StubOrchestrator("I refactored the code.");
        var tool = new InvokeAgentTool(registry, orchestrator);

        var call = CreateToolCall(new { agent_id = "coder", task = "refactor UserService" });
        var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("I refactored the code.", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ChildSessionKey_IsEphemeral()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder"));
        SessionKey? capturedSessionKey = null;
        var orchestrator = new StubOrchestrator("done", onRun: req => capturedSessionKey = req.SessionKey);
        var tool = new InvokeAgentTool(registry, orchestrator);

        var parentContext = CreateContext();
        var call = CreateToolCall(new { agent_id = "coder", task = "do work" });
        await tool.ExecuteAsync(call, parentContext, CancellationToken.None);

        Assert.NotNull(capturedSessionKey);
        Assert.Contains("coder", capturedSessionKey.Value.Value);
        Assert.NotEqual(parentContext.SessionKey, capturedSessionKey.Value);
    }

    [Fact]
    public async Task ExecuteAsync_ChildRequest_IncrementsSubagentDepth()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder"));
        AgentRunRequest? capturedRequest = null;
        var orchestrator = new StubOrchestrator("done", onRun: req => capturedRequest = req);
        var tool = new InvokeAgentTool(registry, orchestrator);

        var parentContext = CreateContext(subagentDepth: 1);
        var call = CreateToolCall(new { agent_id = "coder", task = "do work" });
        await tool.ExecuteAsync(call, parentContext, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(2, capturedRequest.ToolPolicy.SubagentDepth);
        Assert.Equal("coder", capturedRequest.AgentId);
        Assert.NotNull(capturedRequest.ParentRunId);
    }

    [Fact]
    public async Task ExecuteAsync_ChildRequest_PropagatesScopedParentLineage()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder"));
        AgentRunRequest? capturedRequest = null;
        var orchestrator = new StubOrchestrator("done", onRun: req => capturedRequest = req);
        var tool = new InvokeAgentTool(registry, orchestrator);

        var parentContext = new ToolExecutionContext(
            RunId: new RunId("child-run"),
            SessionKey: new SessionKey("child-session"),
            Provider: "local",
            Model: "rule-based-v1",
            Caller: CallerIdentity.LocalOwner(),
            SubagentDepth: 1,
            AgentId: "supervisor",
            ParentRunId: new RunId("root-run"),
            ParentSessionKey: new SessionKey("root-session"));

        var call = CreateToolCall(new { agent_id = "coder", task = "do work" });
        await tool.ExecuteAsync(call, parentContext, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.ParentRunId);
        Assert.Equal("root-run", capturedRequest.ParentRunId!.Value.Value);
        Assert.NotNull(capturedRequest.ParentSessionKey);
        Assert.Equal("root-session", capturedRequest.ParentSessionKey!.Value.Value);
    }

    [Fact]
    public async Task ExecuteAsync_MissingAgentId_ReturnsError()
    {
        var registry = new InMemoryAgentRegistry();
        var orchestrator = new StubOrchestrator("unused");
        var tool = new InvokeAgentTool(registry, orchestrator);

        var call = CreateToolCall(new { task = "do something" });
        var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("agent_id", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_MissingTask_ReturnsError()
    {
        var registry = new InMemoryAgentRegistry();
        var orchestrator = new StubOrchestrator("unused");
        var tool = new InvokeAgentTool(registry, orchestrator);

        var call = CreateToolCall(new { agent_id = "coder" });
        var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("task", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_UsesAgentProviderOverride()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder", provider: "anthropic", model: "claude-3"));
        AgentRunRequest? capturedRequest = null;
        var orchestrator = new StubOrchestrator("done", onRun: req => capturedRequest = req);
        var tool = new InvokeAgentTool(registry, orchestrator);

        var call = CreateToolCall(new { agent_id = "coder", task = "do work" });
        await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("anthropic", capturedRequest.Provider);
        Assert.Equal("claude-3", capturedRequest.Model);
    }

    [Fact]
    public async Task ExecuteAsync_InheritsParentProviderWhenAgentHasNoOverride()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder")); // No provider/model override
        AgentRunRequest? capturedRequest = null;
        var orchestrator = new StubOrchestrator("done", onRun: req => capturedRequest = req);
        var tool = new InvokeAgentTool(registry, orchestrator);

        var parentContext = new ToolExecutionContext(
            RunId.New(),
            new SessionKey("parent"),
            "openai",
            "gpt-4",
            CallerIdentity.LocalOwner());

        var call = CreateToolCall(new { agent_id = "coder", task = "do work" });
        await tool.ExecuteAsync(call, parentContext, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("openai", capturedRequest.Provider);
        Assert.Equal("gpt-4", capturedRequest.Model);
    }

    private static ToolExecutionContext CreateContext(int subagentDepth = 0)
    {
        return new ToolExecutionContext(
            RunId.New(),
            new SessionKey("test-session"),
            "local",
            "rule-based-v1",
            CallerIdentity.LocalOwner(),
            SubagentDepth: subagentDepth);
    }

    private static ToolCall CreateToolCall(object args)
    {
        return new ToolCall(
            Guid.NewGuid().ToString("n"),
            "invoke_agent",
            JsonSerializer.SerializeToElement(args));
    }

    private static AgentDefinition CreateAgentDef(
        string id,
        string? provider = null,
        string? model = null)
    {
        return new AgentDefinition(
            AgentId: id,
            DisplayName: $"Test {id}",
            Description: $"A test agent: {id}",
            SystemPrompt: $"You are the {id} agent.",
            ToolPolicy: new ToolSelectionPolicy(ToolProfile.Coding),
            Provider: provider,
            Model: model);
    }

    private sealed class StubOrchestrator : IAgentOrchestrator
    {
        private readonly string _response;
        private readonly Action<AgentRunRequest>? _onRun;

        public StubOrchestrator(string response, Action<AgentRunRequest>? onRun = null)
        {
            _response = response;
            _onRun = onRun;
        }

        public Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentEventSink sink, CancellationToken ct)
        {
            _onRun?.Invoke(request);

            return Task.FromResult(new AgentRunResult(
                RunId.New(),
                request.SessionKey,
                _response,
                [],
                new Dictionary<string, object?>
                {
                    ["rounds"] = 1,
                    ["toolCount"] = 0,
                    ["totalTokens"] = 100L
                }));
        }
    }
}
