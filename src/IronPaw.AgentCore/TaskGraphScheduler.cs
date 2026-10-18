using System.Collections.Concurrent;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

/// <summary>
/// Drives durable dependency claims through host-owned execution and read-only verification.
/// It supplies no model/tool adapter and grants no permissions or budget outside the host boundary.
/// </summary>
public sealed class TaskGraphScheduler
{
    private readonly ITaskGraphStore store;
    private readonly IWorkflowTaskExecutor executor;
    private readonly ITaskAcceptanceVerifier verifier;
    private readonly ITaskVerificationRunner? verificationRunner;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);

    public TaskGraphScheduler(ITaskGraphStore store, IWorkflowTaskExecutor executor, ITaskAcceptanceVerifier verifier,
        ITaskVerificationRunner? verificationRunner = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        this.verificationRunner = verificationRunner;
    }

    public async Task<TaskGraphCheckpoint> StartAsync(WorkflowTaskGraph graph, CallerIdentity caller,
        int maxConcurrency, CancellationToken ct)
    {
        var accepted = await store.AcceptTaskGraphAsync(graph, caller, maxConcurrency, ct).ConfigureAwait(false);
        return await ResumeAsync(accepted.Graph.GraphId, caller, ct).ConfigureAwait(false);
    }

    public Task<TaskGraphCheckpoint> ResumeAsync(string graphId, CallerIdentity caller, CancellationToken ct) =>
        ResumeCoreAsync(graphId, caller, ct, clearPause: true);

    private async Task<TaskGraphCheckpoint> ResumeCoreAsync(string graphId, CallerIdentity caller, CancellationToken ct, bool clearPause)
    {
        var checkpoint = await RequireAsync(graphId, caller, ct).ConfigureAwait(false);
        if (!clearPause && checkpoint.Control != WorkflowControl.None) return checkpoint;
        if (checkpoint.Control == WorkflowControl.Cancel)
            throw new WorkflowCancelledException("The task graph was permanently cancelled.");
        using var executionCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_active.TryAdd(graphId, executionCt))
            throw new WorkflowConflictException("This scheduler is already running the task graph.");
        var running = new List<Task>();
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (checkpoint.Control == WorkflowControl.Pause)
                checkpoint = await store.ControlTaskGraphAsync(graphId, WorkflowControl.None, caller, ct).ConfigureAwait(false);
            while (!executionCt.IsCancellationRequested)
            {
                checkpoint = await RequireAsync(graphId, caller, executionCt.Token).ConfigureAwait(false);
                if (checkpoint.Control != WorkflowControl.None)
                {
                    CancelSafely(executionCt);
                    break;
                }
                while (running.Count < checkpoint.MaxConcurrency && !executionCt.IsCancellationRequested)
                {
                    TaskGraphClaim? claim;
                    try { claim = await store.ClaimTaskAsync(graphId, caller, executionCt.Token, attempted).ConfigureAwait(false); }
                    catch (WorkflowCancelledException) { CancelSafely(executionCt); break; }
                    catch (WorkflowSuspendedException) { CancelSafely(executionCt); break; }
                    if (claim is null) break;
                    attempted.Add(claim.Task.TaskId);
                    running.Add(ProcessClaimAsync(claim, executionCt.Token));
                }
                if (running.Count == 0) break;

                // Observe durable control changes made by another scheduler instance.
                var signal = Task.Delay(TimeSpan.FromMilliseconds(50), executionCt.Token);
                await Task.WhenAny(running.Append(signal)).ConfigureAwait(false);
                for (var index = running.Count - 1; index >= 0; index--)
                    if (running[index].IsCompleted)
                    {
                        await running[index].ConfigureAwait(false);
                        running.RemoveAt(index);
                    }
            }
        }
        catch (OperationCanceledException) when (executionCt.IsCancellationRequested) { }
        finally
        {
            CancelSafely(executionCt);
            try { await Task.WhenAll(running).ConfigureAwait(false); }
            finally { _active.TryRemove(graphId, out _); }
        }
        using var readCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await RequireAsync(graphId, caller, readCt.Token).ConfigureAwait(false);
    }

    public async Task PauseAsync(string graphId, CallerIdentity caller, CancellationToken ct)
    {
        await store.ControlTaskGraphAsync(graphId, WorkflowControl.Pause, caller, ct).ConfigureAwait(false);
        Signal(graphId);
    }

    public async Task CancelAsync(string graphId, CallerIdentity caller, CancellationToken ct)
    {
        await store.ControlTaskGraphAsync(graphId, WorkflowControl.Cancel, caller, ct).ConfigureAwait(false);
        Signal(graphId);
    }

    public Task<TaskGraphCheckpoint> ReconcileAsync(string graphId, string taskId, WorkflowTaskResult result,
        string note, CallerIdentity caller, CancellationToken ct) =>
        store.ReconcileTaskAsync(graphId, taskId, result, note, caller, ct);

    private async Task ProcessClaimAsync(TaskGraphClaim claim, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = claim.ProposedResult;
            if (result is null)
            {
                result = await executor.ExecuteAsync(claim, ct).ConfigureAwait(false);
                // A returned receipt is still durable if a control/deadline races its return.
                using var receiptCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await store.RecordTaskResultAsync(claim, result, receiptCt.Token).ConfigureAwait(false);
                if (result.Status is not (WorkflowTaskStatus.Succeeded or WorkflowTaskStatus.AwaitingVerification)) return;
            }
            ct.ThrowIfCancellationRequested();
            var graph = await store.GetTaskGraphAsync(claim.GraphId, ct).ConfigureAwait(false)
                ?? throw new WorkflowConflictException("The task graph was not found.");
            if (graph.Control != WorkflowControl.None)
                throw new OperationCanceledException("Task graph control stopped verification.");
            if (verificationRunner is not null)
                await verificationRunner.RunAsync(claim, result, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            graph = await store.GetTaskGraphAsync(claim.GraphId, ct).ConfigureAwait(false)
                ?? throw new WorkflowConflictException("The task graph was not found.");
            if (graph.Control != WorkflowControl.None)
                throw new OperationCanceledException("Task graph control stopped acceptance.");
            var decision = await verifier.VerifyAsync(claim, result, ct).ConfigureAwait(false);
            using var acceptanceCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await store.RecordTaskAcceptanceAsync(claim, decision, acceptanceCt.Token).ConfigureAwait(false); }
            catch (WorkflowBudgetExceededException error) when (decision.Outcome == TaskAcceptanceOutcome.Accepted)
            {
                // The atomic success guard can observe usage or elapsed time that raced
                // the verifier's final read. Establish the known budget outcome now.
                using var budgetCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await store.RecordTaskAcceptanceAsync(claim, new(TaskAcceptanceOutcome.BudgetExhausted,
                    "shared-budget/v1", "The shared graph budget was exhausted before acceptance: " + error.Message),
                    budgetCt.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            using var settleCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await store.AbandonTaskClaimAsync(claim,
                    $"{error.GetType().Name}: {error.Message}", settleCt.Token).ConfigureAwait(false);
            }
            catch (Exception persistenceError)
            {
                error.Data["IronPaw.TaskGraphPersistenceError"] = persistenceError.Message;
                throw new InvalidOperationException("The task claim could not be durably settled. Inspect its journal before recovery.", error);
            }
        }
    }

    internal async Task<TaskGraphCheckpoint> RecoverAgentTaskAsync(string graphId, string taskId,
        CallerIdentity caller, CancellationToken ct)
    {
        if (store is not IAgentTaskGraphStore agentStore)
            throw new NotSupportedException("Agent task recovery requires an agent-bound task graph store.");
        var graph = await RequireAsync(graphId, caller, ct).ConfigureAwait(false);
        if (graph.Control == WorkflowControl.Cancel)
            throw new WorkflowCancelledException("The task graph was permanently cancelled.");
        using var executionCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_active.TryAdd(graphId, executionCt))
            throw new WorkflowConflictException("This scheduler is already running the task graph.");
        try
        {
            if (graph.Control == WorkflowControl.Pause)
                await store.ControlTaskGraphAsync(graphId, WorkflowControl.None, caller, executionCt.Token).ConfigureAwait(false);
            var claim = await agentStore.ReclaimAgentTaskAsync(graphId, taskId, caller, executionCt.Token).ConfigureAwait(false);
            await ProcessClaimAsync(claim, executionCt.Token).ConfigureAwait(false);
        }
        finally { _active.TryRemove(graphId, out _); }
        if (executionCt.IsCancellationRequested)
        {
            using var readCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await RequireAsync(graphId, caller, readCt.Token).ConfigureAwait(false);
        }
        return await ResumeCoreAsync(graphId, caller, ct, clearPause: false).ConfigureAwait(false);
    }

    private async Task<TaskGraphCheckpoint> RequireAsync(string graphId, CallerIdentity caller, CancellationToken ct)
    {
        var graph = await store.GetTaskGraphAsync(graphId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException("The task graph was not found.", nameof(graphId));
        if (graph.Caller.Id != caller.Id || graph.Caller.IsOwner != caller.IsOwner)
            throw new UnauthorizedAccessException("Only the graph's original caller and ownership level can control it.");
        return graph;
    }

    private void Signal(string graphId)
    {
        if (_active.TryGetValue(graphId, out var cancellation)) CancelSafely(cancellation);
    }

    private static void CancelSafely(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* Faulty host callbacks cannot prevent other claims from being cancelled. */ }
    }
}
