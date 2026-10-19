using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.Desktop;

namespace IronPaw.Desktop.Tests;

public sealed class WorkflowRecoveryControllerTests
{
    [Fact]
    public async Task AnswerPersistsExactChildRequestBeforeResumingRoot()
    {
        var scope = new Scope(WorkflowHumanRequestKind.Input);
        var result = await scope.Controller.AnswerAndResumeAsync(new("child"), "request-7", "Use blue", scope.Sink, default);
        Assert.Equal("root", result.RunId.Value);
        Assert.Equal(["answer:child:request-7:Use blue", "resume:root"], scope.Orchestrator.Calls);
        Assert.Equal(CallerIdentity.LocalOwner(), scope.Orchestrator.LastCaller);
        Assert.Same(scope.Sink, scope.Orchestrator.LastSink);
    }

    [Fact]
    public async Task FailedReceiptNeverResumes()
    {
        var scope = new Scope(WorkflowHumanRequestKind.Input);
        scope.Orchestrator.ReceiptFailure = new WorkflowConflictException("stale request");
        await Assert.ThrowsAsync<WorkflowConflictException>(() => scope.Controller.AnswerAndResumeAsync(new("child"), "request-7", "Use blue", scope.Sink, default));
        Assert.Single(scope.Orchestrator.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("answered")]
    public async Task InvalidOrAnsweredRequestCannotResume(string requestId)
    {
        var scope = new Scope(WorkflowHumanRequestKind.Input);
        scope.Store.Runs["child"] = scope.Store.Runs["child"] with { HumanRequests = [
            scope.Store.Runs["child"].HumanRequests![0] with { RequestId = "answered", Response = new("already", null, "owner", DateTimeOffset.UtcNow) } ] };
        await Assert.ThrowsAsync<WorkflowConflictException>(() => scope.Controller.AnswerAndResumeAsync(new("child"), requestId, "Use blue", scope.Sink, default));
        Assert.Empty(scope.Orchestrator.Calls);
    }

    [Fact]
    public async Task RequestKindMustMatchResponse()
    {
        var scope = new Scope(WorkflowHumanRequestKind.ToolApproval);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => scope.Controller.AnswerAndResumeAsync(new("child"), "request-7", "yes", scope.Sink, default));
        Assert.Empty(scope.Orchestrator.Calls);
    }

    [Fact]
    public async Task ApprovalPersistsExactOperationBeforeRootResume()
    {
        var scope = new Scope(WorkflowHumanRequestKind.ToolApproval);
        await scope.Controller.DecideApprovalAsync(new("child"), "request-7", true, scope.Sink, default);
        Assert.Equal(["approval:child:request-7:True", "resume:root"], scope.Orchestrator.Calls);
        Assert.Equal(CallerIdentity.LocalOwner(), scope.Orchestrator.LastCaller);
    }

    [Fact]
    public async Task DenialRecordsDecisionWithoutExecutingOrResuming()
    {
        var scope = new Scope(WorkflowHumanRequestKind.ToolApproval);
        Assert.Null(await scope.Controller.DecideApprovalAsync(new("child"), "request-7", false, scope.Sink, default));
        Assert.Equal(["approval:child:request-7:False"], scope.Orchestrator.Calls);
    }

    [Fact]
    public async Task ReconciliationPreservesSuppliedOutcomeAndEvidenceBeforeResume()
    {
        var scope = new Scope();
        await scope.Controller.ReconcileAndResumeAsync(new("child"), "effect-4", "file was saved", false, "Read resulting file and checksum", scope.Sink, default);
        Assert.Equal(["reconcile:child:effect-4", "resume:root"], scope.Orchestrator.Calls);
        Assert.Equal(new ToolResult(false, "file was saved", ContentTrust.Local), scope.Orchestrator.LastResult);
        Assert.Equal("Read resulting file and checksum", scope.Orchestrator.LastNote);
    }

    [Fact]
    public async Task CompletedOutcomeCannotBeReconciledAgain()
    {
        var scope = new Scope();
        scope.Store.Runs["child"] = scope.Store.Runs["child"] with { ToolExecutions = [scope.Store.Runs["child"].ToolExecutions![0] with { Result = ToolResult.Success("done"), State = WorkflowToolState.Completed }] };
        await Assert.ThrowsAsync<WorkflowConflictException>(() => scope.Controller.ReconcileAndResumeAsync(new("child"), "effect-4", "replacement", false, "evidence", scope.Sink, default));
        Assert.Empty(scope.Orchestrator.Calls);
    }

