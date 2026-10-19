using System.Diagnostics;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.Workflow.CrashHarness;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class TaskGraphCrashRecoveryTests
{
    [TestMethod]
    public async Task KilledHostAfterEffect_RequiresReconciliationThenVerifiesWithoutRepeatingEffect()
    {
        using var scope = new GraphCrashScope();
        var graphId = await scope.StartAndKillAsync(CrashPoint.AfterGraphEffect);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var store = await IronPaw.Testing.ProcessExitRecovery.OpenWorkflowAsync(CrashFixture.DatabasePath(scope.Root), timeout.Token);
        var recovered = (await store.GetTaskGraphAsync(graphId, timeout.Token))!;
        var unknown = Assert.ContainsSingle(recovered.Tasks);
        Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, unknown.Status);
        Assert.IsNotNull(unknown.AttemptId);
        Assert.IsNull(unknown.LeaseToken);
        Assert.IsNull(unknown.Result);
        Assert.IsNull(unknown.Acceptance);
        Assert.AreEqual($"{graphId}:{unknown.AttemptId}", Assert.ContainsSingle(await File.ReadAllLinesAsync(CrashFixture.EffectsPath(scope.Root), timeout.Token)));
        Assert.Contains(entry => entry.EventType == "recovered", await store.ReadTaskGraphJournalAsync(graphId, timeout.Token));
        var executor = new NeverRepeatExecutor();
        var verifier = new RecoveryVerifier(scope.Root);
        var scheduler = new TaskGraphScheduler(store, executor, verifier);

        var blocked = await scheduler.ResumeAsync(graphId, TaskGraphCrashFixture.Caller, timeout.Token);

        Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, Assert.ContainsSingle(blocked.Tasks).Status);
        Assert.AreEqual(0, executor.CallCount);
        Assert.AreEqual(0, verifier.CallCount);
        var established = TaskGraphCrashFixture.EstablishedResult(scope.Root, graphId, recovered.DefinitionFingerprint, unknown.AttemptId!);
        var reconciled = await scheduler.ReconcileAsync(graphId, TaskGraphCrashFixture.TaskId, established,
            "Inspected the durable file and confirmed the matching attempt was recorded once", TaskGraphCrashFixture.Caller, timeout.Token);
        Assert.AreEqual(TaskGraphTaskStatus.AwaitingAcceptance, Assert.ContainsSingle(reconciled.Tasks).Status);
        Assert.AreEqual(unknown.AttemptId, Assert.ContainsSingle(reconciled.Tasks).AttemptId);
        Assert.AreEqual(0, verifier.CallCount);

        var resumed = await scheduler.ResumeAsync(graphId, TaskGraphCrashFixture.Caller, timeout.Token);

        AssertAccepted(resumed, unknown.AttemptId!);
        Assert.IsNotNull(Assert.ContainsSingle(resumed.Tasks).ReconciliationNote);
        Assert.AreEqual(0, executor.CallCount);
        Assert.AreEqual(1, verifier.CallCount);
        Assert.ContainsSingle(await File.ReadAllLinesAsync(CrashFixture.EffectsPath(scope.Root), timeout.Token));
    }

    [TestMethod]
    public async Task KilledHostAfterProposal_ResumesVerificationWithoutRepeatingExecutor()
    {
        using var scope = new GraphCrashScope();
        var graphId = await scope.StartAndKillAsync(CrashPoint.AfterGraphProposal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var store = await IronPaw.Testing.ProcessExitRecovery.OpenWorkflowAsync(CrashFixture.DatabasePath(scope.Root), timeout.Token);
        var recovered = (await store.GetTaskGraphAsync(graphId, timeout.Token))!;
        var proposed = Assert.ContainsSingle(recovered.Tasks);
        Assert.AreEqual(TaskGraphTaskStatus.AwaitingAcceptance, proposed.Status);
        Assert.IsNull(proposed.LeaseToken);
        Assert.IsNotNull(proposed.AttemptId);
        Assert.AreEqual(WorkflowTaskStatus.Succeeded, proposed.Result!.Status);
        Assert.IsNull(proposed.Acceptance);
        Assert.Contains(entry => entry.EventType == "task_result", await store.ReadTaskGraphJournalAsync(graphId, timeout.Token));
        var executor = new NeverRepeatExecutor();
        var verifier = new RecoveryVerifier(scope.Root);

        var resumed = await new TaskGraphScheduler(store, executor, verifier)
            .ResumeAsync(graphId, TaskGraphCrashFixture.Caller, timeout.Token);

        AssertAccepted(resumed, proposed.AttemptId!);
        Assert.AreEqual(JsonSerializer.Serialize(proposed.Result), JsonSerializer.Serialize(Assert.ContainsSingle(resumed.Tasks).Result));
        Assert.AreEqual(0, executor.CallCount);
        Assert.AreEqual(1, verifier.CallCount);
        Assert.ContainsSingle(await File.ReadAllLinesAsync(CrashFixture.EffectsPath(scope.Root), timeout.Token));
    }

    [TestMethod]
    public async Task KilledHostAfterAcceptance_ReusesEstablishedSuccessWithoutExecutorOrVerifier()
    {
        using var scope = new GraphCrashScope();
        var graphId = await scope.StartAndKillAsync(CrashPoint.AfterGraphAcceptance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var store = await IronPaw.Testing.ProcessExitRecovery.OpenWorkflowAsync(CrashFixture.DatabasePath(scope.Root), timeout.Token);
        var recovered = (await store.GetTaskGraphAsync(graphId, timeout.Token))!;
        var accepted = Assert.ContainsSingle(recovered.Tasks);
        AssertAccepted(recovered, accepted.AttemptId!);
        var journalBefore = await store.ReadTaskGraphJournalAsync(graphId, timeout.Token);
        Assert.Contains(entry => entry.EventType == "task_acceptance", journalBefore);
        var executor = new NeverRepeatExecutor();
        var verifier = new RecoveryVerifier(scope.Root);

        var resumed = await new TaskGraphScheduler(store, executor, verifier)
            .ResumeAsync(graphId, TaskGraphCrashFixture.Caller, timeout.Token);

        AssertAccepted(resumed, accepted.AttemptId!);
        Assert.AreEqual(recovered.Revision, resumed.Revision);
        Assert.AreEqual(accepted.Acceptance, Assert.ContainsSingle(resumed.Tasks).Acceptance);
        Assert.AreSequenceEqual(journalBefore.ToArray(), (await store.ReadTaskGraphJournalAsync(graphId, timeout.Token)).ToArray());
        Assert.AreEqual(0, executor.CallCount);
        Assert.AreEqual(0, verifier.CallCount);
        Assert.ContainsSingle(await File.ReadAllLinesAsync(CrashFixture.EffectsPath(scope.Root), timeout.Token));
    }

    private static void AssertAccepted(TaskGraphCheckpoint graph, string attemptId)
    {
        var state = Assert.ContainsSingle(graph.Tasks);
        Assert.AreEqual(TaskGraphTaskStatus.Succeeded, state.Status);
        Assert.AreEqual(attemptId, state.AttemptId);
        Assert.IsNull(state.LeaseToken);
        Assert.AreEqual(WorkflowTaskStatus.Succeeded, state.Result!.Status);
        var receipt = Assert.IsExactInstanceOfType<TaskAcceptanceReceipt>(state.Acceptance);
        Assert.AreEqual(TaskAcceptanceOutcome.Accepted, receipt.Outcome);
        Assert.AreEqual(graph.DefinitionFingerprint, receipt.GraphFingerprint);
        Assert.AreEqual(TaskGraphCrashFixture.TaskId, receipt.TaskId);
        Assert.AreEqual(attemptId, receipt.AttemptId);
        Assert.AreEqual(state.Result.Revision, receipt.Revision);
        Assert.IsFalse(string.IsNullOrWhiteSpace(receipt.ResultDigest));
    }

    private sealed class NeverRepeatExecutor : IWorkflowTaskExecutor
    {
        public int CallCount { get; private set; }
        public Task<WorkflowTaskResult> ExecuteAsync(TaskGraphClaim claim, CancellationToken ct)
        {
            CallCount++;
            throw new InvalidOperationException("The effect must never be automatically repeated after the killed host.");
        }
    }

    private sealed class RecoveryVerifier(string root) : ITaskAcceptanceVerifier
    {
        public int CallCount { get; private set; }
        public Task<TaskAcceptanceDecision> VerifyAsync(TaskGraphClaim claim, WorkflowTaskResult result, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(TaskGraphCrashFixture.InspectEffect(root, claim, result));
        }
    }

    private sealed class GraphCrashScope : IDisposable
    {
        private static readonly string TestBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IronPawGraphCrashTests"));
        public string Root { get; } = Path.Combine(TestBase, Guid.NewGuid().ToString("n"));
        public GraphCrashScope() => Directory.CreateDirectory(Root);

        public async Task<string> StartAndKillAsync(CrashPoint point)
        {
            var testAssembly = typeof(TaskGraphCrashRecoveryTests).Assembly.Location;
            var start = new ProcessStartInfo("dotnet")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"), typeof(CrashFixture).Assembly.Location, Root, point.ToString() })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start graph crash harness");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try
            {
                string? graphId = null;
                while (graphId is null)
                {
                    if (process.HasExited)
                        throw new InvalidOperationException($"Graph crash harness exited early ({process.ExitCode}): {await stderr}\n{await stdout}");
                    if (File.Exists(CrashFixture.ReadyPath(Root)))
                    {
                        try { graphId = await File.ReadAllTextAsync(CrashFixture.ReadyPath(Root), timeout.Token); }
                        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
                        { /* An atomic ready-file publication can retain a brief Windows sharing lock. */ }
                    }
                    if (graphId is not null) break;
                    await Task.Delay(25, timeout.Token);
                }
                Assert.IsFalse(string.IsNullOrWhiteSpace(graphId));
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(timeout.Token);
                Assert.IsTrue(process.HasExited);
                return graphId;
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

        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(TestBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Graph crash test cleanup escaped its isolated test directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
