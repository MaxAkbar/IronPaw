using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

public sealed class WorkflowHumanControlTests
{
    [Fact]
    public async Task CancellingIdleHumanWait_PermanentlyRejectsAnswerAndResume()
    {
        await using var scope = await TestScope.OpenAsync();
        var human = new HumanInputTool();
        var runtime = scope.CreateOrchestrator(new ScriptedClient(
            ToolResponse("request_input", new { question = "Which target?" })), human);
        var waiting = await runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.AwaitingInput, waiting.Status);
        var pending = Assert.Single((await scope.Store.GetAsync(waiting.RunId, CancellationToken.None))!.HumanRequests!);
        await runtime.CancelAsync(waiting.RunId, scope.Request.Caller, CancellationToken.None);
        var cancelled = (await scope.Store.GetAsync(waiting.RunId, CancellationToken.None))!;
        Assert.Equal(WorkflowControl.Cancel, cancelled.Control);
        Assert.Equal(WorkflowStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.LeaseOwner);
        await runtime.CancelAsync(waiting.RunId, scope.Request.Caller, CancellationToken.None);
        Assert.Equal(cancelled.Revision, (await scope.Store.GetAsync(waiting.RunId, CancellationToken.None))!.Revision);
        await scope.ReopenAsync();
        var unused = new ScriptedClient();
        var resumed = scope.CreateOrchestrator(unused, human);
        await Assert.ThrowsAsync<WorkflowCancelledException>(() => resumed.AnswerAsync(waiting.RunId, pending.RequestId,
            "new target", scope.Request.Caller, CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowCancelledException>(() => resumed.ResumeAsync(waiting.RunId,
            scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
        var unchanged = (await scope.Store.GetAsync(waiting.RunId, CancellationToken.None))!;
        Assert.Equal(cancelled.Revision, unchanged.Revision);
        Assert.Null(Assert.Single(unchanged.HumanRequests!).Response);
        Assert.Equal(0, unused.CallCount);
        Assert.Equal(0, human.CallCount);
    }

    [Theory]
    [InlineData(WorkflowStatus.BudgetExhausted)]
    [InlineData(WorkflowStatus.Failed)]
    public async Task PausingTerminalRunWithoutResult_CannotReopenItOrAlterLaterConversation(WorkflowStatus terminalStatus)
    {
        await using var scope = await TestScope.OpenAsync();
        var request = scope.Request with { Budget = new WorkflowBudget(MaxModelCalls: 1) };
        var failed = scope.CreateOrchestrator(new ScriptedClient([new LlmErrorDelta("interrupted model attempt")]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.RunAsync(request, NullAgentEventSink.Instance, CancellationToken.None));
        var checkpoint = Assert.Single(await scope.Store.ListAsync(CancellationToken.None));
        if (terminalStatus == WorkflowStatus.BudgetExhausted)
        {
            await scope.Store.SaveAsync(checkpoint with { Status = WorkflowStatus.BudgetExhausted },
                checkpoint.Revision, "legacy_budget_terminal", CancellationToken.None);
        }
        else
            await scope.Store.SaveAsync(checkpoint with { Status = WorkflowStatus.Failed }, checkpoint.Revision, "terminal_failure", CancellationToken.None);
        checkpoint = (await scope.Store.GetAsync(new RunId(checkpoint.RunId), CancellationToken.None))!;
        Assert.Equal(terminalStatus, checkpoint.Status);
        Assert.Null(checkpoint.Result);
        var later = await scope.CreateOrchestrator(new ScriptedClient(FinalResponse()))
            .RunAsync(scope.Request with { UserInput = "a later conversation turn" }, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Succeeded, later.Status);
        var transcript = await scope.Transcripts.LoadAsync(request.SessionKey, CancellationToken.None);
        var client = new ScriptedClient();
        await Assert.ThrowsAsync<WorkflowConflictException>(() => scope.CreateOrchestrator(client).PauseAsync(
            new RunId(checkpoint.RunId), request.Caller, CancellationToken.None));
        var unchanged = (await scope.Store.GetAsync(new RunId(checkpoint.RunId), CancellationToken.None))!;
        Assert.Equal(checkpoint.Revision, unchanged.Revision);
        Assert.Equal(checkpoint.Status, unchanged.Status);
        Assert.Equal(WorkflowControl.None, unchanged.Control);
        Assert.Null(unchanged.Result);
        Assert.Equal(transcript, await scope.Transcripts.LoadAsync(request.SessionKey, CancellationToken.None));
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task CancellationRacingSuspensionCommit_ReturnsAndPersistsCancelled()
    {
        await using var scope = await TestScope.OpenAsync();
        var human = new HumanInputTool();
        var store = new CancelBeforeSuspensionStore(scope.Store);
        var client = new ScriptedClient(ToolResponse("request_input", new { question = "Which target?" }));
        var runtime = scope.CreateOrchestrator(client, registry: new InMemoryToolRegistry([human]), workflowStore: store);
        var result = await runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(1, store.CancellationCount);
        Assert.Equal(AgentRunStatus.Cancelled, result.Status);
        var checkpoint = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        Assert.Equal(WorkflowControl.Cancel, checkpoint.Control);
        Assert.Equal(WorkflowStatus.Cancelled, checkpoint.Status);
        Assert.Equal(AgentRunStatus.Cancelled, checkpoint.Result!.Status);
        Assert.Null(checkpoint.Suspension);
        Assert.Null(checkpoint.LeaseOwner);
        Assert.False(WorkflowIdentity.HasUnknownEffects(checkpoint));
        Assert.Null(Assert.Single(checkpoint.HumanRequests!).Response);
        Assert.Equal(0, human.CallCount);
        Assert.Equal(1, client.CallCount);
        await Assert.ThrowsAsync<WorkflowCancelledException>(() => runtime.ResumeAsync(result.RunId,
            scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
        Assert.DoesNotContain(await scope.Store.ReadJournalAsync(result.RunId, CancellationToken.None),
            entry => entry.EventType == "run_suspended");
    }

    [Fact]
    public async Task HumanInputAfterCompletedEffect_SurvivesRestartAndContinuesSameInvocation()
    {
        await using var scope = await TestScope.OpenAsync();
        var effect = new EffectTool();
        var human = new HumanInputTool();
        var initialClient = new ScriptedClient(ToolResponse("record_effect"),
            ToolResponse("request_input", new { question = "Which destination should receive the report?" }));
        var initial = await scope.CreateOrchestrator(initialClient, effect, human)
            .RunAsync(scope.Request, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.AwaitingInput, initial.Status);
        var beforeRestart = (await scope.Store.GetAsync(initial.RunId, CancellationToken.None))!;
        var pending = Assert.Single(beforeRestart.HumanRequests!);
        Assert.Equal(WorkflowHumanRequestKind.Input, pending.Kind);
        Assert.Null(beforeRestart.Result);
        Assert.NotNull(beforeRestart.Suspension);
        Assert.Equal(WorkflowToolState.Completed, beforeRestart.ToolExecutions![0].State);
        Assert.Equal(WorkflowToolState.Planned, beforeRestart.ToolExecutions[1].State);
        Assert.False(WorkflowIdentity.HasUnknownEffects(beforeRestart));
        Assert.Equal(1, effect.CallCount);
        Assert.Equal(0, human.CallCount);

        await scope.ReopenAsync();
        var unused = new ScriptedClient();
        var unanswered = await scope.CreateOrchestrator(unused, effect, human).ResumeAsync(initial.RunId,
            scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(initial.RunId, unanswered.RunId);
        Assert.Equal(AgentRunStatus.AwaitingInput, unanswered.Status);
        Assert.Equal(0, unused.CallCount);

        var resumedClient = new ScriptedClient(FinalResponse());
        var resumedRuntime = scope.CreateOrchestrator(resumedClient, effect, human);
        await resumedRuntime.AnswerAsync(initial.RunId, pending.RequestId, "Use the internal project folder",
            scope.Request.Caller, CancellationToken.None);
        var result = await resumedRuntime.ResumeAsync(initial.RunId, scope.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);

        Assert.Equal(initial.RunId, result.RunId);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(1, effect.CallCount);
        Assert.Equal(0, human.CallCount);
        Assert.Equal(1, resumedClient.CallCount);
        Assert.Single(result.Transcript, message => message.Role == ChatRole.User);
        Assert.Contains(Assert.Single(resumedClient.Requests).Messages,
            message => message.Role == ChatRole.Tool && message.Content.Contains("internal project folder", StringComparison.Ordinal));
        var saved = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        Assert.Equal(beforeRestart.RootRunId, saved.RootRunId);
        Assert.Equal(beforeRestart.TaskId, saved.TaskId);
        Assert.Equal(beforeRestart.ToolExecutions.Select(tool => tool.ExecutionId), saved.ToolExecutions!.Select(tool => tool.ExecutionId));
        Assert.Equal(beforeRestart.Usage!.ModelCalls + 1, saved.Usage!.ModelCalls);
        Assert.Equal(beforeRestart.Usage.ToolCalls, saved.Usage.ToolCalls);
        Assert.Equal("Use the internal project folder", Assert.Single(saved.HumanRequests!).Response!.Answer);
        Assert.Null(saved.Suspension);
    }

    [Fact]
    public async Task HumanResponse_RequiresExactCallerAndRequest_AndIsIdempotent()
    {
        await using var scope = await TestScope.OpenAsync();
        var human = new HumanInputTool();
        var runtime = scope.CreateOrchestrator(new ScriptedClient(
            ToolResponse("request_input", new { question = "Which branch?" })), human);
        var result = await runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, CancellationToken.None);
        var checkpoint = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        var pending = Assert.Single(checkpoint.HumanRequests!);
        var other = CallerIdentity.Authenticated("someone-else", "Another caller");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runtime.AnswerAsync(result.RunId,
            pending.RequestId, "main", other, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runtime.AnswerAsync(result.RunId,
            pending.RequestId, "main", scope.Request.Caller with { IsOwner = true }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.AnswerAsync(result.RunId,
            "wrong-request-id", "main", scope.Request.Caller, CancellationToken.None));
        var afterRejected = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        Assert.Equal(checkpoint.Revision, afterRejected.Revision);
        Assert.Null(Assert.Single(afterRejected.HumanRequests!).Response);

        await runtime.AnswerAsync(result.RunId, pending.RequestId, "main", scope.Request.Caller, CancellationToken.None);
        var answered = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        await runtime.AnswerAsync(result.RunId, pending.RequestId, "main", scope.Request.Caller, CancellationToken.None);
        var duplicate = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        Assert.Equal(answered.Revision, duplicate.Revision);
        Assert.Equal(Assert.Single(answered.HumanRequests!).Response, Assert.Single(duplicate.HumanRequests!).Response);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => runtime.AnswerAsync(result.RunId,
            pending.RequestId, "develop", scope.Request.Caller, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.DecideApprovalAsync(result.RunId,
            pending.RequestId, true, scope.Request.Caller, CancellationToken.None));
        Assert.Equal(0, human.CallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApprovalBeforeEffect_SurvivesRestartAndBindsDecisionToOperation(bool approved)
    {
        await using var scope = await TestScope.OpenAsync();
        var effect = new EffectTool();
        var request = scope.Request with { ApprovalRequiredTools = ["record_effect"] };
        var initial = await scope.CreateOrchestrator(new ScriptedClient(ToolResponse("record_effect", new { target = "first" })), effect)
            .RunAsync(request, NullAgentEventSink.Instance, CancellationToken.None);
        var waiting = (await scope.Store.GetAsync(initial.RunId, CancellationToken.None))!;
        var pending = Assert.Single(waiting.HumanRequests!);
        Assert.Equal(WorkflowHumanRequestKind.ToolApproval, pending.Kind);
        Assert.False(string.IsNullOrWhiteSpace(pending.OperationFingerprint));
        Assert.Equal(Assert.Single(waiting.ToolExecutions!).ExecutionId, pending.ExecutionId);
        Assert.Equal(WorkflowToolState.Planned, Assert.Single(waiting.ToolExecutions!).State);
        Assert.False(WorkflowIdentity.HasUnknownEffects(waiting));
        Assert.Equal(0, effect.CallCount);
        await scope.ReopenAsync();

        var client = new ScriptedClient(FinalResponse());
        var runtime = scope.CreateOrchestrator(client, effect);
        await runtime.DecideApprovalAsync(initial.RunId, pending.RequestId, approved, request.Caller, CancellationToken.None);
        var decided = (await scope.Store.GetAsync(initial.RunId, CancellationToken.None))!;
        await runtime.DecideApprovalAsync(initial.RunId, pending.RequestId, approved, request.Caller, CancellationToken.None);
        Assert.Equal(decided.Revision, (await scope.Store.GetAsync(initial.RunId, CancellationToken.None))!.Revision);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => runtime.DecideApprovalAsync(initial.RunId,
            pending.RequestId, !approved, request.Caller, CancellationToken.None));

        var result = await runtime.ResumeAsync(initial.RunId, request.Caller, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(approved ? 1 : 0, effect.CallCount);
        Assert.Equal(1, client.CallCount);
        var saved = (await scope.Store.GetAsync(initial.RunId, CancellationToken.None))!;
        var execution = Assert.Single(saved.ToolExecutions!);
        Assert.Equal(WorkflowToolState.Completed, execution.State);
        Assert.NotNull(execution.Result);
        Assert.Equal(!approved, execution.Result.IsError);
        Assert.Equal(approved, Assert.Single(saved.HumanRequests!).Response!.Approved);
        Assert.Equal(pending.OperationFingerprint, Assert.Single(saved.HumanRequests!).OperationFingerprint);
    }

    [Fact]
    public async Task ChangedToolSchema_AfterApprovalFailsClosedBeforeEffect()
    {
        await using var scope = await TestScope.OpenAsync();
        var original = new EffectTool();
        var request = scope.Request with { ApprovalRequiredTools = ["record_effect"] };
        var initialRuntime = scope.CreateOrchestrator(new ScriptedClient(ToolResponse("record_effect")), original);
        var result = await initialRuntime.RunAsync(request, NullAgentEventSink.Instance, CancellationToken.None);
        var pending = Assert.Single((await scope.Store.GetAsync(result.RunId, CancellationToken.None))!.HumanRequests!);
        await initialRuntime.DecideApprovalAsync(result.RunId, pending.RequestId, true, request.Caller, CancellationToken.None);
        await scope.ReopenAsync();

        var changed = new EffectTool(schema: """{"type":"object","properties":{"new_argument":{"type":"string"}}}""");
        var client = new ScriptedClient(FinalResponse());
        await Assert.ThrowsAsync<WorkflowConflictException>(() => scope.CreateOrchestrator(client, changed)
            .ResumeAsync(result.RunId, request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
        Assert.Equal(0, client.CallCount);
        Assert.Equal(0, changed.CallCount);
        Assert.Equal(0, original.CallCount);
    }

    [Fact]
    public async Task ApprovedOperationArguments_CannotBeChangedBeforeResume()
    {
        await using var scope = await TestScope.OpenAsync();
        var effect = new EffectTool();
        var client = new ScriptedClient(ToolResponse("record_effect", new { target = "approved-target" }), FinalResponse());
        var runtime = scope.CreateOrchestrator(client, effect);
        var result = await runtime.RunAsync(scope.Request with { ApprovalRequiredTools = ["record_effect"] },
            NullAgentEventSink.Instance, CancellationToken.None);
        var checkpoint = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        var pending = Assert.Single(checkpoint.HumanRequests!);
        await runtime.DecideApprovalAsync(result.RunId, pending.RequestId, true, scope.Request.Caller, CancellationToken.None);
        checkpoint = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        var execution = Assert.Single(checkpoint.ToolExecutions!);
        var changed = execution with { Call = execution.Call with { Arguments = JsonSerializer.SerializeToElement(new { target = "unapproved-target" }) } };
        await Assert.ThrowsAsync<WorkflowConflictException>(() => scope.Store.SaveAsync(checkpoint with { ToolExecutions = [changed] },
            checkpoint.Revision, "replace_approved_arguments", CancellationToken.None));
        Assert.Equal(0, effect.CallCount);
        var completed = await runtime.ResumeAsync(result.RunId, scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Succeeded, completed.Status);
        Assert.Equal("approved-target", Assert.Single(effect.Calls).Arguments.GetProperty("target").GetString());
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task ApprovalForOneOperation_DoesNotApproveLaterCallWithDifferentArguments()
    {
        await using var scope = await TestScope.OpenAsync();
        var effect = new EffectTool();
        var client = new ScriptedClient(ToolResponse("record_effect", new { target = "first-target" }),
            ToolResponse("record_effect", new { target = "second-target" }, "second-effect-call"), FinalResponse());
        var runtime = scope.CreateOrchestrator(client, effect);
        var result = await runtime.RunAsync(scope.Request with { ApprovalRequiredTools = ["record_effect"] },
            NullAgentEventSink.Instance, CancellationToken.None);
        var first = Assert.Single((await scope.Store.GetAsync(result.RunId, CancellationToken.None))!.HumanRequests!);
        await runtime.DecideApprovalAsync(result.RunId, first.RequestId, true, scope.Request.Caller, CancellationToken.None);
        var waiting = await runtime.ResumeAsync(result.RunId, scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.AwaitingInput, waiting.Status);
        Assert.Equal(1, effect.CallCount);
        var checkpoint = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        Assert.Equal(2, checkpoint.HumanRequests!.Count);
        var second = Assert.Single(checkpoint.HumanRequests, request => request.Response is null);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.NotEqual(first.ExecutionId, second.ExecutionId);
        Assert.NotEqual(first.OperationFingerprint, second.OperationFingerprint);
        Assert.False(WorkflowIdentity.HasUnknownEffects(checkpoint));
        var unanswered = await runtime.ResumeAsync(result.RunId, scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.AwaitingInput, unanswered.Status);
        Assert.Equal(2, client.CallCount);
        Assert.Equal(1, effect.CallCount);

        await runtime.DecideApprovalAsync(result.RunId, second.RequestId, false, scope.Request.Caller, CancellationToken.None);
        var completed = await runtime.ResumeAsync(result.RunId, scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Succeeded, completed.Status);
        Assert.Equal("first-target", Assert.Single(effect.Calls).Arguments.GetProperty("target").GetString());
        Assert.Equal(3, client.CallCount);
        var saved = (await scope.Store.GetAsync(result.RunId, CancellationToken.None))!;
        Assert.True(saved.HumanRequests![0].Response!.Approved);
        Assert.False(saved.HumanRequests[1].Response!.Approved);
        Assert.True(saved.ToolExecutions![1].Result!.IsError);
    }

    [Fact]
    public async Task RoutingClarification_AfterRestartConsumesAnswerWithoutRepeatingPlanner()
    {
        await using var scope = await TestScope.OpenAsync();
        var agents = CreateAgentRegistry();
        var registry = new InMemoryToolRegistry([new ListAgentsTool(agents)]);
        var planner = new ScriptedClient([new AssistantTextDelta("""{"needs_clarification":true,"clarification_questions":["Which project should I inspect?"],"delegations":[]}"""), new CompletedDelta()]);
        var initialRuntime = scope.CreateOrchestrator(planner, registry: registry);
        registry.RegisterTools([new InvokeAgentTool(agents, initialRuntime)]);
        var initial = await initialRuntime.RunAsync(scope.Request, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.AwaitingInput, initial.Status);
        var checkpoint = (await scope.Store.GetAsync(initial.RunId, CancellationToken.None))!;
        var pending = Assert.Single(checkpoint.HumanRequests!);
        Assert.StartsWith("route:", pending.SourceKey);
        Assert.Null(pending.ExecutionId);
        Assert.Equal(1, planner.CallCount);

        await scope.ReopenAsync();
        var model = new ScriptedClient(FinalResponse());
        var resumedRegistry = new InMemoryToolRegistry([new ListAgentsTool(agents)]);
        var resumed = scope.CreateOrchestrator(model, registry: resumedRegistry);
        resumedRegistry.RegisterTools([new InvokeAgentTool(agents, resumed)]);
        await resumed.AnswerAsync(initial.RunId, pending.RequestId, "Inspect the IronPaw project", scope.Request.Caller, CancellationToken.None);
        var result = await resumed.ResumeAsync(initial.RunId, scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(initial.RunId, result.RunId);
        Assert.Equal(1, model.CallCount);
        Assert.Contains(Assert.Single(model.Requests).Messages,
            message => message.Role == ChatRole.User && message.Content.Contains("IronPaw project", StringComparison.Ordinal));
        Assert.Single(result.Transcript, message => message.Role == ChatRole.User && message.Content == scope.Request.UserInput);
        var saved = (await scope.Store.GetAsync(initial.RunId, CancellationToken.None))!;
        Assert.Equal(checkpoint.Usage!.ToolCalls, saved.Usage!.ToolCalls);
        Assert.Equal(checkpoint.Usage.ModelCalls + 1, saved.Usage.ModelCalls);
        Assert.Single(await scope.Store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PausingActiveModel_PreservesConsumedAttemptAndContinuesAfterRestart()
    {
        await using var scope = await TestScope.OpenAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new BlockingClient(entered);
        var runtime = scope.CreateOrchestrator(client);
        var runId = RunId.New();
        using var executionCancellation = new CancellationTokenSource();
        var active = runtime.RunAsync(scope.Request with { RequestedRunId = runId }, NullAgentEventSink.Instance, executionCancellation.Token);
        AgentRunResult paused;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await runtime.PauseAsync(runId, scope.Request.Caller, CancellationToken.None);
            paused = await active.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { await DrainAsync(active, executionCancellation); }
        Assert.Equal(AgentRunStatus.Paused, paused.Status);
        var checkpoint = (await scope.Store.GetAsync(runId, CancellationToken.None))!;
        Assert.Equal(WorkflowStatus.Paused, checkpoint.Status);
        Assert.Equal(WorkflowControl.Pause, checkpoint.Control);
        Assert.Equal(1, checkpoint.Usage!.ModelCalls);
        Assert.Null(checkpoint.ModelCalls![0].Response);
        await scope.ReopenAsync();
        var resumedClient = new ScriptedClient(FinalResponse());
        var result = await scope.CreateOrchestrator(resumedClient).ResumeAsync(runId, scope.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(runId, result.RunId);
        var saved = (await scope.Store.GetAsync(runId, CancellationToken.None))!;
        Assert.Equal(2, saved.Usage!.ModelCalls);
        Assert.Equal(1, saved.Usage.InterruptedModelCalls);
        Assert.Equal(1, resumedClient.CallCount);
    }

    [Fact]
    public async Task PausingQueuedRun_DoesNotAdmitProviderWork_AndResumesAfterRestart()
    {
        await using var scope = await TestScope.OpenAsync();
        using var scheduler = new SessionLaneScheduler();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var occupied = scheduler.EnqueueAsync(scope.Request.SessionKey, async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return 0;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var client = new ScriptedClient(FinalResponse());
        var runtime = scope.CreateOrchestrator(client, scheduler: scheduler);
        var runId = RunId.New();
        using var executionCancellation = new CancellationTokenSource();
        var queued = runtime.RunAsync(scope.Request with { RequestedRunId = runId }, NullAgentEventSink.Instance, executionCancellation.Token);
        try
        {
            await WaitUntilAsync(async () => await scope.Store.GetAsync(runId, CancellationToken.None) is not null);
            await runtime.PauseAsync(runId, scope.Request.Caller, CancellationToken.None);
            var paused = await queued.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(AgentRunStatus.Paused, paused.Status);
            Assert.Equal(0, client.CallCount);
            var checkpoint = (await scope.Store.GetAsync(runId, CancellationToken.None))!;
            Assert.Equal(WorkflowStatus.Paused, checkpoint.Status);
            Assert.Equal(WorkflowControl.Pause, checkpoint.Control);
            Assert.Equal(0, checkpoint.Usage!.ModelCalls);
            Assert.Null(checkpoint.Result);
        }
        finally
        {
            executionCancellation.Cancel();
            release.TrySetResult();
            await occupied;
            await DrainAsync(queued, executionCancellation);
        }

        await scope.ReopenAsync();
        var resumedClient = new ScriptedClient(FinalResponse());
        var result = await scope.CreateOrchestrator(resumedClient).ResumeAsync(runId, scope.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(runId, result.RunId);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(1, resumedClient.CallCount);
        Assert.Equal(WorkflowControl.None, (await scope.Store.GetAsync(runId, CancellationToken.None))!.Control);
    }

    [Fact]
    public async Task PermanentCancellation_LeavesUnknownEffectReconcilableWithoutRestoringExecution()
    {
        await using var scope = await TestScope.OpenAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = new EffectTool(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ToolResult.Success("unreachable");
        });
        var client = new ScriptedClient(ToolResponse("record_effect"), FinalResponse());
        var runtime = scope.CreateOrchestrator(client, effect);
        var runId = RunId.New();
        using var executionCancellation = new CancellationTokenSource();
        var active = runtime.RunAsync(scope.Request with { RequestedRunId = runId }, NullAgentEventSink.Instance, executionCancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await runtime.CancelAsync(runId, scope.Request.Caller, CancellationToken.None);
            await ObserveCancellationAsync(active);
        }
        finally { await DrainAsync(active, executionCancellation); }
        var cancelled = (await scope.Store.GetAsync(runId, CancellationToken.None))!;
        Assert.Equal(WorkflowControl.Cancel, cancelled.Control);
        Assert.True(WorkflowIdentity.HasUnknownEffects(cancelled));
        var execution = Assert.Single(cancelled.ToolExecutions!);
        await scope.ReopenAsync();
        var unused = new ScriptedClient();
        var resumedRuntime = scope.CreateOrchestrator(unused, effect);
        await Assert.ThrowsAsync<WorkflowCancelledException>(() => resumedRuntime.ResumeAsync(runId,
            scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
        await resumedRuntime.ReconcileToolAsync(runId, execution.ExecutionId, ToolResult.Success("verified effect completed"),
            "Verified the destination receipt after cancellation", scope.Request.Caller, CancellationToken.None);
        await Assert.ThrowsAsync<WorkflowCancelledException>(() => resumedRuntime.ResumeAsync(runId,
            scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
        var reconciled = (await scope.Store.GetAsync(runId, CancellationToken.None))!;
        Assert.Equal(WorkflowControl.Cancel, reconciled.Control);
        Assert.False(WorkflowIdentity.HasUnknownEffects(reconciled));
        Assert.NotNull(Assert.Single(reconciled.ToolExecutions!).Result);
        Assert.Equal(0, unused.CallCount);
        Assert.Equal(1, effect.CallCount);
    }

    [Fact]
    public async Task PausingStartedEffect_RequiresReconciliationBeforeResuming()
    {
        await using var scope = await TestScope.OpenAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = new EffectTool(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ToolResult.Success("unreachable");
        });
        var initial = scope.CreateOrchestrator(new ScriptedClient(ToolResponse("record_effect")), effect);
        using var executionCancellation = new CancellationTokenSource();
        var runId = RunId.New();
        var active = initial.RunAsync(scope.Request with { RequestedRunId = runId }, NullAgentEventSink.Instance, executionCancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await initial.PauseAsync(runId, scope.Request.Caller, CancellationToken.None);
            Assert.Equal(AgentRunStatus.Paused, (await active.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        }
        finally { await DrainAsync(active, executionCancellation); }
        var interrupted = (await scope.Store.GetAsync(runId, CancellationToken.None))!;
        Assert.Equal(WorkflowStatus.NeedsReconciliation, interrupted.Status);
        Assert.Equal(WorkflowControl.Pause, interrupted.Control);
        Assert.True(WorkflowIdentity.HasUnknownEffects(interrupted));
        await scope.ReopenAsync();
        var client = new ScriptedClient(FinalResponse());
        var runtime = scope.CreateOrchestrator(client, effect);
        await Assert.ThrowsAsync<WorkflowReconciliationRequiredException>(() => runtime.ResumeAsync(runId,
            scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
        Assert.Equal(0, client.CallCount);
        await runtime.ReconcileToolAsync(runId, Assert.Single(interrupted.ToolExecutions!).ExecutionId,
            ToolResult.Success("verified completed effect"), "Confirmed the effect in the destination",
            scope.Request.Caller, CancellationToken.None);
        var result = await runtime.ResumeAsync(runId, scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(1, effect.CallCount);
        Assert.Equal(1, client.CallCount);
        var saved = (await scope.Store.GetAsync(runId, CancellationToken.None))!;
        Assert.Equal(WorkflowControl.None, saved.Control);
        Assert.Equal(1, saved.Usage!.ToolCalls);
    }

    [Theory]
    [InlineData(WorkflowControl.Pause)]
    [InlineData(WorkflowControl.Cancel)]
    public async Task ControlRacingWithReturnedReceipt_PreservesConfirmedEffect(WorkflowControl control)
    {
        await using var scope = await TestScope.OpenAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = new EffectTool(async (_, _) =>
        {
            entered.TrySetResult();
            // The destination has completed and will return a receipt even if
            // cancellation reaches the client while that receipt is in flight.
            await release.Task;
            return ToolResult.Success("confirmed destination receipt");
        });
        var runtime = scope.CreateOrchestrator(new ScriptedClient(ToolResponse("record_effect")), effect);
        using var executionCancellation = new CancellationTokenSource();
        var runId = RunId.New();
        var active = runtime.RunAsync(scope.Request with { RequestedRunId = runId }, NullAgentEventSink.Instance, executionCancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (control == WorkflowControl.Pause) await runtime.PauseAsync(runId, scope.Request.Caller, CancellationToken.None);
            else await runtime.CancelAsync(runId, scope.Request.Caller, CancellationToken.None);
            if (control == WorkflowControl.Cancel)
                await Assert.ThrowsAsync<WorkflowCancelledException>(() => runtime.ResumeAsync(runId,
                    scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
            else
                await Assert.ThrowsAsync<WorkflowConflictException>(() => runtime.ResumeAsync(runId,
                    scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
            release.TrySetResult();
            Assert.Equal(control == WorkflowControl.Pause ? AgentRunStatus.Paused : AgentRunStatus.Cancelled,
                (await active.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        }
        finally
        {
            release.TrySetResult();
            await DrainAsync(active, executionCancellation);
        }
        var settled = (await scope.Store.GetAsync(runId, CancellationToken.None))!;
        Assert.Equal(control, settled.Control);
        Assert.False(WorkflowIdentity.HasUnknownEffects(settled));
        Assert.Equal(WorkflowToolState.Completed, Assert.Single(settled.ToolExecutions!).State);
        Assert.Equal("confirmed destination receipt", Assert.Single(settled.ToolExecutions!).Result!.Content);
        Assert.Equal(1, settled.Usage!.ToolCalls);
        await scope.ReopenAsync();
        var model = new ScriptedClient(FinalResponse());
        var resumed = scope.CreateOrchestrator(model, effect);
        if (control == WorkflowControl.Cancel)
        {
            await Assert.ThrowsAsync<WorkflowCancelledException>(() => resumed.ResumeAsync(runId,
                scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
            Assert.Equal(0, model.CallCount);
        }
        else
        {
            Assert.Equal(AgentRunStatus.Succeeded, (await resumed.ResumeAsync(runId,
                scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None)).Status);
            Assert.Equal(1, model.CallCount);
        }
        Assert.Equal(1, effect.CallCount);
    }

    [Fact]
    public async Task ProviderReturningToolCallAfterPermanentCancellation_CannotAdmitEffect()
    {
        await using var scope = await TestScope.OpenAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new UncooperativeClient(entered, release);
        var effect = new EffectTool();
        var runtime = scope.CreateOrchestrator(model, effect);
        using var executionCancellation = new CancellationTokenSource();
        var runId = RunId.New();
        var active = runtime.RunAsync(scope.Request with { RequestedRunId = runId }, NullAgentEventSink.Instance, executionCancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await runtime.CancelAsync(runId, scope.Request.Caller, CancellationToken.None);
            release.TrySetResult();
            await ObserveCancellationAsync(active);
        }
        finally
        {
            release.TrySetResult();
            await DrainAsync(active, executionCancellation);
        }
        var cancelled = (await scope.Store.GetAsync(runId, CancellationToken.None))!;
        Assert.Equal(WorkflowControl.Cancel, cancelled.Control);
        Assert.Equal(WorkflowStatus.Cancelled, cancelled.Status);
        Assert.Equal(0, effect.CallCount);
        Assert.Equal(0, cancelled.Usage!.ToolCalls);
        Assert.False(WorkflowIdentity.HasUnknownEffects(cancelled));
        await Assert.ThrowsAsync<WorkflowCancelledException>(() => runtime.ResumeAsync(runId,
            scope.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task ChildHumanWait_SuspendsParentAndReusesStableChildOnResume()
    {
        await using var scope = await TestScope.OpenAsync();
        var human = new HumanInputTool();
        var model = new DelegatingClient();
        var agents = CreateAgentRegistry();
        var registry = new InMemoryToolRegistry([human]);
        var runtime = scope.CreateOrchestrator(model, registry: registry);
        registry.RegisterTools([new InvokeAgentTool(agents, runtime)]);
        var initial = await runtime.RunAsync(scope.Request, NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.AwaitingInput, initial.Status);
        var checkpoints = await scope.Store.ListAsync(CancellationToken.None);
        var parent = Assert.Single(checkpoints, checkpoint => checkpoint.ParentRunId is null);
        var child = Assert.Single(checkpoints, checkpoint => checkpoint.ParentRunId == parent.RunId);
        Assert.Equal(WorkflowStatus.AwaitingInput, parent.Status);
        Assert.Equal(WorkflowStatus.AwaitingInput, child.Status);
        Assert.Null(parent.Result);
        Assert.Equal(WorkflowToolState.WaitingForChild, Assert.Single(parent.ToolExecutions!).State);
        Assert.Equal(child.RunId, Assert.Single(parent.ToolExecutions!).ChildRunId);
        var pending = Assert.Single(child.HumanRequests!);
        Assert.Equal(2, model.CallCount);

        await scope.ReopenAsync();
        var resumedModel = new DelegatingClient();
        var resumedRegistry = new InMemoryToolRegistry([human]);
        var resumedRuntime = scope.CreateOrchestrator(resumedModel, registry: resumedRegistry);
        resumedRegistry.RegisterTools([new InvokeAgentTool(agents, resumedRuntime)]);
        var stillWaiting = await resumedRuntime.ResumeAsync(new RunId(parent.RunId), scope.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.AwaitingInput, stillWaiting.Status);
        Assert.Equal(0, resumedModel.CallCount);
        await resumedRuntime.AnswerAsync(new RunId(child.RunId), pending.RequestId, "the verified branch",
            scope.Request.Caller, CancellationToken.None);
        var result = await resumedRuntime.ResumeAsync(new RunId(parent.RunId), scope.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(parent.RunId, result.RunId.Value);
        Assert.Equal(2, resumedModel.CallCount);
        var final = await scope.Store.ListAsync(CancellationToken.None);
        Assert.Equal(2, final.Count);
        Assert.Equal(child.RunId, Assert.Single(final, checkpoint => checkpoint.ParentRunId is not null).RunId);
        Assert.All(final, checkpoint => Assert.Equal(WorkflowStatus.Succeeded, checkpoint.Status));
        Assert.Equal(0, human.CallCount);
    }

    [Fact]
    public async Task CancellingRootWhileChildRuns_PreventsAllDescendantAdmissions()
    {
        await using var scope = await TestScope.OpenAsync();
        var childEntered = new TaskCompletionSource<RunId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = new EffectTool();
        var model = new DelegatingClient(childEntered);
        var registry = new InMemoryToolRegistry([effect]);
        var runtime = scope.CreateOrchestrator(model, registry: registry);
        registry.RegisterTools([new InvokeAgentTool(CreateAgentRegistry(), runtime)]);
        var runId = RunId.New();
        using var executionCancellation = new CancellationTokenSource();
        var active = runtime.RunAsync(scope.Request with { RequestedRunId = runId }, NullAgentEventSink.Instance, executionCancellation.Token);
        RunId childRunId;
        try
        {
            childRunId = await childEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await runtime.CancelAsync(runId, scope.Request.Caller, CancellationToken.None);
            await ObserveCancellationAsync(active);
        }
        finally { await DrainAsync(active, executionCancellation); }
        Assert.Equal(0, effect.CallCount);
        Assert.Equal(2, model.CallCount);
        var root = (await scope.Store.GetAsync(runId, CancellationToken.None))!;
        Assert.Equal(WorkflowControl.Cancel, root.Control);
        var usage = await ((IWorkflowStore)scope.Store).GetUsageAsync(runId, CancellationToken.None);
        await scope.ReopenAsync();
        var unused = new ScriptedClient(FinalResponse());
        var resumed = scope.CreateOrchestrator(unused, effect);
        await Assert.ThrowsAsync<WorkflowCancelledException>(() => resumed.ResumeAsync(runId, scope.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowCancelledException>(() => resumed.ResumeAsync(childRunId, scope.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None));
        Assert.Equal(usage, await ((IWorkflowStore)scope.Store).GetUsageAsync(runId, CancellationToken.None));
        Assert.Equal(0, unused.CallCount);
        Assert.Equal(0, effect.CallCount);
    }

    private static InMemoryAgentRegistry CreateAgentRegistry()
    {
        var agents = new InMemoryAgentRegistry();
        agents.Register(new AgentDefinition("specialist", "Specialist", "Completes the delegated task", "Complete the task",
            new ToolSelectionPolicy(ToolProfile.Full)));
        return agents;
    }

    private static LlmDelta[] ToolResponse(string name, object? arguments = null, string? callId = null) =>
        [new ToolCallDelta(callId ?? "call-" + name, name, JsonSerializer.SerializeToElement(arguments ?? new { })), new CompletedDelta()];
    private static LlmDelta[] FinalResponse() => [new AssistantTextDelta("finished"), new CompletedDelta()];

    private static async Task ObserveCancellationAsync(Task<AgentRunResult> active)
    {
        Assert.Equal(AgentRunStatus.Cancelled, (await active.WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }

    private static async Task DrainAsync(Task active, CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        try { await active.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* Cleanup must preserve the original assertion/operation failure. */ }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class TestScope : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "IronPawHumanControlTests", Guid.NewGuid().ToString("n"));
        private readonly List<SessionLaneScheduler> _schedulers = [];
        public CSharpDbWorkflowStore Store { get; private set; } = null!;
        public JsonTranscriptStore Transcripts => new(Path.Combine(_root, "transcripts"));
        public AgentRunRequest Request { get; } = new(new SessionKey("human-workflow"), "perform the task", "local", "test",
            new ToolSelectionPolicy(ToolProfile.Full), CallerIdentity.Authenticated("workflow-owner", "Workflow owner"),
            SystemPromptOverride: "A fixed human control test prompt", Budget: new WorkflowBudget(12, 12));

        public static async Task<TestScope> OpenAsync()
        {
            var scope = new TestScope();
            Directory.CreateDirectory(scope._root);
            scope.Store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(scope._root, "workflows.cdb"));
            return scope;
        }

        public async Task ReopenAsync()
        {
            await Store.DisposeAsync();
            Store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(_root, "workflows.cdb"));
        }

        public IResumableAgentOrchestrator CreateOrchestrator(ILlmClient client, params ITool[] tools)
            => CreateOrchestrator(client, registry: new InMemoryToolRegistry(tools));

        public IResumableAgentOrchestrator CreateOrchestrator(ILlmClient client,
            SessionLaneScheduler? scheduler = null, InMemoryToolRegistry? registry = null, IWorkflowStore? workflowStore = null)
        {
            scheduler ??= new SessionLaneScheduler();
            _schedulers.Add(scheduler);
            return new AgentOrchestrator(scheduler,
                new FileSessionStore(new FileSessionStoreOptions(Path.Combine(_root, "sessions.json"), TimeSpan.Zero)),
                new JsonTranscriptStore(Path.Combine(_root, "transcripts")), registry ?? new InMemoryToolRegistry([]),
                new ToolExecutor(), new ToolSchemaNormalizer(), client, new TranscriptSanitizer(),
                new ToolResultTruncator(), new DefaultSystemPromptBuilder(), workflowStore: workflowStore ?? Store);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var scheduler in _schedulers) scheduler.Dispose();
            await Store.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CancelBeforeSuspensionStore(IWorkflowStore inner) : IWorkflowStore
    {
        public int CancellationCount { get; private set; }
        public Task CreateAsync(WorkflowCheckpoint checkpoint, CancellationToken ct) => inner.CreateAsync(checkpoint, ct);
        public Task<WorkflowCheckpoint?> GetAsync(RunId runId, CancellationToken ct) => inner.GetAsync(runId, ct);
        public Task<IReadOnlyList<WorkflowCheckpoint>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<IReadOnlyList<WorkflowJournalEntry>> ReadJournalAsync(RunId runId, CancellationToken ct) => inner.ReadJournalAsync(runId, ct);
        public async Task<WorkflowCheckpoint> SaveAsync(WorkflowCheckpoint checkpoint, int expectedRevision,
            string eventType, CancellationToken ct)
        {
            if (eventType == "run_suspended" && CancellationCount == 0)
            {
                CancellationCount++;
                var root = (await inner.GetAsync(new RunId(checkpoint.RootRunId), ct))!;
                await inner.SaveAsync(root with { Control = WorkflowControl.Cancel }, root.Revision, "cancel_requested", ct);
            }
            return await inner.SaveAsync(checkpoint, expectedRevision, eventType, ct);
        }
    }

    private sealed class EffectTool : ITool
    {
        private readonly Func<ToolExecutionContext, CancellationToken, Task<ToolResult>>? _action;
        public int CallCount { get; private set; }
        public List<ToolCall> Calls { get; } = [];
        public ToolDescriptor Descriptor { get; }
        public EffectTool(Func<ToolExecutionContext, CancellationToken, Task<ToolResult>>? action = null,
            string schema = """{"type":"object","properties":{}}""")
        {
            _action = action;
            Descriptor = new("record_effect", "Records an externally visible effect", JsonSerializer.Deserialize<JsonElement>(schema), "group:system");
        }
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
        {
            CallCount++;
            Calls.Add(call);
            return _action is null ? ToolResult.Success("effect completed") : await _action(context, ct);
        }
    }

    private sealed class HumanInputTool : IHumanInputTool
    {
        public int CallCount { get; private set; }
        public ToolDescriptor Descriptor { get; } = new("request_input", "Asks the human for a durable answer",
            JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"question":{"type":"string"}},"required":["question"]}"""), "group:system");
        public string GetQuestion(ToolCall call) => call.Arguments.GetProperty("question").GetString()!;
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
        {
            CallCount++;
            throw new InvalidOperationException("Human input must suspend durably instead of invoking the tool.");
        }
    }

    private sealed class ScriptedClient(params LlmDelta[][] responses) : ILlmClient
    {
        public int CallCount { get; private set; }
        public List<LlmRequest> Requests { get; } = [];
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            var index = CallCount++;
            Requests.Add(request);
            if (index >= responses.Length) throw new InvalidOperationException("Unexpected provider call while replaying or waiting for a human");
            foreach (var delta in responses[index]) { ct.ThrowIfCancellationRequested(); yield return delta; }
        }
    }

    private sealed class BlockingClient(TaskCompletionSource entered) : ILlmClient
    {
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield return new AssistantTextDelta("unreachable");
            yield return new CompletedDelta();
        }
    }

    private sealed class UncooperativeClient(TaskCompletionSource entered, TaskCompletionSource release) : ILlmClient
    {
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            entered.TrySetResult();
            await release.Task;
            // Deliberately ignore ct: the runtime still must reject admission.
            yield return new ToolCallDelta("late-effect", "record_effect", JsonSerializer.SerializeToElement(new { }));
            yield return new CompletedDelta();
        }
    }

    private sealed class DelegatingClient(TaskCompletionSource<RunId>? childEntered = null) : ILlmClient
    {
        public int CallCount { get; private set; }
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            CallCount++;
            var isChild = request.Messages.Last(message => message.Role == ChatRole.User).Content == "child task";
            if (!request.Messages.Any(message => message.Role == ChatRole.Tool))
            {
                if (!isChild)
                    yield return new ToolCallDelta("delegate-call", "invoke_agent", JsonSerializer.SerializeToElement(new { agent_id = "specialist", task = "child task" }));
                else if (childEntered is not null)
                {
                    childEntered.TrySetResult(request.RunId);
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    yield return new ToolCallDelta("forbidden-after-cancel", "record_effect", JsonSerializer.SerializeToElement(new { }));
                }
                else
                    yield return new ToolCallDelta("human-call", "request_input", JsonSerializer.SerializeToElement(new { question = "Which branch should I verify?" }));
            }
            else yield return new AssistantTextDelta(isChild ? "child completed with the human answer" : "parent completed");
            yield return new CompletedDelta();
        }
    }
}
