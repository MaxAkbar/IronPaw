using System.Diagnostics;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.Workflow.CrashHarness;

namespace IronPaw.AgentCore.Tests;

public sealed class WorkflowCrashRecoveryTests
{
    [Fact]
    public async Task KilledHost_ReopenResumesCommittedToolAndModelWithoutRepeatingEffect()
    {
        using var scope = new CrashScope();
        var runId = await scope.StartAndKillAsync(CrashPoint.AfterCompletedTool);
        await using var store = await CSharpDbWorkflowStore.OpenAsync(CrashFixture.DatabasePath(scope.Root));
        var recovered = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(runId, recovered.RunId);
        Assert.Equal(WorkflowStatus.Interrupted, recovered.Status);
        Assert.Null(recovered.LeaseOwner);
        Assert.NotNull(Assert.Single(recovered.ToolExecutions!).Result);
        Assert.Equal(2, recovered.Usage!.ModelCalls);
        Assert.Equal(1, recovered.Usage.ToolCalls);
        Assert.Contains(await store.ReadJournalAsync(new RunId(runId), CancellationToken.None), entry => entry.EventType == "recovered");

        using var scheduler = new SessionLaneScheduler();
        var model = new RecoveryModel();
        var tool = new CrashEffectTool(scope.Root);
        var orchestrator = CrashFixture.CreateOrchestrator(scope.Root, store, scheduler, model, tool);
        var result = await orchestrator.ResumeAsync(new RunId(runId), CrashFixture.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);

        Assert.Equal(runId, result.RunId.Value);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal("recovered successfully", result.FinalAssistantMessage);
        Assert.Equal(1, model.CallCount);
        Assert.Equal(0, tool.CallCount);
        Assert.Single(await File.ReadAllLinesAsync(CrashFixture.EffectsPath(scope.Root)));
        Assert.Contains(Assert.Single(model.Requests).Messages,
            message => message.Role == ChatRole.Tool && message.Content == "effect recorded");
        var completed = await store.GetAsync(result.RunId, CancellationToken.None);
        Assert.Equal(WorkflowStatus.Succeeded, completed!.Status);
        Assert.Equal(3, completed.Usage!.ModelCalls);
        Assert.Equal(1, completed.Usage.ToolCalls);
        Assert.Equal(10, completed.Usage.InputTokens);
        Assert.Equal(3, completed.Usage.OutputTokens);
    }

    [Fact]
    public async Task KilledHostAfterEffect_ReopenBlocksUnknownOutcomeUntilExplicitReconciliation()
    {
        using var scope = new CrashScope();
        var runId = await scope.StartAndKillAsync(CrashPoint.AfterEffectBeforeToolResult);
        await using var store = await CSharpDbWorkflowStore.OpenAsync(CrashFixture.DatabasePath(scope.Root));
        var recovered = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(WorkflowStatus.NeedsReconciliation, recovered.Status);
        Assert.Null(recovered.LeaseOwner);
        var execution = Assert.Single(recovered.ToolExecutions!);
        Assert.Null(execution.Result);
        Assert.Equal($"{runId}:{execution.ExecutionId}", Assert.Single(await File.ReadAllLinesAsync(CrashFixture.EffectsPath(scope.Root))));

        using var scheduler = new SessionLaneScheduler();
        var model = new RecoveryModel();
        var tool = new CrashEffectTool(scope.Root);
        var orchestrator = CrashFixture.CreateOrchestrator(scope.Root, store, scheduler, model, tool);
        await Assert.ThrowsAsync<WorkflowReconciliationRequiredException>(() => orchestrator.ResumeAsync(new RunId(runId),
            CrashFixture.Request.Caller, NullAgentEventSink.Instance, CancellationToken.None));
        Assert.Equal(0, model.CallCount);
        Assert.Equal(0, tool.CallCount);
        Assert.Single(await File.ReadAllLinesAsync(CrashFixture.EffectsPath(scope.Root)));

        await orchestrator.ReconcileToolAsync(new RunId(runId), execution.ExecutionId, ToolResult.Success("effect recorded"),
            "Confirmed the matching execution id in the destination file", CrashFixture.Request.Caller, CancellationToken.None);
        var result = await orchestrator.ResumeAsync(new RunId(runId), CrashFixture.Request.Caller,
            NullAgentEventSink.Instance, CancellationToken.None);
        Assert.Equal(runId, result.RunId.Value);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal(1, model.CallCount);
        Assert.Equal(0, tool.CallCount);
        Assert.Single(await File.ReadAllLinesAsync(CrashFixture.EffectsPath(scope.Root)));
        var completed = await store.GetAsync(result.RunId, CancellationToken.None);
        Assert.Equal(1, completed!.Usage!.ToolCalls);
        Assert.NotNull(Assert.Single(completed.ToolExecutions!).ReconciliationNote);
    }

    private sealed class CrashScope : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "IronPawCrashTests", Guid.NewGuid().ToString("n"));
        public CrashScope() => Directory.CreateDirectory(Root);

        public async Task<string> StartAndKillAsync(CrashPoint point)
        {
            var testAssembly = typeof(WorkflowCrashRecoveryTests).Assembly.Location;
            var start = new ProcessStartInfo("dotnet")
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardError = true, RedirectStandardOutput = true
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
                while (!File.Exists(CrashFixture.ReadyPath(Root)))
                {
                    if (process.HasExited)
                        throw new InvalidOperationException($"Crash harness exited early ({process.ExitCode}): {await stderr}\n{await stdout}");
                    await Task.Delay(25, timeout.Token);
                }
                var runId = await File.ReadAllTextAsync(CrashFixture.ReadyPath(Root), timeout.Token);
                Assert.False(string.IsNullOrWhiteSpace(runId));
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
