using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tools.Tests;

[TestClass]
public sealed class InvokeAgentToolTests
{
    [TestMethod]
    public async Task ExecuteAsync_UnknownAgent_ReturnsError()
    {
        var registry = new InMemoryAgentRegistry();
        var orchestrator = new StubOrchestrator("unused");
        var tool = new InvokeAgentTool(registry, orchestrator);

        var call = CreateToolCall(new { agent_id = "nonexistent", task = "do something" });
        var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.Contains("not found", result.Content);
    }

    [TestMethod]
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

        Assert.IsTrue(result.IsError);
        Assert.Contains("depth", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task ExecuteAsync_Success_ReturnsChildFinalMessage()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder"));
        var orchestrator = new StubOrchestrator("I refactored the code.");
        var tool = new InvokeAgentTool(registry, orchestrator);

        var call = CreateToolCall(new { agent_id = "coder", task = "refactor UserService" });
        var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

        Assert.IsFalse(result.IsError);
        Assert.AreEqual("I refactored the code.", result.Content);
    }

    [TestMethod]
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

        Assert.IsNotNull(capturedSessionKey);
        Assert.Contains("coder", capturedSessionKey.Value.Value);
        Assert.AreNotEqual(parentContext.SessionKey, capturedSessionKey.Value);
    }

    [TestMethod]
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

        Assert.IsNotNull(capturedRequest);
        Assert.AreEqual(2, capturedRequest.ToolPolicy.SubagentDepth);
        Assert.AreEqual("coder", capturedRequest.AgentId);
        Assert.IsNotNull(capturedRequest.ParentRunId);
    }

    [TestMethod]
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

        Assert.IsNotNull(capturedRequest);
        Assert.IsNotNull(capturedRequest.ParentRunId);
        Assert.AreEqual("child-run", capturedRequest.ParentRunId!.Value.Value);
        Assert.AreEqual("root-run", capturedRequest.RootRunId!.Value.Value);
        Assert.IsNotNull(capturedRequest.ParentSessionKey);
        Assert.AreEqual("root-session", capturedRequest.ParentSessionKey!.Value.Value);
    }

    [TestMethod]
    public async Task ExecuteAsync_MissingAgentId_ReturnsError()
    {
        var registry = new InMemoryAgentRegistry();
        var orchestrator = new StubOrchestrator("unused");
        var tool = new InvokeAgentTool(registry, orchestrator);

        var call = CreateToolCall(new { task = "do something" });
        var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.Contains("agent_id", result.Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_MissingTask_ReturnsError()
    {
        var registry = new InMemoryAgentRegistry();
        var orchestrator = new StubOrchestrator("unused");
        var tool = new InvokeAgentTool(registry, orchestrator);

        var call = CreateToolCall(new { agent_id = "coder" });
        var result = await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.Contains("task", result.Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_UsesAgentProviderOverride()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder", provider: "anthropic", model: "claude-3"));
        AgentRunRequest? capturedRequest = null;
        var orchestrator = new StubOrchestrator("done", onRun: req => capturedRequest = req);
        var tool = new InvokeAgentTool(registry, orchestrator);

        var call = CreateToolCall(new { agent_id = "coder", task = "do work" });
        await tool.ExecuteAsync(call, CreateContext(), CancellationToken.None);

        Assert.IsNotNull(capturedRequest);
        Assert.AreEqual("anthropic", capturedRequest.Provider);
        Assert.AreEqual("claude-3", capturedRequest.Model);
    }

    [TestMethod]
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

        Assert.IsNotNull(capturedRequest);
        Assert.AreEqual("openai", capturedRequest.Provider);
        Assert.AreEqual("gpt-4", capturedRequest.Model);
    }

    [TestMethod]
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
        Assert.IsNotNull(captured);
        var tools = BuiltInToolFactory.CreateDefault(Path.GetTempPath());
        var available = new InMemoryToolRegistry(tools).ResolveTools(captured.ToolPolicy);
        Assert.Contains(tool => tool.Descriptor.Name == "read_file", available);
        Assert.DoesNotContain(tool => tool.Descriptor.Name == "write_file", available);
        Assert.DoesNotContain(tool => tool.Descriptor.Group != "group:fs", available);
    }

    [TestMethod]
    public async Task ExecuteAsync_MissingParentPolicyFailsClosed()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder"));
        var invoked = false;
        var orchestrator = new StubOrchestrator("done", onRun: _ => invoked = true);
        var result = await new InvokeAgentTool(registry, orchestrator).ExecuteAsync(
            CreateToolCall(new { agent_id = "coder", task = "do work" }),
            CreateContext() with { ToolPolicy = null }, CancellationToken.None);
        Assert.IsTrue(result.IsError);
        Assert.IsFalse(invoked);
    }

    [TestMethod]
    public async Task ExecuteAsync_ForwardsChildEventsToParentSink()
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder"));
        var sink = new CapturingSink();
        await new InvokeAgentTool(registry, new StubOrchestrator("done")).ExecuteAsync(
            CreateToolCall(new { agent_id = "coder", task = "do work" }),
            CreateContext() with { EventSink = sink }, CancellationToken.None);
        Assert.AreEqual("coder", Assert.ContainsSingle(sink.Events).AgentId);
    }

    [TestMethod]
    [DataRow(AgentRunStatus.RoundLimitReached)]
    [DataRow(AgentRunStatus.Failed)]
    [DataRow(AgentRunStatus.Cancelled)]
    [DataRow(AgentRunStatus.AwaitingInput)]
    public async Task ExecuteAsync_IncompleteChildResultIsAnError(AgentRunStatus status)
    {
        var registry = new InMemoryAgentRegistry();
        registry.Register(CreateAgentDef("coder"));
        var result = await new InvokeAgentTool(registry, new StubOrchestrator("incomplete", status: status)).ExecuteAsync(
            CreateToolCall(new { agent_id = "coder", task = "do work" }), CreateContext(), CancellationToken.None);
        Assert.IsTrue(result.IsError);
        Assert.AreEqual(status.ToString(), result.Details!["status"]);
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
