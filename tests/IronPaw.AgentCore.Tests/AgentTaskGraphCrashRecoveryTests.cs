using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;
using IronPaw.Workflow.CrashHarness;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class AgentTaskGraphCrashRecoveryTests
{
    [TestMethod]
    public async Task KilledAfterDurableChildSuccess_ExplicitRecoveryCapturesWithoutProviderOrToolReplay()
    {
        using var scope = new CrashScope();
        var graphId = await scope.StartAndKillAsync(CrashPoint.AfterAgentChildSuccess);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var store = await IronPaw.Testing.ProcessExitRecovery.OpenWorkflowAsync(CrashFixture.DatabasePath(scope.Root), ct);
        var recovered = (await store.GetTaskGraphAsync(graphId, ct))!;
        var unknown = Assert.ContainsSingle(recovered.Tasks);
        Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, unknown.Status);
        Assert.IsNull(unknown.LeaseToken);
        Assert.IsNull(unknown.Result);
        var child = Assert.ContainsSingle(run => run.ParentRunId is not null, await store.ListAsync(ct));
        Assert.AreEqual(AgentRunStatus.Succeeded, child.Result!.Status);
        Assert.IsNull(child.LeaseOwner);
        Assert.IsFalse(WorkflowIdentity.HasUnknownEffects(child));
        var write = Assert.ContainsSingle(child.ToolExecutions!);
        Assert.AreEqual(WorkflowToolState.Completed, write.State);
        Assert.IsFalse(write.Result!.IsError);
        var workspace = await scope.WorkspaceAsync(ct);
        var workingOutput = Path.Combine(workspace.WorkspacePath, AgentTaskGraphCrashFixture.OutputName);
        var writeTime = File.GetLastWriteTimeUtc(workingOutput);
        var model = new MustNotCallProvider();
        var runner = new AgentTaskGraphRunner(store, store, new GitTaskWorkspaceManager(), model);
        var callsBefore = await File.ReadAllLinesAsync(AgentTaskGraphCrashFixture.ProviderCallsPath(scope.Root), ct);
        Assert.AreEqual(2, callsBefore.Length);

        var blocked = await runner.ResumeAsync(graphId, AgentTaskGraphCrashFixture.Caller, ct);
        Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, Assert.ContainsSingle(blocked.Tasks).Status);
        var completed = await runner.RecoverTaskAsync(graphId, AgentTaskGraphCrashFixture.TaskId, AgentTaskGraphCrashFixture.Caller, ct);

        AssertAccepted(completed, unknown.AttemptId!);
        Assert.AreEqual(0, model.Calls);
        Assert.AreEqual(AgentTaskGraphCrashFixture.OutputContent, await File.ReadAllTextAsync(workingOutput, ct));
        Assert.AreEqual(writeTime, File.GetLastWriteTimeUtc(workingOutput));
        Assert.AreEqual(AgentTaskGraphCrashFixture.OutputContent, await File.ReadAllTextAsync(Assert.ContainsSingle(completed.Tasks[0].Result!.Artifacts).Location, ct));
        var reused = (await store.GetAsync(new RunId(child.RunId), ct))!;
        Assert.AreEqual(child.Request.SessionKey, reused.Request.SessionKey);
        Assert.AreEqual(write.ExecutionId, Assert.ContainsSingle(reused.ToolExecutions!).ExecutionId);
        Assert.AreEqual(1, reused.Usage!.ToolCalls);
        Assert.AreEqual(2, reused.Usage.ModelCalls);
        Assert.AreSequenceEqual(callsBefore, await File.ReadAllLinesAsync(AgentTaskGraphCrashFixture.ProviderCallsPath(scope.Root), ct));
        Assert.AreEqual(workspace, await scope.WorkspaceAsync(ct));
    }

    [TestMethod]
    public async Task KilledAfterSavedCandidate_ResumesReadOnlyVerificationWithoutReplayingAgent()
    {
        using var scope = new CrashScope();
        var graphId = await scope.StartAndKillAsync(CrashPoint.AfterAgentCandidate);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var store = await IronPaw.Testing.ProcessExitRecovery.OpenWorkflowAsync(CrashFixture.DatabasePath(scope.Root), ct);
        var recovered = (await store.GetTaskGraphAsync(graphId, ct))!;
        var candidate = Assert.ContainsSingle(recovered.Tasks);
        Assert.AreEqual(TaskGraphTaskStatus.AwaitingAcceptance, candidate.Status);
        Assert.IsNull(candidate.LeaseToken);
        Assert.IsNull(candidate.Acceptance);
        Assert.AreEqual(WorkflowTaskStatus.AwaitingVerification, candidate.Result!.Status);
        var child = Assert.ContainsSingle(run => run.ParentRunId is not null, await store.ListAsync(ct));
        var childBefore = JsonSerializer.Serialize(child);
        var budgetBefore = await store.GetBudgetAsync(new RunId(recovered.AgentBinding!.RootRunId), ct);
        var callsBefore = await File.ReadAllLinesAsync(AgentTaskGraphCrashFixture.ProviderCallsPath(scope.Root), ct);
        var model = new MustNotCallProvider();
        var verifier = new CountingVerificationWorkspaces();

        var completed = await new AgentTaskGraphRunner(store, store, verifier, model)
            .ResumeAsync(graphId, AgentTaskGraphCrashFixture.Caller, ct);

        AssertAccepted(completed, candidate.AttemptId!);
        Assert.AreEqual(0, model.Calls);
        Assert.AreEqual(0, verifier.CaptureCalls);
        Assert.AreEqual(1, verifier.VerificationCalls);
        Assert.AreEqual(JsonSerializer.Serialize(candidate.Result), JsonSerializer.Serialize(completed.Tasks[0].Result));
        Assert.AreEqual(childBefore, JsonSerializer.Serialize(await store.GetAsync(new RunId(child.RunId), ct)));
        Assert.AreSequenceEqual(callsBefore, await File.ReadAllLinesAsync(AgentTaskGraphCrashFixture.ProviderCallsPath(scope.Root), ct));
        Assert.AreEqual(budgetBefore.ChargedTokens, (await store.GetBudgetAsync(new RunId(recovered.AgentBinding.RootRunId), ct)).ChargedTokens);
        Assert.AreEqual(AgentTaskGraphCrashFixture.OutputContent, await File.ReadAllTextAsync(Assert.ContainsSingle(completed.Tasks[0].Result!.Artifacts).Location, ct));
    }

    private static void AssertAccepted(TaskGraphCheckpoint graph, string attemptId)
    {
        Assert.AreEqual(TaskGraphRunStatus.Succeeded, TaskGraphProgress.Inspect(graph).Status);
        var task = Assert.ContainsSingle(graph.Tasks);
        Assert.AreEqual(attemptId, task.AttemptId);
        Assert.AreEqual(TaskGraphTaskStatus.Succeeded, task.Status);
        Assert.AreEqual(TaskAcceptanceOutcome.Accepted, task.Acceptance!.Outcome);
        Assert.AreEqual(graph.DefinitionFingerprint, task.Acceptance.GraphFingerprint);
        Assert.AreEqual(task.Result!.Revision, task.Acceptance.Revision);
    }

    private sealed class MustNotCallProvider : ILlmClient
    {
        public int Calls;
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Interlocked.Increment(ref Calls);
            await Task.FromException(new InvalidOperationException("A durably completed child must not call the provider again."));
            yield break;
        }
    }

    private sealed class CountingVerificationWorkspaces : ITaskWorkspaceManager
    {
        private readonly GitTaskWorkspaceManager inner = new();
        public int CaptureCalls;
        public int VerificationCalls;
        public Task<string> ResolveCommitAsync(string sourceWorkspaceRoot, string revision, CancellationToken ct) =>
            throw new InvalidOperationException("Verification must retain the accepted base commit without resolving a new revision.");
        public Task<TaskWorkspace> OpenOrCreateAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct) =>
            throw new InvalidOperationException("Verification must only open an existing workspace without creating it.");
        public Task<TaskWorkspace> OpenExistingAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct) => inner.OpenExistingAsync(binding, claim, ct);
        public Task<TaskWorkspace> PrepareAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim,
            System.Collections.Immutable.ImmutableArray<TaskDependencyInput> dependencies, CancellationToken ct) =>
            throw new InvalidOperationException("Verification must never prepare or reapply accepted dependency changes.");
        public Task<TaskWorkspaceCapture> CaptureAsync(TaskWorkspace workspace, System.Collections.Immutable.ImmutableArray<TaskArtifactRequirement> requirements, CancellationToken ct)
        {
            Interlocked.Increment(ref CaptureCalls);
            throw new InvalidOperationException("A saved candidate must not be captured again.");
        }
        public async Task<bool> VerifyAsync(TaskWorkspace workspace, WorkflowTaskResult result, CancellationToken ct)
        {
            Interlocked.Increment(ref VerificationCalls);
            return await inner.VerifyAsync(workspace, result, ct);
        }
    }

    private sealed class CrashScope : IDisposable
    {
        private static readonly string TestBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IronPawAgentGraphCrashTests"));
        public string Root { get; } = Path.Combine(TestBase, Guid.NewGuid().ToString("n"));
        public CrashScope() => Directory.CreateDirectory(Root);

        public async Task<TaskWorkspace> WorkspaceAsync(CancellationToken ct) => JsonSerializer.Deserialize<TaskWorkspace>(
            await File.ReadAllTextAsync(Assert.ContainsSingle(Directory.EnumerateFiles(AgentTaskGraphCrashFixture.WorkspacesPath(Root), "workspace.json", SearchOption.AllDirectories)), ct))!;

        public async Task<string> StartAndKillAsync(CrashPoint point)
        {
            var assembly = typeof(AgentTaskGraphCrashRecoveryTests).Assembly.Location;
            var start = new ProcessStartInfo("dotnet")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(assembly, ".deps.json"), typeof(CrashFixture).Assembly.Location, Root, point.ToString() })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start agent graph crash harness.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try
            {
                string? graphId = null;
                while (graphId is null)
                {
                    if (process.HasExited)
                        throw new InvalidOperationException($"Agent graph crash harness exited before its durable boundary ({process.ExitCode}): {await stderr}\n{await stdout}");
                    if (File.Exists(CrashFixture.ReadyPath(Root)))
                    {
                        try { graphId = await File.ReadAllTextAsync(CrashFixture.ReadyPath(Root), timeout.Token); }
                        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33) { }
                    }
                    if (graphId is null) await Task.Delay(25, timeout.Token);
                }
                Assert.IsFalse(string.IsNullOrWhiteSpace(graphId));
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(timeout.Token);
                Assert.IsTrue(process.HasExited);
                await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token);
                // Process exit and exclusive file availability are separate observations on
                // Windows. Wait only for a sharing violation, without opening the database
                // engine or performing recovery, before testing the persisted crash state.
                var releaseWait = Stopwatch.StartNew();
                while (true)
                {
                    try
                    {
                        using var access = File.OpenHandle(CrashFixture.DatabasePath(Root), FileMode.Open,
                            FileAccess.ReadWrite, FileShare.None);
                        break;
                    }
                    catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33 &&
                        releaseWait.Elapsed < TimeSpan.FromSeconds(5))
                    {
                        await Task.Delay(25, timeout.Token);
                    }
                }
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
                throw new InvalidOperationException("Agent graph crash cleanup escaped its isolated test directory.");
            foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(resolved, recursive: true);
        }
    }
}
