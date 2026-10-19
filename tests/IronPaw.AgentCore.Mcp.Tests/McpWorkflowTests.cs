using System.Runtime.CompilerServices;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;
using static IronPaw.AgentCore.Mcp.Tests.McpConnectorTests;

namespace IronPaw.AgentCore.Mcp.Tests;

public sealed class McpWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegatedCallerCannotWidenTheRootConnectorGrant(bool rootGrant)
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        using var scope = new DurableScope();
        var delegateCall = new LlmDelta[] { new ToolCallDelta("delegate", "invoke_agent", McpConnectorTests.Json("""{"agent_id":"connector_worker","task":"Record an effect"}""")), new CompletedDelta() };
        await scope.Open(connector, new ScriptedClient(delegateCall, ToolResponse(connector), Answer, Answer));
        if (!rootGrant) scope.Request = scope.Request with { ToolPolicy = new(ToolProfile.Full) };
        var agents = new InMemoryAgentRegistry();
        agents.Register(new("connector_worker", "Worker", "Use the reviewed connector", "Use the requested tool", Policy(connector)));
        scope.Registry.RegisterTools([new InvokeAgentTool(agents, scope.Runtime)]);
        var result = await scope.Runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, default);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(rootGrant ? 1 : 0, fixture.Calls);
        Assert.Equal(rootGrant ? 1 : 0, fixture.Connections);
        var child = Assert.Single(await scope.Store.ListAsync(default), run => run.ParentRunId is not null);
        Assert.Equal(scope.Request.Caller, child.Request.Caller);
        Assert.Equal(result.RunId.Value, child.RootRunId);
        Assert.Equal(connector.ToolNames, child.Request.ToolPolicy.AllowedMcpTools);
        Assert.Equal(rootGrant ? connector.ToolNames : null, child.Request.ToolPolicy.InheritedPolicy!.AllowedMcpTools);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedResultAndConnectorGrantSurvivePhysicalDatabaseReopenWithoutAnyConnection(bool isError)
    {
        await using var fixture = new McpFixture(Schema);
        fixture.Execute = (_, _) => Task.FromResult<object?>(new { isError, content = new[] { new { type = "text", text = "Established result" } } });
        await using var connector = new McpConnector(Options, fixture.Transport);
        using var scope = new DurableScope();
        await scope.Open(connector, new ScriptedClient(ToolResponse(connector), Answer));
        var result = await scope.Runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, default);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        var saved = (await scope.Store.GetAsync(result.RunId, default))!;
        Assert.Equal(1, saved.Usage!.ToolCalls);
        var execution = Assert.Single(saved.ToolExecutions!);
        Assert.Equal(WorkflowToolState.Completed, execution.State);
        Assert.Equal(ContentTrust.Untrusted, execution.Result!.Trust);
        Assert.Equal(isError, execution.Result.IsError);
        Assert.Contains(result.Transcript, message => message.Role == ChatRole.Tool &&
            message.Content.StartsWith("[UNTRUSTED EXTERNAL CONTENT from " + connector.ToolNames[0], StringComparison.Ordinal));
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(connector.ToolNames, saved.Request.ToolPolicy.AllowedMcpTools);
        Assert.Contains(await scope.Store.ReadJournalAsync(result.RunId, default), e => e.EventType == "tool_completed");
        await using var reopened = new McpConnector(Options, () => throw new InvalidOperationException("Replay must not connect"));
        await scope.Open(reopened, new ScriptedClient());
        var replay = await scope.Runtime.ResumeAsync(result.RunId, scope.Request.Caller, NullAgentEventSink.Instance, default);
        Assert.Equal(result.FinalAssistantMessage, replay.FinalAssistantMessage);
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(1, fixture.Connections);
    }

    [Fact]
    public async Task PartialReplayUsesRecordedToolResponseWithoutReconnecting()
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        using var scope = new DurableScope();
        await scope.Open(connector, new ScriptedClient(ToolResponse(connector)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, default));
        var saved = (await scope.Store.GetAsync(scope.Request.RequestedRunId!.Value, default))!;
        Assert.Equal(WorkflowStatus.Interrupted, saved.Status);
        Assert.Equal(WorkflowToolState.Completed, Assert.Single(saved.ToolExecutions!).State);
        await using var reopened = new McpConnector(Options, () => throw new InvalidOperationException("Recorded tool must not connect"));
        await scope.Open(reopened, new ScriptedClient(Answer));
        var result = await scope.Runtime.ResumeAsync(scope.Request.RequestedRunId.Value, scope.Request.Caller, NullAgentEventSink.Instance, default);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(1, fixture.Connections);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HumanApprovalIsDurableAndRequiredBeforeTransportInitialization(bool approved)
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        using var scope = new DurableScope();
        await scope.Open(connector, new ScriptedClient(ToolResponse(connector), Answer));
        scope.Request = scope.Request with { ApprovalRequiredTools = connector.ToolNames };
        var waiting = await scope.Runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, default);
        Assert.Equal(AgentRunStatus.AwaitingInput, waiting.Status);
        Assert.Equal(0, fixture.Connections);
        var saved = (await scope.Store.GetAsync(waiting.RunId, default))!;
        Assert.Equal(WorkflowToolState.Planned, Assert.Single(saved.ToolExecutions!).State);
        var approval = Assert.Single(saved.HumanRequests!);
        Assert.NotNull(approval.OperationFingerprint);
        Assert.Equal(WorkflowHumanRequestKind.ToolApproval, approval.Kind);
        await scope.Runtime.DecideApprovalAsync(waiting.RunId, approval.RequestId, approved, scope.Request.Caller, default);
        var result = await scope.Runtime.ResumeAsync(waiting.RunId, scope.Request.Caller, NullAgentEventSink.Instance, default);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(approved ? 1 : 0, fixture.Calls);
        Assert.Equal(approved ? 1 : 0, fixture.Connections);
    }

    [Fact]
    public async Task PersistentToolBudgetStopsBeforeConnectorInitialization()
    {
        await using var fixture = new McpFixture(Schema);
        await using var connector = new McpConnector(Options, fixture.Transport);
        using var scope = new DurableScope();
        await scope.Open(connector, new ScriptedClient(ToolResponse(connector)));
        scope.Request = scope.Request with { Budget = new(MaxToolCalls: 0) };
        var result = await scope.Runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, default);
        Assert.Equal(AgentRunStatus.BudgetExhausted, result.Status);
        Assert.Equal(0, fixture.Connections);
        Assert.Equal(0, (await scope.Store.GetAsync(result.RunId, default))!.Usage!.ToolCalls);
    }

    [Fact]
    public async Task ChangedTransportBindingCannotInheritAPendingApprovalOrResumeWithSavedModelInputs()
    {
        await using var fixture = new McpFixture(Schema);
        await using var original = new McpConnector(Options, fixture.Transport);
        using var scope = new DurableScope();
        await scope.Open(original, new ScriptedClient(ToolResponse(original)));
        scope.Request = scope.Request with { ApprovalRequiredTools = original.ToolNames };
        var waiting = await scope.Runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, default);
        var saved = (await scope.Store.GetAsync(waiting.RunId, default))!;
        await scope.Runtime.DecideApprovalAsync(waiting.RunId, Assert.Single(saved.HumanRequests!).RequestId, true, scope.Request.Caller, default);
        await using var changed = new McpConnector(Options with { BindingRevision = "other-reviewed-endpoint" }, fixture.Transport);
        await scope.Open(changed, new ScriptedClient());
        await Assert.ThrowsAsync<WorkflowConflictException>(() => scope.Runtime.ResumeAsync(waiting.RunId, scope.Request.Caller, NullAgentEventSink.Instance, default));
        Assert.Equal(0, fixture.Connections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostOrCancelledResponseRequiresReconciliationAndNeverRepeatsTheEffect(bool cancel)
    {
        await using var fixture = new McpFixture(Schema);
        fixture.Execute = async (_, ct) => {
            if (cancel) await Task.Delay(Timeout.Infinite, ct);
            return null;
        };
        await using var connector = new McpConnector(Options, fixture.Transport);
        using var scope = new DurableScope();
        await scope.Open(connector, new ScriptedClient(ToolResponse(connector), Answer));
        using var cancellation = new CancellationTokenSource();
        var pending = scope.Runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, cancellation.Token);
        await fixture.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel) cancellation.Cancel();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        else await Assert.ThrowsAsync<WorkflowReconciliationRequiredException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        var saved = (await scope.Store.GetAsync(scope.Request.RequestedRunId!.Value, default))!;
        Assert.Equal(WorkflowStatus.NeedsReconciliation, saved.Status);
        var execution = Assert.Single(saved.ToolExecutions!);
        Assert.Equal(WorkflowToolState.Started, execution.State);
        Assert.Null(execution.Result);
        Assert.Equal(1, saved.Usage!.ToolCalls);
        await using var reopened = new McpConnector(Options, () => throw new InvalidOperationException("Unknown effect must not reconnect"));
        await scope.Open(reopened, new ScriptedClient(Answer));
        await Assert.ThrowsAsync<WorkflowReconciliationRequiredException>(() => scope.Runtime.ResumeAsync(new(saved.RunId), scope.Request.Caller, NullAgentEventSink.Instance, default));
        await scope.Runtime.ReconcileToolAsync(new(saved.RunId), execution.ExecutionId,
            ToolResult.Success("Effect established outside the connector", ContentTrust.Untrusted), "Checked the fixture receipt", scope.Request.Caller, default);
        var result = await scope.Runtime.ResumeAsync(new(saved.RunId), scope.Request.Caller, NullAgentEventSink.Instance, default);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(1, fixture.Connections);
    }

    private static LlmDelta[] ToolResponse(McpConnector connector) => [new ToolCallDelta("call-1", connector.ToolNames[0], McpConnectorTests.Json("""{"value":2}""")), new CompletedDelta()];
    private static readonly LlmDelta[] Answer = [new AssistantTextDelta("Finished"), new CompletedDelta()];
    private sealed class ScriptedClient(params LlmDelta[][] responses) : ILlmClient
    {
        private int _calls;
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Assert.DoesNotContain("UNTRUSTED SERVER INSTRUCTIONS", request.SystemPrompt);
            if (_calls >= responses.Length) throw new InvalidOperationException("Simulated local model interruption");
            foreach (var delta in responses[_calls++]) { ct.ThrowIfCancellationRequested(); yield return delta; }
        }
    }

    private sealed class DurableScope : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("IronPaw-mcp-");
        private SessionLaneScheduler? _scheduler;
        public CSharpDbWorkflowStore Store { get; private set; } = null!;
        public AgentOrchestrator Runtime { get; private set; } = null!;
        public InMemoryToolRegistry Registry { get; private set; } = null!;
        public AgentRunRequest Request { get; set; } = new(new("mcp-workflow"), "Record the requested effect", "local", "fixture",
            new(ToolProfile.Full), CallerIdentity.LocalOwner(), SystemPromptOverride: "Use the requested tool", RequestedRunId: RunId.New());
        public async Task Open(McpConnector connector, ILlmClient client)
        {
            _scheduler?.Dispose();
            Store?.Dispose();
            _scheduler = new();
            Store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(_directory.FullName, "workflows.cdb"));
            if (Request.ToolPolicy.AllowedMcpTools is null) Request = Request with { ToolPolicy = Policy(connector) };
            Registry = new(connector.Tools);
            Runtime = new(_scheduler, new FileSessionStore(new(Path.Combine(_directory.FullName, "sessions.json"), TimeSpan.Zero)),
                new JsonTranscriptStore(Path.Combine(_directory.FullName, "transcripts")), Registry,
                new ToolExecutor(), new ToolSchemaNormalizer(), client, new TranscriptSanitizer(), new ToolResultTruncator(),
                new DefaultSystemPromptBuilder(), workflowStore: Store);
        }
        public void Dispose()
        {
            _scheduler?.Dispose();
            Store?.Dispose();
            var target = Path.GetFullPath(_directory.FullName);
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!target.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, comparison) ||
                !Path.GetFileName(target).StartsWith("IronPaw-mcp-", StringComparison.Ordinal))
                throw new InvalidOperationException("MCP fixture cleanup escaped its private temporary directory.");
            Directory.Delete(target, recursive: true);
        }
    }
}
