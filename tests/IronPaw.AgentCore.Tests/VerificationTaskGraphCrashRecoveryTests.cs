using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;
using IronPaw.Workflow.CrashHarness;

namespace IronPaw.AgentCore.Tests;

public sealed class VerificationTaskGraphCrashRecoveryTests
{
    [Fact]
    public async Task KilledAfterCommandStarted_ReopensUnknownAndRequiresReconciliationWithoutReplay()
    {
        using var scope = new CrashScope();
        var graphId = await scope.StartAndKillAsync(CrashPoint.AfterVerificationStarted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var store = await CSharpDbWorkflowStore.OpenAsync(CrashFixture.DatabasePath(scope.Root), ct);
        var recovered = (await store.GetTaskGraphAsync(graphId, ct))!;
        var candidate = Assert.Single(recovered.Tasks);
        var invocation = Assert.Single(candidate.Verifications);
        Assert.Equal(TaskGraphTaskStatus.AwaitingAcceptance, candidate.Status);
        Assert.Equal(TaskVerificationInvocationState.NeedsReconciliation, invocation.State);
        Assert.Null(invocation.Completion);
        Assert.Null(candidate.Acceptance);
        Assert.Null(candidate.LeaseToken);
        Assert.Equal(TaskGraphRunStatus.NeedsReconciliation, TaskGraphProgress.Inspect(recovered).Status);
        var providerBefore = await File.ReadAllLinesAsync(VerificationTaskGraphCrashFixture.ProviderCallsPath(scope.Root), ct);
        Assert.Equal(2, providerBefore.Length);
        Assert.Equal([invocation.InvocationId], await File.ReadAllLinesAsync(VerificationTaskGraphCrashFixture.WorkerEffectsPath(scope.Root), ct));
        var child = Assert.Single(await store.ListAsync(ct), run => run.ParentRunId is not null);
        var childBefore = JsonSerializer.Serialize(child);
        var model = new MustNotCallProvider();
        var worker = new MustNotRunWorker();
        var runner = new AgentTaskGraphRunner(store, store, new GitTaskWorkspaceManager(), model, worker);

        var stillUnknown = await runner.ResumeAsync(graphId, VerificationTaskGraphCrashFixture.Caller, ct);

        Assert.Equal(TaskGraphRunStatus.NeedsReconciliation, TaskGraphProgress.Inspect(stillUnknown).Status);
        Assert.Equal(0, worker.Executions);
        Assert.Equal(0, model.Calls);
        await runner.ReconcileVerificationAsync(graphId, VerificationTaskGraphCrashFixture.TaskId, invocation.InvocationId,
            VerificationTaskGraphCrashFixture.EstablishedCompletion(), "Inspected the matching durable worker effect and established its successful outcome.",
            VerificationTaskGraphCrashFixture.Caller, ct);
        var accepted = await runner.ResumeAsync(graphId, VerificationTaskGraphCrashFixture.Caller, ct);

        Assert.Equal(TaskGraphRunStatus.Succeeded, TaskGraphProgress.Inspect(accepted).Status);
        Assert.Equal(candidate.AttemptId, accepted.Tasks[0].AttemptId);
        Assert.Equal(invocation.InvocationId, Assert.Single(accepted.Tasks[0].Verifications).InvocationId);
        Assert.Equal(0, worker.Executions);
        Assert.Equal(0, model.Calls);
        Assert.Equal(childBefore, JsonSerializer.Serialize(await store.GetAsync(new RunId(child.RunId), ct)));
        Assert.Equal(providerBefore, await File.ReadAllLinesAsync(VerificationTaskGraphCrashFixture.ProviderCallsPath(scope.Root), ct));
        Assert.Equal([invocation.InvocationId], await File.ReadAllLinesAsync(VerificationTaskGraphCrashFixture.WorkerEffectsPath(scope.Root), ct));
    }

    [Fact]
    public async Task KilledAfterCompletedCommandBeforeAcceptance_ReusesExactReceiptWithNoWorkerOrProviderCalls()
    {
        using var scope = new CrashScope();
        var graphId = await scope.StartAndKillAsync(CrashPoint.AfterVerificationCompleted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var store = await CSharpDbWorkflowStore.OpenAsync(CrashFixture.DatabasePath(scope.Root), ct);
        var recovered = (await store.GetTaskGraphAsync(graphId, ct))!;
        var candidate = Assert.Single(recovered.Tasks);
        var invocation = Assert.Single(candidate.Verifications);
        Assert.Equal(TaskGraphTaskStatus.AwaitingAcceptance, candidate.Status);
        Assert.Null(candidate.Acceptance);
        Assert.Null(candidate.LeaseToken);
        Assert.Equal(TaskVerificationInvocationState.Completed, invocation.State);
        Assert.Equal(TaskCommandOutcome.Passed, invocation.Completion!.Outcome);
        Assert.Equal(candidate.Result!.Revision, invocation.Revision);
        Assert.Equal(TaskInputIdentity.ResultDigest(candidate.Result), invocation.ResultDigest);
        var callsBefore = await File.ReadAllLinesAsync(VerificationTaskGraphCrashFixture.ProviderCallsPath(scope.Root), ct);
        var budgetBefore = await store.GetBudgetAsync(new RunId(recovered.AgentBinding!.RootRunId), ct);
        var model = new MustNotCallProvider();
        var worker = new MustNotRunWorker();

        var accepted = await new AgentTaskGraphRunner(store, store, new GitTaskWorkspaceManager(), model, worker)
            .ResumeAsync(graphId, VerificationTaskGraphCrashFixture.Caller, ct);

        Assert.Equal(TaskGraphRunStatus.Succeeded, TaskGraphProgress.Inspect(accepted).Status);
        Assert.Equal(TaskAcceptanceOutcome.Accepted, accepted.Tasks[0].Acceptance!.Outcome);
        Assert.Equal(invocation, Assert.Single(accepted.Tasks[0].Verifications));
        Assert.Equal(candidate.AttemptId, accepted.Tasks[0].AttemptId);
        Assert.Equal(JsonSerializer.Serialize(candidate.Result), JsonSerializer.Serialize(accepted.Tasks[0].Result));
        Assert.Equal(0, worker.Executions);
        Assert.Equal(0, model.Calls);
        Assert.Equal([invocation.InvocationId], await File.ReadAllLinesAsync(VerificationTaskGraphCrashFixture.WorkerEffectsPath(scope.Root), ct));
        Assert.Equal(callsBefore, await File.ReadAllLinesAsync(VerificationTaskGraphCrashFixture.ProviderCallsPath(scope.Root), ct));
        Assert.Equal(budgetBefore.ChargedTokens, (await store.GetBudgetAsync(new RunId(recovered.AgentBinding.RootRunId), ct)).ChargedTokens);
        var journal = await store.ReadTaskGraphJournalAsync(graphId, ct);
        Assert.Single(journal, entry => entry.EventType == "verification_started");
        Assert.Single(journal, entry => entry.EventType == "verification_receipt");
        Assert.Single(journal, entry => entry.EventType == "task_acceptance");
    }

    private sealed class MustNotRunWorker : ITaskVerificationWorker
    {
        public int Executions;
        public void ValidateProfile(TaskVerificationProfile profile, CallerIdentity caller) =>
            throw new InvalidOperationException("A command with a saved or uncertain invocation must not be admitted again.");
        public Task<TaskVerificationCompletion> ExecuteAsync(TaskVerificationJob job, CancellationToken ct)
        {
            Interlocked.Increment(ref Executions);
            throw new InvalidOperationException("A durably completed or uncertain command must not execute again.");
        }
    }

    private sealed class MustNotCallProvider : ILlmClient
    {
        public int Calls;
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Interlocked.Increment(ref Calls);
            await Task.FromException(new InvalidOperationException("A durable candidate must not call the provider again."));
            yield break;
        }
    }

    private sealed class CrashScope : IDisposable
    {
        private static readonly string TestBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IronPawVerificationCrashTests"));
        public string Root { get; } = Path.Combine(TestBase, Guid.NewGuid().ToString("n"));
        public CrashScope() => Directory.CreateDirectory(Root);
        public async Task<string> StartAndKillAsync(CrashPoint point)
        {
            var assembly = typeof(VerificationTaskGraphCrashRecoveryTests).Assembly.Location;
            var start = new ProcessStartInfo("dotnet") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(assembly, ".deps.json"), typeof(CrashFixture).Assembly.Location, Root, point.ToString() })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the verification crash harness.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try
            {
                string? graphId = null;
                while (graphId is null)
                {
                    if (process.HasExited)
                        throw new InvalidOperationException($"Verification crash harness exited before its durable boundary ({process.ExitCode}): {await stderr}\n{await stdout}");
                    if (File.Exists(CrashFixture.ReadyPath(Root)))
                    {
                        try { graphId = await File.ReadAllTextAsync(CrashFixture.ReadyPath(Root), timeout.Token); }
                        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33) { }
                    }
                    if (graphId is null) await Task.Delay(25, timeout.Token);
                }
                Assert.False(string.IsNullOrWhiteSpace(graphId));
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.HasExited);
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
                throw new InvalidOperationException("Verification crash cleanup escaped its isolated test directory.");
            foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(resolved, recursive: true);
        }
    }
}