    [Fact]
    public async Task CancelledRootCanRecordUnknownOutcomeWithoutResuming()
    {
        var scope = new Scope();
        scope.Store.Runs["root"] = scope.Store.Runs["root"] with { Control = WorkflowControl.Cancel };
        Assert.Null(await scope.Controller.ReconcileAndResumeAsync(new("child"), "effect-4", "saved", false, "checked disk", scope.Sink, default));
        Assert.Equal(["reconcile:child:effect-4"], scope.Orchestrator.Calls);
    }

    [Theory]
    [InlineData("", "evidence")]
    [InlineData("result", "")]
    public async Task ReconciliationRequiresExplicitResultAndEvidence(string result, string evidence)
    {
        var scope = new Scope();
        await Assert.ThrowsAsync<ArgumentException>(() => scope.Controller.ReconcileAndResumeAsync(new("child"), "effect-4", result, false, evidence, scope.Sink, default));
        Assert.Empty(scope.Orchestrator.Calls);
    }

    [Fact]
    public async Task PauseAndPermanentCancellationAlwaysUseRootAndLocalOwner()
    {
        var scope = new Scope();
        await scope.Controller.PauseAsync(new("child"), default);
        await scope.Controller.CancelAsync(new("child"), default);
        Assert.Equal(["pause:root", "cancel:root"], scope.Orchestrator.Calls);
        Assert.Equal(CallerIdentity.LocalOwner(), scope.Orchestrator.LastCaller);
    }

    [Fact]
    public async Task RootControlRemainsResponsiveDuringPendingResume()
    {
        var scope = new Scope();
        var pending = new TaskCompletionSource<AgentRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Orchestrator.PendingResume = pending.Task;
        var resume = scope.Controller.ResumeAsync(new("child"), scope.Sink, default);
        Assert.False(resume.IsCompleted);
        await scope.Controller.PauseAsync(new("child"), default);
        Assert.Equal(["resume:root", "pause:root"], scope.Orchestrator.Calls);
        pending.SetResult(scope.Orchestrator.Result);
        await resume;
    }

    [Fact]
    public async Task BrowserAndDetailsIncludeOnlySelectedRootsDescendants()
    {
        var scope = new Scope();
        scope.Store.Runs["other"] = MakeRun("other", "other", null);
        var roots = await scope.Controller.ListRootsAsync(default);
        Assert.Equal(2, roots.Count);
        Assert.DoesNotContain(roots, run => run.RunId == "child");
        var detail = await scope.Controller.InspectAsync(new("child"), default);
        Assert.Equal("root", detail.Root.RunId);
        Assert.Equal("child", detail.Selected.RunId);
        Assert.Equal(["root", "child"], detail.Invocations.Select(run => run.RunId));
        Assert.Equal(3, detail.Usage.ModelCalls);
        Assert.Equal(2, detail.Journal.Count);
        Assert.Equal("root", detail.Budget.RootRunId);
    }

    [Fact]
    public void NewRunSettingsAcceptOptionalRootLimits()
    {
        Assert.Equal(new WorkflowBudget(), DesktopRunSettings.ParseBudget("", ""));
        Assert.Equal(new WorkflowBudget(MaxTotalTokens: 12000, MaxExecutionTime: TimeSpan.FromSeconds(90)),
            DesktopRunSettings.ParseBudget("12000", "90"));
    }

    [Theory]
    [InlineData("-1", "")]
    [InlineData("0", "")]
    [InlineData("abc", "")]
    [InlineData("", "0")]
    [InlineData("", "-20")]
    [InlineData("", "1.5")]
    public void InvalidNewRunLimitsFailBeforeExecution(string tokens, string seconds) =>
        Assert.Throws<ArgumentException>(() => DesktopRunSettings.ParseBudget(tokens, seconds));

    internal static WorkflowCheckpoint MakeRun(string id, string root, string? parent) => new(id, root, parent,
        "task-" + id, new(new("desktop-test"), "inspect work", "local", "rule-based-v1", new(ToolProfile.Coding), CallerIdentity.LocalOwner()),
        DateTimeOffset.UnixEpoch.AddSeconds(parent is null ? 1 : 2), DateTimeOffset.UtcNow,
        Status: WorkflowStatus.Interrupted, Budget: new(), Usage: new(parent is null ? 1 : 2));

