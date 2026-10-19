using System.Diagnostics;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.Workflow.CrashHarness;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class WorkflowCrashRecoveryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task KilledHostWithReservedModel_ChargesUnknownTokensAndUncleanTimeWithoutAnotherProviderCall(bool advanceClock)
    {
        using var scope = new CrashScope();
        var runId = await scope.StartAndKillAsync(CrashPoint.AfterReservedModelAttempt);
        var clock = advanceClock ? new RecoveryClock(DateTimeOffset.UtcNow.AddMinutes(2)) : TimeProvider.System;
        await using var store = await scope.OpenAfterTerminationAsync(clock);
        var recovered = (await store.GetAsync(new RunId(runId), CancellationToken.None))!;
        var attempt = Assert.ContainsSingle(Assert.ContainsSingle(recovered.ModelCalls!).Attempts!);
        Assert.AreEqual(WorkflowReservationState.Unknown, attempt.State);
        Assert.AreEqual(2048, attempt.ReservedTokens);
        var budget = await store.GetBudgetAsync(new RunId(runId), CancellationToken.None);
        Assert.AreEqual(2048, budget.ChargedTokens);
        Assert.AreEqual(0, budget.ReservedTokens);
        Assert.IsTrue(budget.ExecutionTime > TimeSpan.Zero);
        if (advanceClock) Assert.AreEqual(TimeSpan.Zero, budget.RemainingExecutionTime);
        using var scheduler = new SessionLaneScheduler();
        var model = new RecoveryModel();
        var effect = new CrashEffectTool(scope.Root);
        var runtime = CrashFixture.CreateOrchestrator(scope.Root, store, scheduler, model, effect);
        var result = await runtime.ResumeAsync(new RunId(runId), CrashFixture.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);
        Assert.AreEqual(AgentRunStatus.BudgetExhausted, result.Status);
        Assert.AreEqual(0, model.CallCount);
        Assert.AreEqual(0, effect.CallCount);
        Assert.IsFalse(File.Exists(CrashFixture.EffectsPath(scope.Root)));
        Assert.AreEqual(2048, (await store.GetBudgetAsync(new RunId(runId), CancellationToken.None)).ChargedTokens);
    }

    private sealed class RecoveryClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [TestMethod]
    public async Task KilledHostWhileAwaitingHuman_ReopenRetainsRequestAndResumesWithoutRepeatingEffect()
    {
        using var scope = new CrashScope();
        var runId = await scope.StartAndKillAsync(CrashPoint.AfterHumanWait);
        await using var store = await scope.OpenAfterTerminationAsync();
        var recovered = Assert.ContainsSingle(await store.ListAsync(CancellationToken.None));
        Assert.AreEqual(runId, recovered.RunId);
        Assert.AreEqual(WorkflowStatus.AwaitingInput, recovered.Status);
        Assert.IsNull(recovered.Result);
        Assert.IsNull(recovered.LeaseOwner);
        var pending = Assert.ContainsSingle(recovered.HumanRequests!);
        Assert.IsNull(pending.Response);
        Assert.AreEqual(WorkflowHumanRequestKind.Input, pending.Kind);
        Assert.IsFalse(WorkflowIdentity.HasUnknownEffects(recovered));
        Assert.IsNotNull(recovered.ToolExecutions![0].Result);
        Assert.AreEqual(WorkflowToolState.Planned, recovered.ToolExecutions[1].State);
        Assert.ContainsSingle(await File.ReadAllLinesAsync(CrashFixture.EffectsPath(scope.Root)));

        using var scheduler = new SessionLaneScheduler();
        var model = new RecoveryModel();
        var tool = new CrashEffectTool(scope.Root);
        var orchestrator = CrashFixture.CreateOrchestrator(scope.Root, store, scheduler, model, tool, new CrashHumanInputTool());
        var waiting = await orchestrator.ResumeAsync(new RunId(runId), CrashFixture.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);
        Assert.AreEqual(AgentRunStatus.AwaitingInput, waiting.Status);
        Assert.AreEqual(0, model.CallCount);
        Assert.AreEqual(0, tool.CallCount);
        await orchestrator.AnswerAsync(new RunId(runId), pending.RequestId, "Use the verified release branch",
            CrashFixture.Request.Caller, CancellationToken.None);
        var result = await orchestrator.ResumeAsync(new RunId(runId), CrashFixture.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);
        Assert.AreEqual(runId, result.RunId.Value);
        Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
        Assert.AreEqual(1, model.CallCount);
        Assert.AreEqual(0, tool.CallCount);
        Assert.ContainsSingle(await File.ReadAllLinesAsync(CrashFixture.EffectsPath(scope.Root)));
        Assert.Contains(message => message.Role == ChatRole.Tool && message.Content.Contains("verified release branch", StringComparison.Ordinal), Assert.ContainsSingle(model.Requests).Messages);
        var completed = (await store.GetAsync(result.RunId, CancellationToken.None))!;
        Assert.AreEqual(WorkflowStatus.Succeeded, completed.Status);
        Assert.AreEqual(recovered.TaskId, completed.TaskId);
        Assert.AreEqual(recovered.Usage!.ToolCalls, completed.Usage!.ToolCalls);
        Assert.AreEqual(recovered.Usage.ModelCalls + 1, completed.Usage.ModelCalls);
        Assert.AreEqual(pending.RequestId, Assert.ContainsSingle(completed.HumanRequests!).RequestId);
        Assert.AreEqual("Use the verified release branch", Assert.ContainsSingle(completed.HumanRequests!).Response!.Answer);
    }

    [TestMethod]
    public async Task KilledHost_ReopenResumesCommittedToolAndModelWithoutRepeatingEffect()
    {
        using var scope = new CrashScope();
        var runId = await scope.StartAndKillAsync(CrashPoint.AfterCompletedTool);
        await using var store = await scope.OpenAfterTerminationAsync();
        var recovered = Assert.ContainsSingle(await store.ListAsync(CancellationToken.None));
        Assert.AreEqual(runId, recovered.RunId);
        Assert.AreEqual(WorkflowStatus.Interrupted, recovered.Status);
        Assert.IsNull(recovered.LeaseOwner);
        Assert.IsNotNull(Assert.ContainsSingle(recovered.ToolExecutions!).Result);
        Assert.AreEqual(2, recovered.Usage!.ModelCalls);
        Assert.AreEqual(1, recovered.Usage.ToolCalls);
        Assert.Contains(entry => entry.EventType == "recovered", await store.ReadJournalAsync(new RunId(runId), CancellationToken.None));

        using var scheduler = new SessionLaneScheduler();
        var model = new RecoveryModel();
        var tool = new CrashEffectTool(scope.Root);
        var orchestrator = CrashFixture.CreateOrchestrator(scope.Root, store, scheduler, model, tool);
        var result = await orchestrator.ResumeAsync(new RunId(runId), CrashFixture.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);

        Assert.AreEqual(runId, result.RunId.Value);
        Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
        Assert.AreEqual("recovered successfully", result.FinalAssistantMessage);
        Assert.AreEqual(1, model.CallCount);
        Assert.AreEqual(0, tool.CallCount);
        Assert.ContainsSingle(await File.ReadAllLinesAsync(CrashFixture.EffectsPath(scope.Root)));
        Assert.Contains(message => message.Role == ChatRole.Tool && message.Content == "effect recorded", Assert.ContainsSingle(model.Requests).Messages);
        var completed = await store.GetAsync(result.RunId, CancellationToken.None);
        Assert.AreEqual(WorkflowStatus.Succeeded, completed!.Status);
        Assert.AreEqual(3, completed.Usage!.ModelCalls);
        Assert.AreEqual(1, completed.Usage.ToolCalls);
        Assert.AreEqual(10, completed.Usage.InputTokens);
        Assert.AreEqual(3, completed.Usage.OutputTokens);
    }

    [TestMethod]
    public async Task KilledHostAfterEffect_ReopenBlocksUnknownOutcomeUntilExplicitReconciliation()
    {
        using var scope = new CrashScope();
        var runId = await scope.StartAndKillAsync(CrashPoint.AfterEffectBeforeToolResult);
        await using var store = await scope.OpenAfterTerminationAsync();
        var recovered = Assert.ContainsSingle(await store.ListAsync(CancellationToken.None));
        Assert.AreEqual(WorkflowStatus.NeedsReconciliation, recovered.Status);
        Assert.IsNull(recovered.LeaseOwner);
        var execution = Assert.ContainsSingle(recovered.ToolExecutions!);
        Assert.IsNull(execution.Result);
        Assert.AreEqual($"{runId}:{execution.ExecutionId}", Assert.ContainsSingle(await File.ReadAllLinesAsync(CrashFixture.EffectsPath(scope.Root))));

        using var scheduler = new SessionLaneScheduler();
        var model = new RecoveryModel();
        var tool = new CrashEffectTool(scope.Root);
        var orchestrator = CrashFixture.CreateOrchestrator(scope.Root, store, scheduler, model, tool);
        await Assert.ThrowsExactlyAsync<WorkflowReconciliationRequiredException>(() => orchestrator.ResumeAsync(new RunId(runId),
            CrashFixture.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
        Assert.AreEqual(0, model.CallCount);
        Assert.AreEqual(0, tool.CallCount);
        Assert.ContainsSingle(await File.ReadAllLinesAsync(CrashFixture.EffectsPath(scope.Root)));

        await orchestrator.ReconcileToolAsync(new RunId(runId), execution.ExecutionId, ToolResult.Success("effect recorded"),
            "Confirmed the matching execution id in the destination file", CrashFixture.Request.Caller, CancellationToken.None);
        var result = await orchestrator.ResumeAsync(new RunId(runId), CrashFixture.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);
        Assert.AreEqual(runId, result.RunId.Value);
        Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
        Assert.AreEqual(1, model.CallCount);
        Assert.AreEqual(0, tool.CallCount);
        Assert.ContainsSingle(await File.ReadAllLinesAsync(CrashFixture.EffectsPath(scope.Root)));
        var completed = await store.GetAsync(result.RunId, CancellationToken.None);
        Assert.AreEqual(1, completed!.Usage!.ToolCalls);
        Assert.IsNotNull(Assert.ContainsSingle(completed.ToolExecutions!).ReconciliationNote);
    }

    private sealed class CrashScope : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "IronPawCrashTests", Guid.NewGuid().ToString("n"));
        public CrashScope() => Directory.CreateDirectory(Root);

        public Task<CSharpDbWorkflowStore> OpenAfterTerminationAsync(TimeProvider? clock = null) =>
            IronPaw.Testing.ProcessExitRecovery.OpenWorkflowAsync(CrashFixture.DatabasePath(Root), timeProvider: clock);

        public async Task<string> StartAndKillAsync(CrashPoint point)
        {
            var testAssembly = typeof(WorkflowCrashRecoveryTests).Assembly.Location;
            var start = new ProcessStartInfo("dotnet")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            // Run against the test project's runtime/dependency graph so the
            // executable project reference resolves its copied dependencies.
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"), typeof(CrashFixture).Assembly.Location, Root, point.ToString() })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start crash harness");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try
            {
                string? runId = null;
                while (runId is null)
                {
                    if (process.HasExited)
                        throw new InvalidOperationException($"Crash harness exited early ({process.ExitCode}): {await stderr}\n{await stdout}");
                    if (File.Exists(CrashFixture.ReadyPath(Root)))
                    {
                        try { runId = await File.ReadAllTextAsync(CrashFixture.ReadyPath(Root), timeout.Token); }
                        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
                        { /* Windows can retain a brief sharing lock after atomic publication. */ }
                    }
                    if (runId is not null) break;
                    await Task.Delay(25, timeout.Token);
                }
                Assert.IsFalse(string.IsNullOrWhiteSpace(runId));
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(timeout.Token);
                return runId;
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
