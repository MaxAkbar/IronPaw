using System.Collections.Concurrent;
using System.Collections.Immutable;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class TaskGraphSchedulerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly CallerIdentity Caller = CallerIdentity.Authenticated("graph-owner", "Graph owner");

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChainAndDiamond_ExecuteInStableDependencyOrder(bool diamond)
    {
        await using var scope = await Scope.OpenAsync();
        using var timeout = new CancellationTokenSource(Deadline);
        var graph = diamond
            ? Graph(Task("deliver", "review", "test"), Task("review", "inspect"), Task("test", "inspect"), Task("inspect"))
            : Graph(Task("deliver", "implement"), Task("implement", "inspect"), Task("inspect"));
        var executor = new RecordingExecutor();
        var verifier = new RecordingVerifier();

        var result = await scope.Runtime(executor, verifier).StartAsync(graph, Caller, 1, timeout.Token);

        Assert.AreSequenceEqual(diamond ? ["inspect", "review", "test", "deliver"] : ["inspect", "implement", "deliver"], executor.Calls.Select(claim => claim.Task.TaskId).ToArray());
        foreach (var state in result.Tasks)
        {
            Assert.AreEqual(TaskGraphTaskStatus.Succeeded, state.Status);
        }
        foreach (var state in result.Tasks)
        {
            Assert.AreEqual(TaskAcceptanceOutcome.Accepted, state.Acceptance!.Outcome);
            Assert.AreEqual(state.AttemptId, state.Acceptance.AttemptId);
            Assert.AreEqual(result.DefinitionFingerprint, state.Acceptance.GraphFingerprint);
            Assert.AreEqual(state.Result!.Revision, state.Acceptance.Revision);
            Assert.IsFalse(string.IsNullOrWhiteSpace(state.Acceptance.ResultDigest));
        }
        Assert.AreEqual(result.Tasks.Length, verifier.Calls.Count);
    }

    [TestMethod]
    public async Task IndependentBranches_RespectThePersistedParallelLimit()
    {
        await using var scope = await Scope.OpenAsync();
        using var timeout = new CancellationTokenSource(Deadline);
        var release = Signal();
        var twoStarted = Signal();
        var starts = 0;
        var executor = new RecordingExecutor(async (claim, ct) =>
        {
            if (Interlocked.Increment(ref starts) == 2) twoStarted.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Success(claim);
        });
        var graph = Graph(Enumerable.Range(0, 6).Select(index => Task($"task-{index}")).ToArray());
        var run = scope.Runtime(executor, new RecordingVerifier()).StartAsync(graph, Caller, 2, timeout.Token);
        try
        {
            await twoStarted.Task.WaitAsync(timeout.Token);
            Assert.AreEqual(2, executor.Calls.Count);
            var active = (await scope.Store.GetTaskGraphAsync(graph.GraphId, timeout.Token))!;
            Assert.AreEqual(2, active.MaxConcurrency);
            Assert.AreEqual(2, active.Tasks.Count(task => task.Status == TaskGraphTaskStatus.Running));
            release.TrySetResult();
            var result = await run.WaitAsync(timeout.Token);
            Assert.AreEqual(6, executor.Calls.Count);
            Assert.AreEqual(2, executor.MaximumActive);
            foreach (var task in result.Tasks)
            {
                Assert.AreEqual(TaskGraphTaskStatus.Succeeded, task.Status);
            }
        }
        finally
        {
            release.TrySetResult();
            timeout.Cancel();
            await DrainAsync(run);
        }
    }

    [TestMethod]
    public async Task FailedDependency_BlocksDescendantsWhileIndependentWorkFinishes()
    {
        await using var scope = await Scope.OpenAsync();
        using var timeout = new CancellationTokenSource(Deadline);
        var graph = Graph(Task("failed"), Task("blocked", "failed"), Task("independent"));
        var executor = new RecordingExecutor((claim, _) => System.Threading.Tasks.Task.FromResult(
            claim.Task.TaskId == "failed" ? Outcome(claim, WorkflowTaskStatus.Failed) : Success(claim)));

        var result = await scope.Runtime(executor, new RecordingVerifier()).StartAsync(graph, Caller, 2, timeout.Token);

        Assert.AreEqual(TaskGraphTaskStatus.Failed, State(result, "failed").Status);
        Assert.AreEqual(TaskGraphTaskStatus.Succeeded, State(result, "independent").Status);
        Assert.IsNull(State(result, "blocked").AttemptId);
        Assert.DoesNotContain(claim => claim.Task.TaskId == "blocked", executor.Calls);
    }

    [TestMethod]
    public async Task ProposedSuccess_DoesNotUnblockDependentBeforeTrustedAcceptance()
    {
        await using var scope = await Scope.OpenAsync();
        using var timeout = new CancellationTokenSource(Deadline);
        var verifying = Signal();
        var release = Signal();
        var graph = Graph(Task("parent"), Task("dependent", "parent"));
        var executor = new RecordingExecutor();
        var verifier = new RecordingVerifier(async (claim, _, ct) =>
        {
            if (claim.Task.TaskId == "parent")
            {
                verifying.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return Accepted();
        });
        var run = scope.Runtime(executor, verifier).StartAsync(graph, Caller, 2, timeout.Token);
        try
        {
            await verifying.Task.WaitAsync(timeout.Token);
            var pending = (await scope.Store.GetTaskGraphAsync(graph.GraphId, timeout.Token))!;
            Assert.AreEqual(TaskGraphTaskStatus.AwaitingAcceptance, State(pending, "parent").Status);
            Assert.IsNull(State(pending, "parent").Acceptance);
            Assert.IsNull(State(pending, "dependent").AttemptId);
            Assert.AreEqual("parent", Assert.ContainsSingle(executor.Calls).Task.TaskId);
            release.TrySetResult();
            var result = await run.WaitAsync(timeout.Token);
            foreach (var task in result.Tasks)
            {
                Assert.AreEqual(TaskGraphTaskStatus.Succeeded, task.Status);
            }
        }
        finally
        {
            release.TrySetResult();
            timeout.Cancel();
            await DrainAsync(run);
        }
    }

    [TestMethod]
    [DataRow(TaskAcceptanceOutcome.Rejected)]
    [DataRow(TaskAcceptanceOutcome.Inconclusive)]
    public async Task RejectedOrInconclusiveVerification_CannotUnblockDependent(TaskAcceptanceOutcome decision)
    {
        await using var scope = await Scope.OpenAsync();
        using var timeout = new CancellationTokenSource(Deadline);
        var graph = Graph(Task("parent"), Task("blocked", "parent"), Task("independent"));
        var executor = new RecordingExecutor();
        var verifier = new RecordingVerifier((claim, _, _) => System.Threading.Tasks.Task.FromResult(
            claim.Task.TaskId == "parent" ? new(decision, "trusted-test-verifier", "Acceptance was not established.") : Accepted()));

        var result = await scope.Runtime(executor, verifier).StartAsync(graph, Caller, 2, timeout.Token);

        Assert.AreNotEqual(TaskGraphTaskStatus.Succeeded, State(result, "parent").Status);
        Assert.AreEqual(decision, State(result, "parent").Acceptance!.Outcome);
        Assert.IsNull(State(result, "blocked").AttemptId);
        Assert.AreEqual(TaskGraphTaskStatus.Succeeded, State(result, "independent").Status);
        Assert.AreEqual(1, verifier.Calls.Count(claim => claim.Task.TaskId == "parent"));
        Assert.DoesNotContain(claim => claim.Task.TaskId == "blocked", executor.Calls);
    }

    [TestMethod]
    public async Task InconclusiveVerification_ExplicitResumeRetriesVerifierWithoutRepeatingExecutor()
    {
        await using var scope = await Scope.OpenAsync();
        using var timeout = new CancellationTokenSource(Deadline);
        var graph = Graph(Task("parent"), Task("dependent", "parent"));
        var executor = new RecordingExecutor();
        var verifier = new RecordingVerifier((_, _, _) => System.Threading.Tasks.Task.FromResult(
            new TaskAcceptanceDecision(TaskAcceptanceOutcome.Inconclusive, "trusted-test-verifier", "Need more observations.")));
        var first = await scope.Runtime(executor, verifier).StartAsync(graph, Caller, 1, timeout.Token);
        Assert.AreEqual(TaskGraphTaskStatus.AwaitingAcceptance, State(first, "parent").Status);
        var initialAttempt = State(first, "parent").AttemptId;
        await scope.ReopenAsync();
        var laterExecutor = new RecordingExecutor();
        var laterVerifier = new RecordingVerifier();

        var resumed = await scope.Runtime(laterExecutor, laterVerifier).ResumeAsync(graph.GraphId, Caller, timeout.Token);

        Assert.AreEqual(initialAttempt, State(resumed, "parent").AttemptId);
        Assert.AreEqual("parent", Assert.ContainsSingle(executor.Calls).Task.TaskId);
        Assert.AreEqual("dependent", Assert.ContainsSingle(laterExecutor.Calls).Task.TaskId);
        Assert.AreSequenceEqual(["parent", "dependent"], laterVerifier.Calls.Select(claim => claim.Task.TaskId).ToArray());
        foreach (var task in resumed.Tasks)
        {
            Assert.AreEqual(TaskGraphTaskStatus.Succeeded, task.Status);
        }
    }

    [TestMethod]
    public async Task CompletedGraph_ReplayAndReopenDoNotExecuteOrVerifyAgain()
    {
        await using var scope = await Scope.OpenAsync();
        using var timeout = new CancellationTokenSource(Deadline);
        var graph = Graph(Task("parent"), Task("dependent", "parent"));
        var firstExecutor = new RecordingExecutor();
        var first = await scope.Runtime(firstExecutor, new RecordingVerifier()).StartAsync(graph, Caller, 2, timeout.Token);
        var unusedExecutor = new RecordingExecutor((_, _) => throw new InvalidOperationException("Completed task was repeated."));
        var unusedVerifier = new RecordingVerifier((_, _, _) => throw new InvalidOperationException("Accepted result was reverified."));
        var replay = await scope.Runtime(unusedExecutor, unusedVerifier).ResumeAsync(graph.GraphId, Caller, timeout.Token);
        await scope.ReopenAsync();

        var reopened = await scope.Runtime(unusedExecutor, unusedVerifier).ResumeAsync(graph.GraphId, Caller, timeout.Token);

        Assert.IsEmpty(unusedExecutor.Calls);
        Assert.IsEmpty(unusedVerifier.Calls);
        Assert.AreEqual(first.DefinitionFingerprint, replay.DefinitionFingerprint);
        Assert.AreSequenceEqual(first.Tasks.Select(task => task.AttemptId), reopened.Tasks.Select(task => task.AttemptId));
        foreach (var task in reopened.Tasks)
        {
            Assert.AreEqual(TaskGraphTaskStatus.Succeeded, task.Status);
        }
    }

    [TestMethod]
    [DataRow("resume", false)]
    [DataRow("resume", true)]
    [DataRow("pause", false)]
    [DataRow("pause", true)]
    [DataRow("cancel", false)]
    [DataRow("cancel", true)]
    [DataRow("reconcile", false)]
    [DataRow("reconcile", true)]
    public async Task RecoveryCommands_RequireOriginalCallerAndOwnershipLevel(string operation, bool changedOwnership)
    {
        await using var scope = await Scope.OpenAsync();
        using var timeout = new CancellationTokenSource(Deadline);
        var graph = Graph(Task("task"));
        await scope.Store.AcceptTaskGraphAsync(graph, Caller, 1, timeout.Token);
        var claim = (await scope.Store.ClaimTaskAsync(graph.GraphId, Caller, timeout.Token))!;
        var before = await scope.Store.AbandonTaskClaimAsync(claim, "Outcome unknown.", timeout.Token);
        var unauthorized = changedOwnership ? Caller with { IsOwner = true }
            : CallerIdentity.Authenticated("other-caller", "Other caller");
        var executor = new RecordingExecutor();
        var runtime = scope.Runtime(executor, new RecordingVerifier());

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () =>
        {
            switch (operation)
            {
                case "resume": await runtime.ResumeAsync(graph.GraphId, unauthorized, timeout.Token); break;
                case "pause": await runtime.PauseAsync(graph.GraphId, unauthorized, timeout.Token); break;
                case "cancel": await runtime.CancelAsync(graph.GraphId, unauthorized, timeout.Token); break;
                case "reconcile": await runtime.ReconcileAsync(graph.GraphId, "task", Success(claim), "Established outcome.", unauthorized, timeout.Token); break;
                default: throw new ArgumentException("Unknown command.", nameof(operation));
            }
        });

        var after = (await scope.Store.GetTaskGraphAsync(graph.GraphId, timeout.Token))!;
        Assert.AreEqual(before.Revision, after.Revision);
        Assert.AreEqual(before.Control, after.Control);
        Assert.IsEmpty(executor.Calls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InterruptedExecutorWithPossibleEffects_RemainsUnknownAndIsNeverRetried(bool permanentCancel)
    {
        await using var scope = await Scope.OpenAsync();
        using var timeout = new CancellationTokenSource(Deadline);
        using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var entered = Signal();
        var effects = 0;
        var executor = new RecordingExecutor(async (claim, ct) =>
        {
            Interlocked.Increment(ref effects);
            entered.TrySetResult();
            await System.Threading.Tasks.Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Success(claim);
        });
        var graph = Graph(Task("effect"), Task("dependent", "effect"));
        var runtime = scope.Runtime(executor, new RecordingVerifier());
        var run = runtime.StartAsync(graph, Caller, 1, callerCancellation.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            if (permanentCancel) await runtime.CancelAsync(graph.GraphId, Caller, timeout.Token);
            else callerCancellation.Cancel();
            try { await run.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested || permanentCancel) { }
            var interrupted = (await scope.Store.GetTaskGraphAsync(graph.GraphId, timeout.Token))!;
            Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, State(interrupted, "effect").Status);
            Assert.IsNull(State(interrupted, "effect").Result);
            Assert.IsNull(State(interrupted, "dependent").AttemptId);
            Assert.AreEqual(1, effects);
            var attempt = State(interrupted, "effect").AttemptId;
            await scope.ReopenAsync();
            var unused = new RecordingExecutor();
            try { await scope.Runtime(unused, new RecordingVerifier()).ResumeAsync(graph.GraphId, Caller, timeout.Token); }
            catch (WorkflowCancelledException) when (permanentCancel) { }
            Assert.IsEmpty(unused.Calls);
            Assert.AreEqual(attempt, State((await scope.Store.GetTaskGraphAsync(graph.GraphId, timeout.Token))!, "effect").AttemptId);
        }
        finally
        {
            callerCancellation.Cancel();
            await DrainAsync(run);
        }
    }

    [TestMethod]
    public async Task PauseDuringExecution_PreservesKnownOutcomeAndResumeClaimsOnlyRemainingWork()
    {
        await using var scope = await Scope.OpenAsync();
        using var timeout = new CancellationTokenSource(Deadline);
        var entered = Signal();
        var release = Signal();
        var executor = new RecordingExecutor(async (claim, _) =>
        {
            if (claim.Task.TaskId == "first")
            {
                entered.TrySetResult();
                await release.Task; // Host settles a known outcome even after its cancellation request.
            }
            return Success(claim);
        });
        var graph = Graph(Task("first"), Task("next", "first"));
        var runtime = scope.Runtime(executor, new RecordingVerifier());
        var run = runtime.StartAsync(graph, Caller, 1, timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            await runtime.PauseAsync(graph.GraphId, Caller, timeout.Token);
            release.TrySetResult();
            try { await run.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) { }
            var paused = (await scope.Store.GetTaskGraphAsync(graph.GraphId, timeout.Token))!;
            Assert.AreEqual(WorkflowControl.Pause, paused.Control);
            Assert.IsNotNull(State(paused, "first").Result);
            Assert.IsNull(State(paused, "next").AttemptId);
            Assert.AreEqual("first", Assert.ContainsSingle(executor.Calls).Task.TaskId);

            var resumed = await runtime.ResumeAsync(graph.GraphId, Caller, timeout.Token);

            Assert.AreEqual(WorkflowControl.None, resumed.Control);
            foreach (var task in resumed.Tasks)
            {
                Assert.AreEqual(TaskGraphTaskStatus.Succeeded, task.Status);
            }
            Assert.AreSequenceEqual(["first", "next"], executor.Calls.Select(claim => claim.Task.TaskId).ToArray());
        }
        finally
        {
            release.TrySetResult();
            timeout.Cancel();
            await DrainAsync(run);
        }
    }

    [TestMethod]
    public async Task ExclusiveStoreReopen_MarksClaimUnknownAndReconciliationVerifiesWithoutRepeatingEffect()
    {
        await using var scope = await Scope.OpenAsync();
        using var timeout = new CancellationTokenSource(Deadline);
        var graph = Graph(Task("effect"), Task("dependent", "effect"));
        await scope.Store.AcceptTaskGraphAsync(graph, Caller, 1, timeout.Token);
        var claim = (await scope.Store.ClaimTaskAsync(graph.GraphId, Caller, timeout.Token))!;
        await scope.ReopenAsync();
        var executor = new RecordingExecutor();
        var verifier = new RecordingVerifier();
        var runtime = scope.Runtime(executor, verifier);

        var unknown = await runtime.ResumeAsync(graph.GraphId, Caller, timeout.Token);
        Assert.AreEqual(TaskGraphTaskStatus.NeedsReconciliation, State(unknown, "effect").Status);
        Assert.AreEqual(claim.AttemptId, State(unknown, "effect").AttemptId);
        Assert.IsEmpty(executor.Calls);
        Assert.IsEmpty(verifier.Calls);

        var reconciled = await runtime.ReconcileAsync(graph.GraphId, "effect", Success(claim),
            "External inspection established the exact revision and artifacts.", Caller, timeout.Token);
        Assert.AreEqual(TaskGraphTaskStatus.AwaitingAcceptance, State(reconciled, "effect").Status);
        Assert.AreEqual(claim.AttemptId, State(reconciled, "effect").AttemptId);
        Assert.IsEmpty(executor.Calls);
        Assert.IsEmpty(verifier.Calls);

        var resumed = await runtime.ResumeAsync(graph.GraphId, Caller, timeout.Token);

        Assert.AreEqual("dependent", Assert.ContainsSingle(executor.Calls).Task.TaskId);
        Assert.AreSequenceEqual(["effect", "dependent"], verifier.Calls.Select(item => item.Task.TaskId).ToArray());
        foreach (var task in resumed.Tasks)
        {
            Assert.AreEqual(TaskGraphTaskStatus.Succeeded, task.Status);
        }
        Assert.AreEqual(claim.AttemptId, State(resumed, "effect").Acceptance!.AttemptId);
        Assert.AreEqual(reconciled.Tasks[0].ReconciliationNote, State(resumed, "effect").ReconciliationNote);
    }

    [TestMethod]
    [DataRow(WorkflowControl.Pause)]
    [DataRow(WorkflowControl.Cancel)]
    public async Task LateReadOnlyVerificationReceipt_PreservesControlAndCannotDispatchDependent(WorkflowControl control)
    {
        await using var scope = await Scope.OpenAsync();
        using var timeout = new CancellationTokenSource(Deadline);
        var verifying = Signal();
        var release = Signal();
        var executor = new RecordingExecutor();
        var verifier = new RecordingVerifier(async (claim, _, _) =>
        {
            if (claim.Task.TaskId == "parent")
            {
                verifying.TrySetResult();
                await release.Task; // Deliberately ignore cancellation for an already-started read-only check.
            }
            return Accepted();
        });
        var graph = Graph(Task("parent"), Task("dependent", "parent"));
        var runtime = scope.Runtime(executor, verifier);
        var run = runtime.StartAsync(graph, Caller, 1, timeout.Token);
        try
        {
            await verifying.Task.WaitAsync(timeout.Token);
            if (control == WorkflowControl.Pause) await runtime.PauseAsync(graph.GraphId, Caller, timeout.Token);
            else await runtime.CancelAsync(graph.GraphId, Caller, timeout.Token);
            release.TrySetResult();
            var settled = await run.WaitAsync(timeout.Token);
            Assert.AreEqual(control, settled.Control);
            Assert.AreEqual(TaskGraphTaskStatus.Succeeded, State(settled, "parent").Status);
            Assert.IsNull(State(settled, "dependent").AttemptId);
            Assert.AreEqual("parent", Assert.ContainsSingle(executor.Calls).Task.TaskId);
            Assert.AreEqual(control == WorkflowControl.Pause ? TaskGraphRunStatus.Paused : TaskGraphRunStatus.Cancelled, TaskGraphProgress.Inspect(settled).Status);
            if (control == WorkflowControl.Cancel)
                await Assert.ThrowsExactlyAsync<WorkflowCancelledException>(() => runtime.ResumeAsync(graph.GraphId, Caller, timeout.Token));
            else
            {
                var resumed = await runtime.ResumeAsync(graph.GraphId, Caller, timeout.Token);
                Assert.AreEqual(TaskGraphRunStatus.Succeeded, TaskGraphProgress.Inspect(resumed).Status);
                Assert.AreEqual(1, executor.Calls.Count(claim => claim.Task.TaskId == "parent"));
                Assert.AreEqual(1, verifier.Calls.Count(claim => claim.Task.TaskId == "parent"));
            }
        }
        finally { release.TrySetResult(); timeout.Cancel(); await DrainAsync(run); }
    }

    [TestMethod]
    public async Task CompetingSchedulers_ShareTheStoreConcurrencyLimitAndNeverDuplicateAnAttempt()
    {
        await using var scope = await Scope.OpenAsync();
        using var timeout = new CancellationTokenSource(Deadline);
        var entered = Signal();
        var capacity = Signal();
        var release = Signal();
        var count = 0;
        var executor = new RecordingExecutor(async (claim, ct) =>
        {
            var started = Interlocked.Increment(ref count);
            entered.TrySetResult();
            if (started == 2) capacity.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Success(claim);
        });
        var graph = Graph(Enumerable.Range(0, 6).Select(index => Task("independent-" + index)).ToArray());
        var first = scope.Runtime(executor, new RecordingVerifier());
        var second = scope.Runtime(executor, new RecordingVerifier());
        var firstRun = first.StartAsync(graph, Caller, 2, timeout.Token);
        System.Threading.Tasks.Task<TaskGraphCheckpoint>? secondRun = null;
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            secondRun = second.ResumeAsync(graph.GraphId, Caller, timeout.Token);
            await capacity.Task.WaitAsync(timeout.Token);
            Assert.AreEqual(2, executor.Calls.Count);
            Assert.AreEqual(2, (await scope.Store.GetTaskGraphAsync(graph.GraphId, timeout.Token))!.Tasks.Count(state => state.LeaseToken is not null));
            release.TrySetResult();
            await System.Threading.Tasks.Task.WhenAll(firstRun, secondRun).WaitAsync(timeout.Token);
            var settled = await first.ResumeAsync(graph.GraphId, Caller, timeout.Token);
            Assert.AreEqual(TaskGraphRunStatus.Succeeded, TaskGraphProgress.Inspect(settled).Status);
            Assert.AreEqual(6, executor.Calls.Count);
            Assert.AreEqual(6, executor.Calls.Select(claim => claim.AttemptId).Distinct().Count());
            Assert.AreEqual(2, executor.MaximumActive);
        }
        finally
        {
            release.TrySetResult(); timeout.Cancel(); await DrainAsync(firstRun);
            if (secondRun is not null) await DrainAsync(secondRun);
        }
    }

    [TestMethod]
    public async Task MissingHostExecutionOrVerificationBoundary_IsRejectedAtConstruction()
    {
        await using var scope = await Scope.OpenAsync();
        var executor = new RecordingExecutor();
        var verifier = new RecordingVerifier();
        Assert.ThrowsExactly<ArgumentNullException>(() => new TaskGraphScheduler(null!, executor, verifier));
        Assert.ThrowsExactly<ArgumentNullException>(() => new TaskGraphScheduler(scope.Store, null!, verifier));
        Assert.ThrowsExactly<ArgumentNullException>(() => new TaskGraphScheduler(scope.Store, executor, null!));
    }

    private static WorkflowTaskGraph Graph(params WorkflowTaskDefinition[] tasks) =>
        new("graph-" + Guid.NewGuid().ToString("n"), "Complete the declared task graph.", tasks.ToImmutableArray());

    private static WorkflowTaskDefinition Task(string taskId, params string[] dependencies) =>
        new(taskId, "Complete " + taskId)
        {
            Dependencies = dependencies.ToImmutableArray(),
            AcceptanceCriteria = [new("accepted", "Trusted inspection accepts the exact result revision.")]
        };

    private static WorkflowTaskResult Success(TaskGraphClaim claim) => Outcome(claim, WorkflowTaskStatus.Succeeded);
    private static WorkflowTaskResult Outcome(TaskGraphClaim claim, WorkflowTaskStatus status)
    {
        var revision = "revision-" + claim.Task.TaskId;
        return new(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId, status, revision,
            "Host established the task outcome.")
        {
            VerificationEvidence = status == WorkflowTaskStatus.Succeeded
                ? [new("host-observation", "accepted", revision, TaskVerificationOutcome.Passed, "Examined the exact revision.")]
                : []
        };
    }
    private static TaskAcceptanceDecision Accepted() => new(TaskAcceptanceOutcome.Accepted,
        "trusted-test-verifier", "Independently examined the exact result revision.");

    [TestMethod]
    public async Task BudgetExhaustionRacingVerifierAcceptance_SettlesWithoutAnotherResume()
    {
        var time = new ManualTimeProvider();
        await using var scope = await Scope.OpenAsync(time);
        using var timeout = new CancellationTokenSource(Deadline);
        var graph = Graph(Task("candidate"), Task("blocked", "candidate"));
        var binding = new TaskGraphAgentBinding(AgentTaskIdentity.RootRunId(graph.GraphId).Value,
            Path.Combine(Path.GetTempPath(), graph.GraphId + "-source"), new string('a', 40),
            Path.Combine(Path.GetTempPath(), graph.GraphId + "-managed"), "test", "model", new(ToolProfile.Minimal),
            new(10, 10, MaxExecutionTime: TimeSpan.FromSeconds(1)));
        await scope.Store.AcceptAgentTaskGraphAsync(graph, Caller, 1, binding, timeout.Token);
        var executor = new RecordingExecutor();
        var verifier = new RecordingVerifier((_, _, _) =>
        {
            time.Advance(TimeSpan.FromSeconds(2));
            return System.Threading.Tasks.Task.FromResult(Accepted());
        });

        var result = await scope.Runtime(executor, verifier).ResumeAsync(graph.GraphId, Caller, timeout.Token);

        var candidate = State(result, "candidate");
        Assert.AreEqual(TaskGraphTaskStatus.BudgetExhausted, candidate.Status);
        Assert.AreEqual(TaskAcceptanceOutcome.BudgetExhausted, candidate.Acceptance!.Outcome);
        Assert.AreEqual("shared-budget/v1", candidate.Acceptance.VerifierId);
        Assert.IsNull(candidate.LeaseToken);
        Assert.IsNull(candidate.Error);
        Assert.AreEqual(TaskGraphRunStatus.BudgetExhausted, TaskGraphProgress.Inspect(result).Status);
        Assert.IsNull(State(result, "blocked").AttemptId);
        Assert.ContainsSingle(executor.Calls);
        Assert.ContainsSingle(verifier.Calls);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    private static TaskGraphTaskState State(TaskGraphCheckpoint graph, string taskId) => graph.Tasks.Single(task => task.TaskId == taskId);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async System.Threading.Tasks.Task DrainAsync(System.Threading.Tasks.Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch { /* Cleanup must drain test-owned work without masking the original assertion. */ }
    }

    private sealed class RecordingExecutor(Func<TaskGraphClaim, CancellationToken, Task<WorkflowTaskResult>>? handler = null)
        : IWorkflowTaskExecutor
    {
        private int _active;
        private int _maximumActive;
        public ConcurrentQueue<TaskGraphClaim> Calls { get; } = new();
        public int MaximumActive => Volatile.Read(ref _maximumActive);
        public async Task<WorkflowTaskResult> ExecuteAsync(TaskGraphClaim claim, CancellationToken ct)
        {
            Calls.Enqueue(claim);
            var active = Interlocked.Increment(ref _active);
            var previous = Volatile.Read(ref _maximumActive);
            while (active > previous)
            {
                var observed = Interlocked.CompareExchange(ref _maximumActive, active, previous);
                if (observed == previous) break;
                previous = observed;
            }
            try { return handler is null ? Success(claim) : await handler(claim, ct); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class RecordingVerifier(Func<TaskGraphClaim, WorkflowTaskResult, CancellationToken, Task<TaskAcceptanceDecision>>? handler = null)
        : ITaskAcceptanceVerifier
    {
        public ConcurrentQueue<TaskGraphClaim> Calls { get; } = new();
        public Task<TaskAcceptanceDecision> VerifyAsync(TaskGraphClaim claim, WorkflowTaskResult result, CancellationToken ct)
        {
            Calls.Enqueue(claim);
            return handler is null ? System.Threading.Tasks.Task.FromResult(Accepted()) : handler(claim, result, ct);
        }
    }

    private sealed class Scope : IAsyncDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("IronPaw-taskgraph-tests-");
        public CSharpDbWorkflowStore Store { get; private set; } = null!;
        public static async Task<Scope> OpenAsync(TimeProvider? timeProvider = null)
        {
            var scope = new Scope();
            scope.Store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(scope._directory.FullName, "graphs.cdb"), timeProvider: timeProvider);
            return scope;
        }
        public TaskGraphScheduler Runtime(IWorkflowTaskExecutor executor, ITaskAcceptanceVerifier verifier) =>
            new(Store, executor, verifier);
        public async System.Threading.Tasks.Task ReopenAsync()
        {
            await Store.DisposeAsync();
            Store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(_directory.FullName, "graphs.cdb"));
        }
        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            _directory.Delete(recursive: true);
        }
    }
}
