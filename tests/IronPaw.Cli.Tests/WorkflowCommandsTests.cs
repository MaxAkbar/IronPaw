using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using Spectre.Console;

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

    [Theory]
    [InlineData("--pause", "pause")]
    [InlineData("--cancel", "cancel")]
    public async Task HumanControl_DoesNotResolveProviderOrResume(string option, string expectedCall)
    {
        var checkpoint = CreateCheckpoint();
        var orchestrator = new FakeOrchestrator();
        var exitCode = await WorkflowCommands.ExecuteAsync(CliOptions.Parse([option, checkpoint.RunId]),
            new FakeWorkflowStore(checkpoint), orchestrator, NullAgentEventSink.Instance,
            _ => throw new InvalidOperationException("Control must not require provider credentials"), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { expectedCall }, orchestrator.Calls);
        Assert.Equal(checkpoint.RunId, orchestrator.ControlledRunId?.Value);
        Assert.True(orchestrator.Caller!.IsOwner);
        Assert.Null(orchestrator.ResumedRunId);
    }

    [Fact]
    public async Task Answer_IsSavedBeforeProviderConfigurationAndResume()
    {
        var pending = CreateCheckpoint() with
        {
            Status = WorkflowStatus.AwaitingInput,
            HumanRequests = [InputRequest()]
        };
        var store = new FakeWorkflowStore(pending);
        var orchestrator = new FakeOrchestrator
        {
            AfterResponse = () => store.Replace(pending with { HumanRequests = [] })
        };
        var exitCode = await WorkflowCommands.ExecuteAsync(CliOptions.Parse(["--resume", pending.RunId,
                "--answer-request", "question-1", "--answer", "Use CSharpDB"]),
            store, orchestrator, NullAgentEventSink.Instance,
            _ => { orchestrator.Calls.Add("configure"); return (true, false); }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("question-1", orchestrator.AnsweredRequestId);
        Assert.Equal("Use CSharpDB", orchestrator.Answer);
        Assert.Equal(new[] { "answer", "configure", "resume" }, orchestrator.Calls);
    }

    [Theory]
    [InlineData("--approve-request", true)]
    [InlineData("--deny-request", false)]
    public async Task ApprovalDecision_IsSavedBeforeResume(string option, bool approved)
    {
        var checkpoint = CreateCheckpoint();
        var orchestrator = new FakeOrchestrator();
        var exitCode = await WorkflowCommands.ExecuteAsync(CliOptions.Parse(["--resume", checkpoint.RunId, option, "approval-1"]),
            new FakeWorkflowStore(checkpoint), orchestrator, NullAgentEventSink.Instance,
            _ => { orchestrator.Calls.Add("configure"); return (true, false); }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("approval-1", orchestrator.DecidedRequestId);
        Assert.Equal(approved, orchestrator.Approved);
        Assert.Equal(new[] { "decide", "configure", "resume" }, orchestrator.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_WithPendingHumanRequestDoesNotRequireProvider(bool inChild)
    {
        var parent = CreateCheckpoint();
        var pending = (inChild ? parent with { RunId = "child", ParentRunId = parent.RunId } : parent) with
        {
            Status = WorkflowStatus.AwaitingInput,
            HumanRequests = [InputRequest()]
        };
        var store = inChild ? new FakeWorkflowStore(parent, pending) : new FakeWorkflowStore(pending);
        var orchestrator = new FakeOrchestrator();
        var exitCode = await WorkflowCommands.ExecuteAsync(CliOptions.Parse(["--resume", parent.RunId]),
            store, orchestrator, NullAgentEventSink.Instance,
            _ => throw new InvalidOperationException("Pending human input must not require provider credentials"), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(parent.RunId, orchestrator.ResumedRunId?.Value);
    }

    [Fact]
    public async Task Resume_RunnableChildConfiguresProviderEvenWhenSiblingIsWaiting()
    {
        var parent = CreateCheckpoint();
        var child = parent with { RunId = "runnable", ParentRunId = parent.RunId };
        var sibling = parent with { RunId = "waiting", ParentRunId = parent.RunId, HumanRequests = [InputRequest()] };
        var orchestrator = new FakeOrchestrator();
        var configured = false;
        var exitCode = await WorkflowCommands.ExecuteAsync(CliOptions.Parse(["--resume", child.RunId]),
            new FakeWorkflowStore(parent, child, sibling), orchestrator, NullAgentEventSink.Instance,
            _ => { configured = true; return (true, false); }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(configured);
        Assert.Equal(child.RunId, orchestrator.ResumedRunId?.Value);
    }

    [Fact]
    public async Task ListWorkflows_DisplaysPendingRequestsLineageAndControlWithoutMarkupInterpretation()
    {
        var checkpoint = CreateCheckpoint() with
        {
            RunId = "child-run", ParentRunId = "workflow", Status = WorkflowStatus.AwaitingInput,
            Control = WorkflowControl.Pause,
            HumanRequests = [InputRequest(), new WorkflowHumanRequest("approval-1", WorkflowHumanRequestKind.ToolApproval,
                "Approve write [file]?", "source", DateTimeOffset.UtcNow, "execution-1", "operation-hash")],
            ToolExecutions = [new WorkflowToolExecution("execution-1", new ToolCall("call", "write_file", JsonSerializer.Deserialize<JsonElement>("{}")),
                State: WorkflowToolState.Planned)]
        };
        using var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No });
        console.Profile.Width = 200;
        await WorkflowCommands.ExecuteAsync(CliOptions.Parse(["--list-workflows"]),
            new FakeWorkflowStore(CreateCheckpoint(), checkpoint), new FakeOrchestrator(), NullAgentEventSink.Instance,
            _ => throw new InvalidOperationException("No provider needed"), CancellationToken.None, console);

        var rendered = output.ToString();
        Assert.Contains("request=question-1", rendered);
        Assert.Contains("Which [database]?", rendered);
        Assert.Contains("request=approval-1", rendered);
        Assert.Contains("Approve write [file]?", rendered);
        Assert.Contains("parent=workflow", rendered);
        Assert.Contains("child-run", rendered);
        Assert.Contains("AwaitingInput", rendered);
        Assert.Contains("Pause", rendered);
        Assert.Contains("Planned", rendered);
        Assert.DoesNotContain("Outcome unknown", rendered);
    }

    [Fact]
    public async Task ListWorkflows_DisplaysSharedRootBudgetOnceForParentAndChild()
    {
        var parent = CreateCheckpoint() with { Budget = new WorkflowBudget(MaxTotalTokens: 1000, MaxExecutionTime: TimeSpan.FromSeconds(20)) };
        var child = parent with { RunId = "child", ParentRunId = parent.RunId };
        var snapshot = new WorkflowBudgetSnapshot(parent.RunId, parent.Budget!, 300, 200,
            TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(13));
        var store = new FakeWorkflowStore(parent, child) { BudgetSnapshot = snapshot };
        using var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No });
        console.Profile.Width = 240;

        var exitCode = await WorkflowCommands.ExecuteAsync(CliOptions.Parse(["--list-workflows"]),
            store, new FakeOrchestrator(), NullAgentEventSink.Instance,
            _ => throw new InvalidOperationException("No provider needed"), CancellationToken.None, console);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { parent.RunId }, store.BudgetQueries);
        var rendered = output.ToString();
        Assert.Contains("tokens charged=300, reserved=200, remaining=500, limit=1000", rendered);
        Assert.Contains("execution consumed=7s, remaining=13s, limit=20s", rendered);
    }

    [Fact]
    public async Task Resume_BudgetExhaustedResultUsesNonSuccessExitCode()
    {
        var checkpoint = CreateCheckpoint();
        var snapshot = new WorkflowBudgetSnapshot(checkpoint.RunId, new WorkflowBudget(MaxTotalTokens: 100),
            100, 0, TimeSpan.Zero, null);
        var orchestrator = new FakeOrchestrator
        {
            ResultStatus = AgentRunStatus.BudgetExhausted,
            ResultMetadata = new Dictionary<string, object?> { ["rootWorkflowBudget"] = snapshot }
        };
        using var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No });
        console.Profile.Width = 240;

        var exitCode = await WorkflowCommands.ExecuteAsync(CliOptions.Parse(["--resume", checkpoint.RunId]),
            new FakeWorkflowStore(checkpoint), orchestrator, NullAgentEventSink.Instance, _ => (true, false), CancellationToken.None, console);

        Assert.Equal(2, exitCode);
        Assert.Contains("BudgetExhausted", output.ToString());
        Assert.Contains("tokens charged=100, reserved=0, remaining=0, limit=100", output.ToString());
    }

    [Fact]
    public void RenderFinal_DisplaysBudgetSnapshotRestoredFromJsonMetadata()
    {
        var snapshot = new WorkflowBudgetSnapshot("workflow", new WorkflowBudget(MaxTotalTokens: 1000),
            300, 50, TimeSpan.FromSeconds(2.5), null);
        var result = new AgentRunResult(new RunId("workflow"), new SessionKey("test"), "Saved result", [],
            new Dictionary<string, object?> { ["rootWorkflowBudget"] = JsonSerializer.SerializeToElement(snapshot) });
        using var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No });
        console.Profile.Width = 240;

        ConsoleUi.RenderFinal(result, console);

        Assert.Contains("tokens charged=300, reserved=50, remaining=650, limit=1000", output.ToString());
        Assert.Contains("execution consumed=2.5s, remaining=unlimited, limit=unlimited", output.ToString());
    }

    private static WorkflowHumanRequest InputRequest() => new("question-1", WorkflowHumanRequestKind.Input,
        "Which [database]?", "source", DateTimeOffset.UtcNow);

    private static WorkflowCheckpoint CreateCheckpoint()
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowCheckpoint("workflow", "workflow", null, "task",
            new AgentRunRequest(new SessionKey("saved-session"), "original prompt", "saved-provider", "saved-model",
                new ToolSelectionPolicy(ToolProfile.Minimal), CallerIdentity.LocalOwner()), now, now);
    }

    private sealed class FakeWorkflowStore(params WorkflowCheckpoint[] checkpoints) : IWorkflowStore
    {
        private readonly Dictionary<string, WorkflowCheckpoint> saved = checkpoints.ToDictionary(c => c.RunId);
        public WorkflowBudgetSnapshot? BudgetSnapshot { get; init; }
        public List<string> BudgetQueries { get; } = [];
        public void Replace(WorkflowCheckpoint checkpoint) => saved[checkpoint.RunId] = checkpoint;
        public Task CreateAsync(WorkflowCheckpoint checkpoint, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkflowCheckpoint?> GetAsync(RunId runId, CancellationToken ct) =>
            Task.FromResult(saved.GetValueOrDefault(runId.Value));
        public Task<IReadOnlyList<WorkflowCheckpoint>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkflowCheckpoint>>(saved.Values.ToArray());
        public Task<WorkflowCheckpoint> SaveAsync(WorkflowCheckpoint checkpoint, int expectedRevision, string eventType, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowJournalEntry>> ReadJournalAsync(RunId runId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<WorkflowBudgetSnapshot> GetBudgetAsync(RunId rootRunId, CancellationToken ct)
        {
            BudgetQueries.Add(rootRunId.Value);
            var root = saved[rootRunId.Value];
            return Task.FromResult(BudgetSnapshot ?? new WorkflowBudgetSnapshot(rootRunId.Value,
                root.Budget ?? new WorkflowBudget(), 0, 0, TimeSpan.Zero, root.Budget?.MaxExecutionTime));
        }
    }

    private sealed class FakeOrchestrator : IResumableAgentOrchestrator
    {
        public RunId? ResumedRunId { get; private set; }
        public CallerIdentity? Caller { get; private set; }
        public string? ReconciledExecutionId { get; private set; }
        public ToolResult? ReconciledResult { get; private set; }
        public string? ReconciliationNote { get; private set; }
        public List<string> Calls { get; } = [];
        public RunId? ControlledRunId { get; private set; }
        public string? AnsweredRequestId { get; private set; }
        public string? Answer { get; private set; }
        public string? DecidedRequestId { get; private set; }
        public bool? Approved { get; private set; }
        public Action? AfterResponse { get; init; }
        public AgentRunStatus ResultStatus { get; init; } = AgentRunStatus.Succeeded;
        public IReadOnlyDictionary<string, object?> ResultMetadata { get; init; } = new Dictionary<string, object?>();

        public Task AnswerAsync(RunId runId, string requestId, string answer, CallerIdentity caller, CancellationToken ct)
        {
            Calls.Add("answer");
            Caller = caller;
            AnsweredRequestId = requestId;
            Answer = answer;
            AfterResponse?.Invoke();
            return Task.CompletedTask;
        }

        public Task DecideApprovalAsync(RunId runId, string requestId, bool approved, CallerIdentity caller, CancellationToken ct)
        {
            Calls.Add("decide");
            Caller = caller;
            DecidedRequestId = requestId;
            Approved = approved;
            AfterResponse?.Invoke();
            return Task.CompletedTask;
        }

        public Task PauseAsync(RunId runId, CallerIdentity caller, CancellationToken ct) => ControlAsync(runId, caller, "pause");
        public Task CancelAsync(RunId runId, CallerIdentity caller, CancellationToken ct) => ControlAsync(runId, caller, "cancel");
        private Task ControlAsync(RunId runId, CallerIdentity caller, string control)
        {
            Calls.Add(control);
            Caller = caller;
            ControlledRunId = runId;
            return Task.CompletedTask;
        }

        public Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentEventSink sink, CancellationToken ct) =>
            throw new InvalidOperationException("Resume must not create a fresh run");

        public Task<AgentRunResult> ResumeAsync(RunId runId, CallerIdentity caller, IAgentEventSink sink, CancellationToken ct)
        {
            Calls.Add("resume");
            ResumedRunId = runId;
            Caller = caller;
            return Task.FromResult(new AgentRunResult(runId, new SessionKey("saved-session"), "Complete", [],
                ResultMetadata, ResultStatus));
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
