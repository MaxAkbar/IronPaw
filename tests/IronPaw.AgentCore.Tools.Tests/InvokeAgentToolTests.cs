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
    public async Task ExecuteAsync_ChildRequest_PropagatesImmediateParentAndRootLineage()
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
            ParentSessionKey: new SessionKey("root-session"),
            ToolPolicy: new ToolSelectionPolicy(ToolProfile.Coding));

        var call = CreateToolCall(new { agent_id = "coder", task = "do work" });
        await tool.ExecuteAsync(call, parentContext, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.ParentRunId);
        Assert.Equal("child-run", capturedRequest.ParentRunId!.Value.Value);
        Assert.Equal("root-run", capturedRequest.RootRunId!.Value.Value);
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
            CallerIdentity.LocalOwner(),
            ToolPolicy: new ToolSelectionPolicy(ToolProfile.Coding));

        var call = CreateToolCall(new { agent_id = "coder", task = "do work" });
        await tool.ExecuteAsync(call, parentContext, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("openai", capturedRequest.Provider);
        Assert.Equal("gpt-4", capturedRequest.Model);
    }

    [Fact]
    public async Task ExecuteAsync_ChildPolicyPreservesParentRestrictions()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder") with { ToolPolicy = new ToolSelectionPolicy(ToolProfile.Full) });
        AgentRunRequest? captured = null;
        var orchestrator = new StubOrchestrator("done", onRun: request => captured = request);
        var parentPolicy = new ToolSelectionPolicy(ToolProfile.Coding, SenderIsOwner: true,
            DenyList: ["write_file"], AllowedGroups: ["group:fs"]);
        await new InvokeAgentTool(registry, orchestrator).ExecuteAsync(
            CreateToolCall(new { agent_id = "coder", task = "inspect only" }),
            CreateContext() with { ToolPolicy = parentPolicy }, CancellationToken.None);
        Assert.NotNull(captured);
        var tools = BuiltInToolFactory.CreateDefault(Path.GetTempPath());
        var available = new InMemoryToolRegistry(tools).ResolveTools(captured.ToolPolicy);
        Assert.Contains(available, tool => tool.Descriptor.Name == "read_file");
        Assert.DoesNotContain(available, tool => tool.Descriptor.Name == "write_file");
        Assert.DoesNotContain(available, tool => tool.Descriptor.Group != "group:fs");
    }

    [Fact]
    public async Task ExecuteAsync_MissingParentPolicyFailsClosed()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder"));
        var invoked = false;
        var orchestrator = new StubOrchestrator("done", onRun: _ => invoked = true);
        var result = await new InvokeAgentTool(registry, orchestrator).ExecuteAsync(
            CreateToolCall(new { agent_id = "coder", task = "do work" }),
            CreateContext() with { ToolPolicy = null }, CancellationToken.None);
        Assert.True(result.IsError);
        Assert.False(invoked);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsChildEventsToParentSink()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder"));
        var sink = new CapturingSink();
        await new InvokeAgentTool(registry, new StubOrchestrator("done")).ExecuteAsync(
            CreateToolCall(new { agent_id = "coder", task = "do work" }),
            CreateContext() with { EventSink = sink }, CancellationToken.None);
        Assert.Equal("coder", Assert.Single(sink.Events).AgentId);
    }

    [Theory]
    [InlineData(AgentRunStatus.RoundLimitReached)]
    [InlineData(AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.Cancelled)]
    [InlineData(AgentRunStatus.AwaitingInput)]
    public async Task ExecuteAsync_IncompleteChildResultIsAnError(AgentRunStatus status)
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder"));
        var result = await new InvokeAgentTool(registry, new StubOrchestrator("incomplete", status: status)).ExecuteAsync(
            CreateToolCall(new { agent_id = "coder", task = "do work" }), CreateContext(), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal(status.ToString(), result.Details!["status"]);
    }

    private sealed class CapturingSink : IAgentEventSink
    {
        public List<AgentEvent> Events { get; } = [];
        public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct)
        {
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }
    }

    private static ToolExecutionContext CreateContext(int subagentDepth = 0)
    {
        return new ToolExecutionContext(
            RunId.New(),
            new SessionKey("test-session"),
            "local",
            "rule-based-v1",
            CallerIdentity.LocalOwner(),
            SubagentDepth: subagentDepth,
            ToolPolicy: new ToolSelectionPolicy(ToolProfile.Coding));
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
        private readonly AgentRunStatus _status;

        public StubOrchestrator(string response, Action<AgentRunRequest>? onRun = null, AgentRunStatus status = AgentRunStatus.Succeeded)
        {
            _response = response;
            _onRun = onRun;
            _status = status;
        }

        public async Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentEventSink sink, CancellationToken ct)
        {
            _onRun?.Invoke(request);

            var runId = RunId.New();
            await sink.PublishAsync(new AgentEvent(runId, request.SessionKey, AgentEventStream.Lifecycle,
                "run_started", DateTimeOffset.UtcNow, EventData.Of()), ct);
            return new AgentRunResult(
                runId,
                request.SessionKey,
                _response,
                [],
                new Dictionary<string, object?>
                {
                    ["rounds"] = 1,
                    ["toolCount"] = 0,
                    ["totalTokens"] = 100L
                }, Status: _status);
        }
    }
}
