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

[TestClass]
[DoNotParallelize]
public sealed class ExecutionDiagnosticsTests
{
    [TestMethod]
    public async Task RealAdapterCallsHaveCorrelatedPayloadFreeTracesAndMetrics_ReplayAddsNoCalls()
    {
        using var probe = new Probe();
        await using var scope = await Scope.OpenAsync();
        var result = await scope.Runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, default);
        Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
        Assert.AreEqual(2, scope.Client.Calls);
        Assert.AreEqual(1, scope.Tool.Calls);
        var root = Assert.ContainsSingle(activity => activity.OperationName == "ironpaw.run", probe.Spans);
        Assert.AreEqual(result.RunId.Value, root.GetTagItem("ironpaw.run.id"));
        Assert.AreEqual("completed", root.GetTagItem("ironpaw.outcome"));
        var tool = Assert.ContainsSingle(activity => activity.OperationName == "ironpaw.tool", probe.Spans);
        var saved = (await scope.Store.GetAsync(result.RunId, default))!;
        Assert.AreEqual(saved.ToolExecutions![0].ExecutionId, tool.GetTagItem("ironpaw.tool.execution_id"));
        Assert.AreEqual(root.SpanId, tool.ParentSpanId);
        foreach (var activity in probe.Spans.Where(activity => activity.OperationName == "ironpaw.model"))
        {
            Assert.AreEqual(root.TraceId, activity.TraceId);
            Assert.AreEqual(root.SpanId, activity.ParentSpanId);
        }
        Assert.AreEqual(2, probe.Count("model"));
        Assert.AreEqual(1, probe.Count("tool"));
        Assert.AreEqual(4, probe.Durations.Count);
        foreach (var seconds in probe.Durations)
        {
            Assert.IsTrue(seconds >= 0);
        }
        Assert.AreEqual(8, probe.Measurements.Where(m => m.Name == "ironpaw.model.reported_tokens" && m.Tags["direction"] == "input").Sum(m => m.Value));
        foreach (var measurement in probe.Measurements)
        {
            foreach (var key in measurement.Tags.Keys)
            {
                Assert.Contains(key, new[] { "operation", "outcome", "direction" });
            }
        }
        Assert.DoesNotContain(tag => tag.Value?.Contains("PRIVATE", StringComparison.Ordinal) == true, probe.Spans.SelectMany(activity => activity.Tags));
        foreach (var group in probe.Measurements.Where(m => m.Name == "ironpaw.operations.active").GroupBy(m => m.Tags["operation"]))
        {
            Assert.AreEqual(0, group.Sum(m => m.Value));
        }
        var measurements = probe.Measurements.Count;
        var replay = await scope.Runtime.ResumeAsync(result.RunId, scope.Request.Caller, NullAgentEventSink.Instance, default);
        Assert.AreEqual(result.RunId, replay.RunId);
        Assert.AreEqual(result.Status, replay.Status);
        Assert.AreEqual(result.FinalAssistantMessage, replay.FinalAssistantMessage);
        Assert.AreEqual(measurements, probe.Measurements.Count);
        Assert.AreEqual(2, scope.Client.Calls);
        Assert.AreEqual(1, scope.Tool.Calls);
    }

    [TestMethod]
    [DataRow("run_started", false)]
    [DataRow("assistant_delta", false)]
    [DataRow("tool_call", false)]
    [DataRow("tool_result", false)]
    [DataRow("run_started", true)]
    [DataRow("assistant_delta", true)]
    [DataRow("tool_call", true)]
    [DataRow("tool_result", true)]
    [DataRow("direct_tool", true)]
    public async Task IntermediateObserverErrorsCannotChangeDurableWorkOrCauseARepeat(string eventType, bool asyncFailure)
    {
        await using var scope = await Scope.OpenAsync();
        var sink = new DelegateSink(evt =>
        {
            if (evt.Type != eventType) return ValueTask.CompletedTask;
            if (asyncFailure) return ValueTask.FromException(new InvalidOperationException("observer failed"));
            throw new InvalidOperationException("observer failed");
        });
        var result = await scope.Runtime.RunAsync(scope.Request, sink, default);
        Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
        var deliveryError = result.Metadata["eventDeliveryError"]!.ToString();
        Assert.IsNotNull(deliveryError);
        Assert.Contains("observer failed", deliveryError);
        Assert.IsTrue((long)result.Metadata["eventDeliveryFailures"]! > 0);
        var checkpoint = (await scope.Store.GetAsync(result.RunId, default))!;
        Assert.AreEqual(WorkflowStatus.Succeeded, checkpoint.Status);
        Assert.AreEqual(WorkflowToolState.Completed, Assert.ContainsSingle(checkpoint.ToolExecutions!).State);
        await scope.Runtime.ResumeAsync(result.RunId, scope.Request.Caller, sink, default);
        Assert.AreEqual(1, scope.Tool.Calls);
        Assert.AreEqual(2, scope.Client.Calls);
    }

    [TestMethod]
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
        Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
        var root = Assert.ContainsSingle(span => span.OperationName == "ironpaw.run" && Equals(span.GetTagItem("ironpaw.run.id"), result.RunId.Value), probe.Spans);
        var child = Assert.ContainsSingle(span => span.OperationName == "ironpaw.run" && Equals(span.GetTagItem("ironpaw.agent.id"), "codegen"), probe.Spans);
        Assert.AreEqual(root.TraceId, child.TraceId);
        Assert.AreEqual(result.RunId.Value, child.GetTagItem("ironpaw.parent_run.id"));
        Assert.AreEqual(result.RunId.Value, child.GetTagItem("ironpaw.workflow.root_id"));
        Assert.Contains(span => span.OperationName == "ironpaw.tool" && span.SpanId == child.ParentSpanId, probe.Spans);
        var savedChild = Assert.ContainsSingle(run => run.ParentRunId is not null, await scope.Store.ListAsync(default));
        Assert.AreEqual(savedChild.RunId, child.GetTagItem("ironpaw.run.id"));
        Assert.AreEqual(2, probe.Count("run"));
        Assert.AreEqual(3, probe.Count("model"));
    }

    [TestMethod]
    public async Task CancellationEscapesAnUncooperativeAsyncObserverBeforeAnyProviderWork()
    {
        using var probe = new Probe();
        await using var scope = await Scope.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new DelegateSink(evt =>
        {
            if (evt.Type != "run_started") return ValueTask.CompletedTask;
            entered.TrySetResult(); return new(release.Task);
        });
        var pending = scope.Runtime.RunAsync(scope.Request, sink, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try { await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { release.TrySetResult(); }
        Assert.AreEqual(0, scope.Client.Calls);
        Assert.AreEqual(0, scope.Tool.Calls);
        Assert.IsNull((await scope.Store.GetAsync(scope.Request.RequestedRunId!.Value, default))!.LeaseOwner);
        Assert.AreEqual("cancelled", Assert.ContainsSingle(span => span.OperationName == "ironpaw.run", probe.Spans).GetTagItem("ironpaw.outcome"));
    }

    [TestMethod]
    public async Task ThrowingTelemetryListenersCannotFailExecutionOrLeakAmbientActivities()
    {
        using var probe = new Probe();
        await using var scope = await Scope.OpenAsync();
        using var parent = new Activity("test-parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var activityFailures = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ExecutionDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => throw new InvalidOperationException("bad trace observer"),
            ActivityStopped = _ => throw new InvalidOperationException("bad trace observer")
        };
        ActivitySource.AddActivityListener(activityFailures);
        using var metricFailures = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ExecutionDiagnostics.MeterName) listener.EnableMeasurementEvents(instrument);
            }
        };
        metricFailures.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("bad metric observer"));
        metricFailures.SetMeasurementEventCallback<double>((_, _, _, _) => throw new InvalidOperationException("bad metric observer"));
        metricFailures.Start();
        var result = await scope.Runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, default);
        Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
        Assert.AreEqual(2, scope.Client.Calls);
        Assert.AreEqual(1, scope.Tool.Calls);
        Assert.AreSame<object>(parent, Activity.Current);
    }

    [TestMethod]
    public async Task ProviderFailureIsRecordedAsFailureWithoutAnAcceptedOrCompletedRun()
    {
        using var probe = new Probe();
        await using var scope = await Scope.OpenAsync([new AssistantTextDelta("PRIVATE partial"), new LlmErrorDelta("PRIVATE failure")]);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scope.Runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, default));
        Assert.AreEqual("failed", Assert.ContainsSingle(span => span.OperationName == "ironpaw.run", probe.Spans).GetTagItem("ironpaw.outcome"));
        Assert.AreEqual("failed", Assert.ContainsSingle(span => span.OperationName == "ironpaw.model", probe.Spans).GetTagItem("ironpaw.outcome"));
        Assert.AreNotEqual(WorkflowStatus.Succeeded, (await scope.Store.GetAsync(scope.Request.RequestedRunId!.Value, default))!.Status);
        Assert.DoesNotContain(tag => tag.Value?.Contains("PRIVATE", StringComparison.Ordinal) == true, probe.Spans.SelectMany(span => span.Tags));
    }

    [TestMethod]
    [DataRow(TaskAcceptanceOutcome.Accepted, "accepted")]
    [DataRow(TaskAcceptanceOutcome.Rejected, "failed")]
    [DataRow(TaskAcceptanceOutcome.Inconclusive, "inconclusive")]
    public async Task TaskSpansUseTheSavedHostDecisionInsteadOfTheExecutorCompletionClaim(TaskAcceptanceOutcome decision, string expected)
    {
        using var probe = new Probe();
        await using var scope = await Scope.OpenAsync();
        var graph = new WorkflowTaskGraph("observed-graph", "Observe trusted acceptance", [new("task", "Inspect the candidate") {
            AcceptanceCriteria = [new("accepted", "A trusted host inspects the candidate")]
        }]);
        var scheduler = new TaskGraphScheduler(scope.Store, new GraphExecutor(), new GraphVerifier(decision));
        await scheduler.StartAsync(graph, scope.Request.Caller, 1, default);
        var span = Assert.ContainsSingle(activity => activity.OperationName == "ironpaw.task", probe.Spans);
        Assert.AreEqual(expected, span.GetTagItem("ironpaw.outcome"));
        Assert.AreEqual("observed-graph", span.GetTagItem("ironpaw.graph.id"));
        Assert.IsNotNull(span.GetTagItem("ironpaw.task.attempt_id"));
        var graphSpan = Assert.ContainsSingle(activity => activity.OperationName == "ironpaw.graph", probe.Spans);
        Assert.AreEqual(graphSpan.TraceId, span.TraceId);
        Assert.AreEqual(graphSpan.SpanId, span.ParentSpanId);
    }

    private sealed class DelegateSink(Func<AgentEvent, ValueTask> publish) : IAgentEventSink
    { public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct) => publish(evt); }
    private sealed class GraphExecutor : IWorkflowTaskExecutor
    {
        public Task<WorkflowTaskResult> ExecuteAsync(TaskGraphClaim claim, CancellationToken ct) => Task.FromResult(new WorkflowTaskResult(
            claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId, WorkflowTaskStatus.Succeeded, "candidate", "Executor claims completion")
        {
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
            _activities = new()
            {
                ShouldListenTo = source => source.Name == ExecutionDiagnostics.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = span => Spans.Enqueue(span)
            };
            ActivitySource.AddActivityListener(_activities);
            _metrics = new()
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == ExecutionDiagnostics.MeterName) listener.EnableMeasurementEvents(instrument);
                }
            };
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
        public ToolDescriptor Descriptor { get; } = new("record_effect", "Record an effect", JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { secret = new { type = "string" } }
        }), "group:system");
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
