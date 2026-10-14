using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Cli.Tests;

public sealed class WorkflowCommandsTests
{
    [Fact]
    public async Task ListWorkflows_DoesNotResolveProviderOrStartRun()
    {
        var store = new FakeWorkflowStore(CreateCheckpoint());
        var orchestrator = new FakeOrchestrator();

        var exitCode = await WorkflowCommands.ExecuteAsync(
            CliOptions.Parse(["--list-workflows", "--provider", "invalid-provider"]), store, orchestrator,
            NullAgentEventSink.Instance, _ => throw new InvalidOperationException("Provider must not be resolved"),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Null(orchestrator.ResumedRunId);
    }

    [Fact]
    public async Task Resume_LoadsOriginalProviderAndRunId()
    {
        var checkpoint = CreateCheckpoint();
        var store = new FakeWorkflowStore(checkpoint);
        var orchestrator = new FakeOrchestrator();
        string? requestedProvider = null;

        var exitCode = await WorkflowCommands.ExecuteAsync(
            CliOptions.Parse(["--resume", checkpoint.RunId, "--provider", "ignored-provider", "--model", "ignored-model"]),
            store, orchestrator, NullAgentEventSink.Instance,
            provider => { requestedProvider = provider; return (true, false); }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(checkpoint.Request.Provider, requestedProvider);
        Assert.Equal(checkpoint.RunId, orchestrator.ResumedRunId?.Value);
        Assert.True(orchestrator.Caller!.IsOwner);
    }

    [Fact]
    public async Task Resume_UnknownWorkflowDoesNotAskForProviderConfiguration()
    {
        var orchestrator = new FakeOrchestrator();

        var exitCode = await WorkflowCommands.ExecuteAsync(CliOptions.Parse(["--resume", "unknown"]),
            new FakeWorkflowStore(), orchestrator, NullAgentEventSink.Instance,
            _ => throw new InvalidOperationException("Provider must not be resolved"), CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Null(orchestrator.ResumedRunId);
    }

    [Fact]
    public async Task Resume_CompletedWorkflowDoesNotRequireProviderCredentials()
    {
        var checkpoint = CreateCheckpoint();
        checkpoint = checkpoint with
        {
            Status = WorkflowStatus.Succeeded,
            Result = new AgentRunResult(new RunId(checkpoint.RunId), checkpoint.Request.SessionKey,
                "Saved result", [], new Dictionary<string, object?>())
        };
        var orchestrator = new FakeOrchestrator();

        var exitCode = await WorkflowCommands.ExecuteAsync(CliOptions.Parse(["--resume", checkpoint.RunId]),
            new FakeWorkflowStore(checkpoint), orchestrator, NullAgentEventSink.Instance,
            _ => throw new InvalidOperationException("Completed workflow must not require credentials"), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(checkpoint.RunId, orchestrator.ResumedRunId?.Value);
        Assert.True(orchestrator.Caller!.IsOwner);
    }

    [Fact]
    public async Task Resume_DoesNotRunWithoutSavedProviderConfiguration()
    {
        var checkpoint = CreateCheckpoint();
        var orchestrator = new FakeOrchestrator();

        var exitCode = await WorkflowCommands.ExecuteAsync(CliOptions.Parse(["--resume", checkpoint.RunId]),
            new FakeWorkflowStore(checkpoint), orchestrator, NullAgentEventSink.Instance,
            _ => (false, false), CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Null(orchestrator.ResumedRunId);
    }

    [Fact]
    public async Task Reconcile_SuppliesVerifiedOutcomeBeforeResume()
    {
        var checkpoint = CreateCheckpoint();
        var orchestrator = new FakeOrchestrator();
        var resultPath = Path.Combine(Path.GetTempPath(), $"ironpaw-reconciliation-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(ToolResult.Success("Verified file contents", ContentTrust.Local)));
            var options = CliOptions.Parse(["--resume", checkpoint.RunId, "--reconcile-tool", "tool-execution",
                "--reconcile-result", resultPath, "--reconcile-note", "Read destination file and matched expected content"]);

            var exitCode = await WorkflowCommands.ExecuteAsync(options, new FakeWorkflowStore(checkpoint),
                orchestrator, NullAgentEventSink.Instance, _ => (true, false), CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal("tool-execution", orchestrator.ReconciledExecutionId);
            Assert.Equal("Verified file contents", orchestrator.ReconciledResult!.Content);
            Assert.Equal(ContentTrust.Local, orchestrator.ReconciledResult.Trust);
            Assert.Contains("destination file", orchestrator.ReconciliationNote);
            Assert.Equal(new[] { "reconcile", "resume" }, orchestrator.Calls);
        }
        finally
        {
            File.Delete(resultPath);
        }
    }

    private static WorkflowCheckpoint CreateCheckpoint()
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowCheckpoint("workflow", "workflow", null, "task",
            new AgentRunRequest(new SessionKey("saved-session"), "original prompt", "saved-provider", "saved-model",
                new ToolSelectionPolicy(ToolProfile.Minimal), CallerIdentity.LocalOwner()), now, now);
    }

    private sealed class FakeWorkflowStore(params WorkflowCheckpoint[] checkpoints) : IWorkflowStore
    {
        public Task CreateAsync(WorkflowCheckpoint checkpoint, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkflowCheckpoint?> GetAsync(RunId runId, CancellationToken ct) =>
            Task.FromResult(checkpoints.FirstOrDefault(c => c.RunId == runId.Value));
        public Task<IReadOnlyList<WorkflowCheckpoint>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkflowCheckpoint>>(checkpoints);
        public Task<WorkflowCheckpoint> SaveAsync(WorkflowCheckpoint checkpoint, int expectedRevision, string eventType, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowJournalEntry>> ReadJournalAsync(RunId runId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeOrchestrator : IResumableAgentOrchestrator
    {
        public RunId? ResumedRunId { get; private set; }
        public CallerIdentity? Caller { get; private set; }
        public string? ReconciledExecutionId { get; private set; }
        public ToolResult? ReconciledResult { get; private set; }
        public string? ReconciliationNote { get; private set; }
        public List<string> Calls { get; } = [];

        public Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentEventSink sink, CancellationToken ct) =>
            throw new InvalidOperationException("Resume must not create a fresh run");

        public Task<AgentRunResult> ResumeAsync(RunId runId, CallerIdentity caller, IAgentEventSink sink, CancellationToken ct)
        {
            Calls.Add("resume");
            ResumedRunId = runId;
            Caller = caller;
            return Task.FromResult(new AgentRunResult(runId, new SessionKey("saved-session"), "Complete", [],
                new Dictionary<string, object?>()));
        }

        public Task<IReadOnlyList<WorkflowCheckpoint>> ListWorkflowsAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ReconcileToolAsync(RunId runId, string executionId, ToolResult result, string note, CallerIdentity caller, CancellationToken ct)
        {
            Calls.Add("reconcile");
            ReconciledExecutionId = executionId;
            ReconciledResult = result;
            ReconciliationNote = note;
            return Task.CompletedTask;
        }
    }
}
