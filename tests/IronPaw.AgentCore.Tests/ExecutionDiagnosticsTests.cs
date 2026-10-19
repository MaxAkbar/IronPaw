using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

[CollectionDefinition("Execution diagnostics", DisableParallelization = true)]
public sealed class ExecutionDiagnosticsCollection;

[Collection("Execution diagnostics")]
public sealed class ExecutionDiagnosticsTests
{
    [Fact]
    public async Task RealAdapterCallsHaveCorrelatedPayloadFreeTracesAndMetrics_ReplayAddsNoCalls()
    {
        using var probe = new Probe();
        await using var scope = await Scope.OpenAsync();
        var result = await scope.Runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, default);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(2, scope.Client.Calls);
        Assert.Equal(1, scope.Tool.Calls);
        var root = Assert.Single(probe.Spans, activity => activity.OperationName == "ironpaw.run");
        Assert.Equal(result.RunId.Value, root.GetTagItem("ironpaw.run.id"));
        Assert.Equal("completed", root.GetTagItem("ironpaw.outcome"));
        var tool = Assert.Single(probe.Spans, activity => activity.OperationName == "ironpaw.tool");
        var saved = (await scope.Store.GetAsync(result.RunId, default))!;
        Assert.Equal(saved.ToolExecutions![0].ExecutionId, tool.GetTagItem("ironpaw.tool.execution_id"));
        Assert.Equal(root.SpanId, tool.ParentSpanId);
        Assert.All(probe.Spans.Where(activity => activity.OperationName == "ironpaw.model"), activity => {
            Assert.Equal(root.TraceId, activity.TraceId); Assert.Equal(root.SpanId, activity.ParentSpanId);
        });
        Assert.Equal(2, probe.Count("model"));
        Assert.Equal(1, probe.Count("tool"));
        Assert.Equal(4, probe.Durations.Count);
        Assert.All(probe.Durations, seconds => Assert.True(seconds >= 0));
        Assert.Equal(8, probe.Measurements.Where(m => m.Name == "ironpaw.model.reported_tokens" && m.Tags["direction"] == "input").Sum(m => m.Value));
        Assert.All(probe.Measurements, measurement => Assert.All(measurement.Tags.Keys,
            key => Assert.Contains(key, new[] { "operation", "outcome", "direction" })));
        Assert.DoesNotContain(probe.Spans.SelectMany(activity => activity.Tags), tag => tag.Value?.Contains("PRIVATE", StringComparison.Ordinal) == true);
        Assert.All(probe.Measurements.Where(m => m.Name == "ironpaw.operations.active").GroupBy(m => m.Tags["operation"]), group => Assert.Equal(0, group.Sum(m => m.Value)));
        var measurements = probe.Measurements.Count;
        var replay = await scope.Runtime.ResumeAsync(result.RunId, scope.Request.Caller, NullAgentEventSink.Instance, default);
        Assert.Equal(result.RunId, replay.RunId);
        Assert.Equal(result.Status, replay.Status);
        Assert.Equal(result.FinalAssistantMessage, replay.FinalAssistantMessage);
        Assert.Equal(measurements, probe.Measurements.Count);
        Assert.Equal(2, scope.Client.Calls);
        Assert.Equal(1, scope.Tool.Calls);
    }

    [Theory]
    [InlineData("run_started", false)]
    [InlineData("assistant_delta", false)]
    [InlineData("tool_call", false)]
    [InlineData("tool_result", false)]
    [InlineData("run_started", true)]
    [InlineData("assistant_delta", true)]
    [InlineData("tool_call", true)]
    [InlineData("tool_result", true)]
    [InlineData("direct_tool", true)]
    public async Task IntermediateObserverErrorsCannotChangeDurableWorkOrCauseARepeat(string eventType, bool asyncFailure)
    {
        await using var scope = await Scope.OpenAsync();
        var sink = new DelegateSink(evt => {
            if (evt.Type != eventType) return ValueTask.CompletedTask;
            if (asyncFailure) return ValueTask.FromException(new InvalidOperationException("observer failed"));
            throw new InvalidOperationException("observer failed");
        });
        var result = await scope.Runtime.RunAsync(scope.Request, sink, default);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Contains("observer failed", result.Metadata["eventDeliveryError"]!.ToString());
        Assert.True((long)result.Metadata["eventDeliveryFailures"]! > 0);
        var checkpoint = (await scope.Store.GetAsync(result.RunId, default))!;
        Assert.Equal(WorkflowStatus.Succeeded, checkpoint.Status);
        Assert.Equal(WorkflowToolState.Completed, Assert.Single(checkpoint.ToolExecutions!).State);
        await scope.Runtime.ResumeAsync(result.RunId, scope.Request.Caller, sink, default);
        Assert.Equal(1, scope.Tool.Calls);
        Assert.Equal(2, scope.Client.Calls);
    }

    [Fact]
    public async Task DelegatedRunsShareTheParentTraceAndKeepTheirOwnInvocationIdentities()
    {
        using var probe = new Probe();
        var planner = new LlmDelta[] { new AssistantTextDelta("""{"needs_clarification":false,"delegations":[{"agent_id":"codegen","task":"Inspect the candidate"}]}"""), new CompletedDelta() };
        await using var scope = await Scope.OpenAsync(planner,
            [new AssistantTextDelta("Child finished"), new CompletedDelta()], [new AssistantTextDelta("Root finished"), new CompletedDelta()]);
        var agents = new InMemoryAgentRegistry();
        agents.Register(new("codegen", "Coder", "Inspects candidates", "Inspect the candidate", new(ToolProfile.Coding)));
        scope.Tools.RegisterTools([new ListAgentsTool(agents), new InvokeAgentTool(agents, scope.Runtime)]);
        var result = await scope.Runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, default);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        var root = Assert.Single(probe.Spans, span => span.OperationName == "ironpaw.run" && Equals(span.GetTagItem("ironpaw.run.id"), result.RunId.Value));
        var child = Assert.Single(probe.Spans, span => span.OperationName == "ironpaw.run" && Equals(span.GetTagItem("ironpaw.agent.id"), "codegen"));
        Assert.Equal(root.TraceId, child.TraceId);
        Assert.Equal(result.RunId.Value, child.GetTagItem("ironpaw.parent_run.id"));
        Assert.Equal(result.RunId.Value, child.GetTagItem("ironpaw.workflow.root_id"));
        Assert.Contains(probe.Spans, span => span.OperationName == "ironpaw.tool" && span.SpanId == child.ParentSpanId);
        var savedChild = Assert.Single(await scope.Store.ListAsync(default), run => run.ParentRunId is not null);
        Assert.Equal(savedChild.RunId, child.GetTagItem("ironpaw.run.id"));
        Assert.Equal(2, probe.Count("run"));
        Assert.Equal(3, probe.Count("model"));
    }

    [Fact]
    public async Task CancellationEscapesAnUncooperativeAsyncObserverBeforeAnyProviderWork()
    {
        using var probe = new Probe();
        await using var scope = await Scope.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new DelegateSink(evt => {
            if (evt.Type != "run_started") return ValueTask.CompletedTask;
            entered.TrySetResult(); return new(release.Task);
        });
        var pending = scope.Runtime.RunAsync(scope.Request, sink, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { release.TrySetResult(); }
        Assert.Equal(0, scope.Client.Calls);
        Assert.Equal(0, scope.Tool.Calls);
        Assert.Null((await scope.Store.GetAsync(scope.Request.RequestedRunId!.Value, default))!.LeaseOwner);
        Assert.Equal("cancelled", Assert.Single(probe.Spans, span => span.OperationName == "ironpaw.run").GetTagItem("ironpaw.outcome"));
    }

    [Fact]
    public async Task ThrowingTelemetryListenersCannotFailExecutionOrLeakAmbientActivities()
    {
        using var probe = new Probe();
        await using var scope = await Scope.OpenAsync();
        using var parent = new Activity("test-parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var activityFailures = new ActivityListener {
            ShouldListenTo = source => source.Name == ExecutionDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => throw new InvalidOperationException("bad trace observer"),
            ActivityStopped = _ => throw new InvalidOperationException("bad trace observer")
        };
        ActivitySource.AddActivityListener(activityFailures);
        using var metricFailures = new MeterListener { InstrumentPublished = (instrument, listener) => {
            if (instrument.Meter.Name == ExecutionDiagnostics.MeterName) listener.EnableMeasurementEvents(instrument);
        } };
        metricFailures.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("bad metric observer"));
        metricFailures.SetMeasurementEventCallback<double>((_, _, _, _) => throw new InvalidOperationException("bad metric observer"));
        metricFailures.Start();
        var result = await scope.Runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, default);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(2, scope.Client.Calls);
        Assert.Equal(1, scope.Tool.Calls);
        Assert.Same(parent, Activity.Current);
    }

    [Fact]
    public async Task ProviderFailureIsRecordedAsFailureWithoutAnAcceptedOrCompletedRun()
    {
        using var probe = new Probe();
        await using var scope = await Scope.OpenAsync([new AssistantTextDelta("PRIVATE partial"), new LlmErrorDelta("PRIVATE failure")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, default));
        Assert.Equal("failed", Assert.Single(probe.Spans, span => span.OperationName == "ironpaw.run").GetTagItem("ironpaw.outcome"));
        Assert.Equal("failed", Assert.Single(probe.Spans, span => span.OperationName == "ironpaw.model").GetTagItem("ironpaw.outcome"));
        Assert.NotEqual(WorkflowStatus.Succeeded, (await scope.Store.GetAsync(scope.Request.RequestedRunId!.Value, default))!.Status);
        Assert.DoesNotContain(probe.Spans.SelectMany(span => span.Tags), tag => tag.Value?.Contains("PRIVATE", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(TaskAcceptanceOutcome.Accepted, "accepted")]
    [InlineData(TaskAcceptanceOutcome.Rejected, "failed")]
    [InlineData(TaskAcceptanceOutcome.Inconclusive, "inconclusive")]
    public async Task TaskSpansUseTheSavedHostDecisionInsteadOfTheExecutorCompletionClaim(TaskAcceptanceOutcome decision, string expected)
    {
        using var probe = new Probe();
        await using var scope = await Scope.OpenAsync();
        var graph = new WorkflowTaskGraph("observed-graph", "Observe trusted acceptance", [new("task", "Inspect the candidate") {
            AcceptanceCriteria = [new("accepted", "A trusted host inspects the candidate")]
        }]);
        var scheduler = new TaskGraphScheduler(scope.Store, new GraphExecutor(), new GraphVerifier(decision));
        await scheduler.StartAsync(graph, scope.Request.Caller, 1, default);
        var span = Assert.Single(probe.Spans, activity => activity.OperationName == "ironpaw.task");
        Assert.Equal(expected, span.GetTagItem("ironpaw.outcome"));
        Assert.Equal("observed-graph", span.GetTagItem("ironpaw.graph.id"));
        Assert.NotNull(span.GetTagItem("ironpaw.task.attempt_id"));
        var graphSpan = Assert.Single(probe.Spans, activity => activity.OperationName == "ironpaw.graph");
        Assert.Equal(graphSpan.TraceId, span.TraceId);
        Assert.Equal(graphSpan.SpanId, span.ParentSpanId);
    }

    private sealed class DelegateSink(Func<AgentEvent, ValueTask> publish) : IAgentEventSink
    { public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct) => publish(evt); }
    private sealed class GraphExecutor : IWorkflowTaskExecutor
    {
        public Task<WorkflowTaskResult> ExecuteAsync(TaskGraphClaim claim, CancellationToken ct) => Task.FromResult(new WorkflowTaskResult(
            claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId, WorkflowTaskStatus.Succeeded, "candidate", "Executor claims completion") {
                VerificationEvidence = [new("evidence", "accepted", "candidate", TaskVerificationOutcome.Passed, "Executor evidence claim")]
            });
    }
    private sealed class GraphVerifier(TaskAcceptanceOutcome outcome) : ITaskAcceptanceVerifier
    { public Task<TaskAcceptanceDecision> VerifyAsync(TaskGraphClaim claim, WorkflowTaskResult result, CancellationToken ct) => Task.FromResult(new TaskAcceptanceDecision(outcome, "host", "Independent decision")); }

    private sealed class Probe : IDisposable
    {
        private readonly ActivityListener _activities;
        private readonly MeterListener _metrics;
        public ConcurrentQueue<Activity> Spans { get; } = new();
        public ConcurrentQueue<double> Durations { get; } = new();
        public ConcurrentQueue<(string Name, long Value, Dictionary<string, string> Tags)> Measurements { get; } = new();
        public Probe()
        {
            _activities = new() { ShouldListenTo = source => source.Name == ExecutionDiagnostics.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = span => Spans.Enqueue(span) };
            ActivitySource.AddActivityListener(_activities);
            _metrics = new() { InstrumentPublished = (instrument, listener) => {
                if (instrument.Meter.Name == ExecutionDiagnostics.MeterName) listener.EnableMeasurementEvents(instrument);
            } };
            _metrics.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Measurements.Enqueue((instrument.Name, value,
                tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value?.ToString() ?? ""))));
            _metrics.SetMeasurementEventCallback<double>((_, value, _, _) => Durations.Enqueue(value));
            _metrics.Start();
        }
        public long Count(string operation) => Measurements.Where(m => m.Name == "ironpaw.operations" && m.Tags["operation"] == operation).Sum(m => m.Value);
        public void Dispose() { _metrics.Dispose(); _activities.Dispose(); }
    }
    private sealed class Scope : IAsyncDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("IronPaw-diagnostics-");
        private readonly SessionLaneScheduler _scheduler = new();
        public CSharpDbWorkflowStore Store { get; private set; } = null!;
        public ScriptedClient Client { get; private set; } = null!;
        public CountingTool Tool { get; } = new();
        public InMemoryToolRegistry Tools { get; private set; } = null!;
        public AgentOrchestrator Runtime { get; private set; } = null!;
        public AgentRunRequest Request { get; } = new(new("diagnostics"), "PRIVATE user input", "local", "PRIVATE model setting",
            new(ToolProfile.Coding), CallerIdentity.LocalOwner(), SystemPromptOverride: "PRIVATE system prompt", RequestedRunId: RunId.New());
        public static async Task<Scope> OpenAsync(LlmDelta[]? response = null, params LlmDelta[][] additional)
        {
            var scope = new Scope();
            scope.Store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(scope._directory.FullName, "workflows.cdb"));
            scope.Client = response is null ? new ScriptedClient(
                [new AssistantTextDelta("PRIVATE thinking"), new ToolCallDelta("call-1", "record_effect", JsonSerializer.SerializeToElement(new { secret = "PRIVATE argument" })), new UsageDelta(3, 2, 1, 1), new CompletedDelta()],
                [new AssistantTextDelta("PRIVATE final"), new UsageDelta(5, 1, 0, 0), new CompletedDelta()]) : new([response, .. additional]);
            scope.Tools = new([scope.Tool]);
            scope.Runtime = new(scope._scheduler,
                new FileSessionStore(new(Path.Combine(scope._directory.FullName, "sessions.json"), TimeSpan.Zero)),
                new JsonTranscriptStore(Path.Combine(scope._directory.FullName, "transcripts")), scope.Tools,
                new ToolExecutor(), new ToolSchemaNormalizer(), scope.Client, new TranscriptSanitizer(), new ToolResultTruncator(),
                new DefaultSystemPromptBuilder(), workflowStore: scope.Store);
            return scope;
        }
        public ValueTask DisposeAsync() { _scheduler.Dispose(); Store.Dispose(); _directory.Delete(true); return ValueTask.CompletedTask; }
    }
    private sealed class CountingTool : ITool
    {
        public int Calls;
        public ToolDescriptor Descriptor { get; } = new("record_effect", "Record an effect", JsonSerializer.SerializeToElement(new {
            type = "object", properties = new { secret = new { type = "string" } } }), "group:system");
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
        {
            Calls++;
            if (context.EventSink is { } sink) await sink.PublishAsync(new(context.RunId, context.SessionKey, AgentEventStream.Tool,
                "direct_tool", DateTimeOffset.UtcNow, EventData.Of()), ct);
            return ToolResult.Success("PRIVATE tool output");
        }
    }
    private sealed class ScriptedClient(params LlmDelta[][] responses) : ILlmClient
    {
        public int Calls;
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var delta in responses[Calls++]) { ct.ThrowIfCancellationRequested(); yield return delta; }
        }
    }
}