    private sealed class Scope
    {
        public Scope(WorkflowHumanRequestKind? kind = null)
        {
            Store.Runs["root"] = MakeRun("root", "root", null);
            Store.Runs["child"] = MakeRun("child", "root", "root") with {
                HumanRequests = kind is null ? [] : [new("request-7", kind.Value, "Choose a color", "source", DateTimeOffset.UtcNow)],
                ToolExecutions = [new("effect-4", new("tool-call", "write_file", JsonSerializer.SerializeToElement(new { path = "result.txt" }))) ] };
            Orchestrator = new(Store);
            Controller = new(Orchestrator, Store);
        }
        public MemoryStore Store { get; } = new();
        public FakeOrchestrator Orchestrator { get; }
        public WorkflowRecoveryController Controller { get; }
        public IAgentEventSink Sink { get; } = new TestSink();
    }

    private sealed class TestSink : IAgentEventSink
    {
        public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct) => ValueTask.CompletedTask;
    }

    internal sealed class FakeOrchestrator(MemoryStore store) : IResumableAgentOrchestrator
    {
        public List<string> Calls { get; } = [];
        public CallerIdentity? LastCaller { get; private set; }
        public IAgentEventSink? LastSink { get; private set; }
        public ToolResult? LastResult { get; private set; }
        public string? LastNote { get; private set; }
        public Exception? ReceiptFailure { get; set; }
        public Task<AgentRunResult>? PendingResume { get; set; }
        public Task<AgentRunResult>? PendingRun { get; set; }
        private AgentRunRequest? _lastRequest;
        public AgentRunRequest? LastRequest { get => Volatile.Read(ref _lastRequest); private set => Volatile.Write(ref _lastRequest, value); }
        public AgentRunResult Result { get; } = new(new("root"), new("desktop-test"), "done", [], new Dictionary<string, object?>());
        public Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentEventSink sink, CancellationToken ct)
        {
            LastSink = sink;
            var id = request.RequestedRunId ?? RunId.New();
            store.Runs[id.Value] = MakeRun(id.Value, id.Value, null) with { Request = request };
            Calls.Add("run:" + id);
            LastRequest = request;
            return PendingRun ?? Task.FromResult(Result with { RunId = id });
        }
        public Task<IReadOnlyList<WorkflowCheckpoint>> ListWorkflowsAsync(CancellationToken ct) => store.ListAsync(ct);
        public Task<AgentRunResult> ResumeAsync(RunId runId, CallerIdentity caller, IAgentEventSink sink, CancellationToken ct)
        {
            Calls.Add("resume:" + runId.Value); LastCaller = caller; LastSink = sink;
            return PendingResume ?? Task.FromResult(Result);
        }
        public Task AnswerAsync(RunId runId, string requestId, string answer, CallerIdentity caller, CancellationToken ct)
        {
            Calls.Add($"answer:{runId}:{requestId}:{answer}"); LastCaller = caller;
            return ReceiptFailure is null ? Task.CompletedTask : Task.FromException(ReceiptFailure);
        }
        public Task DecideApprovalAsync(RunId runId, string requestId, bool approved, CallerIdentity caller, CancellationToken ct)
        { Calls.Add($"approval:{runId}:{requestId}:{approved}"); LastCaller = caller; return Task.CompletedTask; }
        public Task ReconcileToolAsync(RunId runId, string executionId, ToolResult result, string note, CallerIdentity caller, CancellationToken ct)
        { Calls.Add($"reconcile:{runId}:{executionId}"); LastCaller = caller; LastResult = result; LastNote = note; return Task.CompletedTask; }
        public Task PauseAsync(RunId runId, CallerIdentity caller, CancellationToken ct)
        { Calls.Add("pause:" + runId); LastCaller = caller; return Task.CompletedTask; }
        public Task CancelAsync(RunId runId, CallerIdentity caller, CancellationToken ct)
        { Calls.Add("cancel:" + runId); LastCaller = caller; return Task.CompletedTask; }
    }

    internal sealed class MemoryStore : IWorkflowStore
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, WorkflowCheckpoint> Runs { get; } = [];
        public Task CreateAsync(WorkflowCheckpoint checkpoint, CancellationToken ct) { if (!Runs.TryAdd(checkpoint.RunId, checkpoint)) throw new WorkflowConflictException("Workflow already exists"); return Task.CompletedTask; }
        public Task<WorkflowCheckpoint?> GetAsync(RunId runId, CancellationToken ct) => Task.FromResult(Runs.GetValueOrDefault(runId.Value));
        public Task<IReadOnlyList<WorkflowCheckpoint>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WorkflowCheckpoint>>(Runs.Values.ToArray());
        public Task<WorkflowCheckpoint> SaveAsync(WorkflowCheckpoint checkpoint, int expectedRevision, string eventType, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowJournalEntry>> ReadJournalAsync(RunId runId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkflowJournalEntry>>([new(runId.Value, 1, DateTimeOffset.UtcNow, "checkpoint")]);
    }
}
