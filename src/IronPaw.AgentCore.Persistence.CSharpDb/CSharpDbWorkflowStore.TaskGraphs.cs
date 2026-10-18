using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence.CSharpDb;

public sealed partial class CSharpDbWorkflowStore : IAgentTaskGraphStore
{
    private static readonly JsonSerializerOptions TaskGraphJsonOptions = new() { MaxDepth = 256 };

    public async Task<TaskGraphCheckpoint> AcceptTaskGraphAsync(WorkflowTaskGraph graph, CallerIdentity caller,
        int maxConcurrency, CancellationToken ct)
    {
        ValidateTaskGraphCaller(caller);
        if (maxConcurrency is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        var definition = TaskGraphValidator.Validate(graph);
        if (!definition.IsValid) throw new WorkflowConflictException("The task graph is invalid: " + string.Join(" ", definition.Errors.Select(x => x.Message)));
        var now = _timeProvider.GetUtcNow();
        var proposed = new TaskGraphCheckpoint(definition.Graph!, definition.DefinitionFingerprint!, caller,
            maxConcurrency, definition.Graph!.Tasks.Select(task => new TaskGraphTaskState(task.TaskId)).ToImmutableArray(), now, now);
        TaskGraphCheckpoint? accepted = null;
        await InGateAsync(async () =>
        {
            await TransactionAsync(async () =>
            {
                var existing = await ReadTaskGraphAsync(proposed.Graph.GraphId, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    RequireTaskGraphCaller(existing, caller);
                    if (existing.AgentBinding is not null || existing.DefinitionFingerprint != proposed.DefinitionFingerprint || existing.MaxConcurrency != maxConcurrency)
                        throw new WorkflowConflictException("An accepted graph cannot replace its definition or concurrency limit.");
                    accepted = existing;
                    return;
                }
                var id = await NextIdAsync("task_graphs", ct).ConfigureAwait(false);
                await ExecuteAsync($"INSERT INTO task_graphs VALUES ({id}, {Quote(proposed.Graph.GraphId)}, {Quote(SerializeTaskGraph(proposed))})", ct).ConfigureAwait(false);
                await AppendTaskGraphJournalAsync(proposed, "accepted", null, null, ct).ConfigureAwait(false);
                accepted = proposed;
            }, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return accepted!;
    }

    public async Task<TaskGraphCheckpoint?> GetTaskGraphAsync(string graphId, CancellationToken ct)
    {
        RequireTaskGraphIdentity(graphId, nameof(graphId));
        TaskGraphCheckpoint? checkpoint = null;
        await InGateAsync(async () => checkpoint = await ReadTaskGraphAsync(graphId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return checkpoint;
    }

    public async Task<IReadOnlyList<TaskGraphCheckpoint>> ListTaskGraphsAsync(CancellationToken ct)
    {
        IReadOnlyList<TaskGraphCheckpoint> checkpoints = [];
        await InGateAsync(async () => checkpoints = await ReadTaskGraphsAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return checkpoints;
    }

    public async Task<TaskGraphClaim?> ClaimTaskAsync(string graphId, CallerIdentity caller, CancellationToken ct,
        IReadOnlyCollection<string>? excludedTaskIds = null)
    {
        var excluded = (excludedTaskIds ?? []).ToHashSet(StringComparer.Ordinal);
        TaskGraphClaim? claim = null;
        await MutateTaskGraphAsync(graphId, async checkpoint =>
        {
            RequireTaskGraphCaller(checkpoint, caller);
            if (checkpoint.Control != WorkflowControl.None || checkpoint.Tasks.Count(task => task.LeaseToken is not null) >= checkpoint.MaxConcurrency)
                return null;
            foreach (var definition in checkpoint.Graph.Tasks)
            {
                if (excluded.Contains(definition.TaskId)) continue;
                var index = TaskStateIndex(checkpoint, definition.TaskId);
                var state = checkpoint.Tasks[index];
                if (state.Verifications.Any(invocation => invocation.State != TaskVerificationInvocationState.Completed)) continue;
                var resumeVerification = state.Status == TaskGraphTaskStatus.AwaitingAcceptance && state.LeaseToken is null;
                if (!resumeVerification && (state.Status != TaskGraphTaskStatus.Pending || !definition.Dependencies.All(id =>
                    checkpoint.Tasks.Any(dependency => dependency.TaskId == id && dependency.Status == TaskGraphTaskStatus.Succeeded && dependency.Acceptance?.Outcome == TaskAcceptanceOutcome.Accepted))))
                    continue;
                var attemptId = resumeVerification ? state.AttemptId! : Guid.NewGuid().ToString("n");
                var token = Guid.NewGuid().ToString("n");
                var inputs = resumeVerification ? state.Inputs : checkpoint.AgentBinding?.DependencyMode == TaskDependencyMode.AcceptedChanges
                    ? TaskDependencyResolver.CreateInputs(checkpoint, definition.TaskId, attemptId) : null;
                var updatedState = state with { Status = resumeVerification ? TaskGraphTaskStatus.AwaitingAcceptance : TaskGraphTaskStatus.Running,
                    AttemptId = attemptId, LeaseToken = token, Error = null, Inputs = inputs };
                claim = new(graphId, checkpoint.DefinitionFingerprint, definition, attemptId, token, state.Result, inputs);
                return await SaveTaskGraphAsync(checkpoint with { Tasks = checkpoint.Tasks.SetItem(index, updatedState) },
                    resumeVerification ? "verification_claimed" : "task_claimed", definition.TaskId, attemptId, ct).ConfigureAwait(false);
            }
            return null;
        }, ct).ConfigureAwait(false);
        return claim;
    }

    public Task<TaskGraphCheckpoint> RecordTaskResultAsync(TaskGraphClaim claim, WorkflowTaskResult result, CancellationToken ct) =>
        MutateClaimAsync(claim, async (checkpoint, index, state) =>
        {
            ValidateTaskResult(checkpoint, state.TaskId, result);
            if (state.Result is not null)
            {
                if (SerializeTaskGraph(state.Result) == SerializeTaskGraph(result)) return checkpoint;
                throw new WorkflowConflictException("An established task result cannot be replaced.");
            }
            if (state.Status != TaskGraphTaskStatus.Running) throw new WorkflowConflictException("Only a running task can record its result.");
            var status = ResultTaskStatus(result.Status);
            var updated = state with { Status = status, Result = result, Error = null,
                LeaseToken = status == TaskGraphTaskStatus.AwaitingAcceptance ? state.LeaseToken : null };
            return await SaveTaskGraphAsync(checkpoint with { Tasks = checkpoint.Tasks.SetItem(index, updated) },
                "task_result", state.TaskId, state.AttemptId, ct).ConfigureAwait(false);
        }, ct);

    public Task<TaskGraphCheckpoint> RecordTaskAcceptanceAsync(TaskGraphClaim claim, TaskAcceptanceDecision decision, CancellationToken ct) =>
        MutateClaimAsync(claim, async (checkpoint, index, state) =>
        {
            ValidateTaskAcceptanceDecision(decision);
            if (state.Status != TaskGraphTaskStatus.AwaitingAcceptance || state.Result?.Status is not (WorkflowTaskStatus.Succeeded or WorkflowTaskStatus.AwaitingVerification))
                throw new WorkflowConflictException("Acceptance requires the current successful proposal.");
            if (decision.Outcome == TaskAcceptanceOutcome.Accepted && !CanAcceptTaskProposal(checkpoint, state))
                throw new WorkflowConflictException("Acceptance requires declared required criteria, required artifacts, resolved issues, and no failing required evidence.");
            if (state.Verifications.Any(invocation => invocation.State != TaskVerificationInvocationState.Completed))
                throw new WorkflowReconciliationRequiredException("Active or uncertain verification must settle before acceptance.");
            if (decision.Outcome == TaskAcceptanceOutcome.Accepted)
                await RequireGraphBudgetAvailableAsync(checkpoint, ct).ConfigureAwait(false);
            var receipt = new TaskAcceptanceReceipt(decision.Outcome, decision.VerifierId, decision.Explanation,
                checkpoint.DefinitionFingerprint, state.TaskId, state.AttemptId!, TaskResultDigest(state.Result), state.Result.Revision,
                _timeProvider.GetUtcNow());
            var status = decision.Outcome switch { TaskAcceptanceOutcome.Accepted => TaskGraphTaskStatus.Succeeded,
                TaskAcceptanceOutcome.Rejected => TaskGraphTaskStatus.Failed, TaskAcceptanceOutcome.BudgetExhausted => TaskGraphTaskStatus.BudgetExhausted,
                _ => TaskGraphTaskStatus.AwaitingAcceptance };
            var updated = state with { Status = status, Acceptance = receipt, LeaseToken = null, Error = null };
            return await SaveTaskGraphAsync(checkpoint with { Tasks = checkpoint.Tasks.SetItem(index, updated) },
                "task_acceptance", state.TaskId, state.AttemptId, ct).ConfigureAwait(false);
        }, ct);

    public Task<TaskGraphCheckpoint> AbandonTaskClaimAsync(TaskGraphClaim claim, string reason, CancellationToken ct) =>
        MutateClaimAsync(claim, async (checkpoint, index, state) =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            if (state.Status is not (TaskGraphTaskStatus.Running or TaskGraphTaskStatus.AwaitingAcceptance))
                throw new WorkflowConflictException("This task claim cannot be abandoned.");
            var updated = state with { LeaseToken = null, Error = reason, Verifications = InterruptVerifications(state),
                Status = state.Status == TaskGraphTaskStatus.Running ? TaskGraphTaskStatus.NeedsReconciliation : state.Status };
            return await SaveTaskGraphAsync(checkpoint with { Tasks = checkpoint.Tasks.SetItem(index, updated) },
                "task_abandoned", state.TaskId, state.AttemptId, ct).ConfigureAwait(false);
        }, ct);

    public async Task<TaskGraphCheckpoint> ReconcileTaskAsync(string graphId, string taskId, WorkflowTaskResult result,
        string note, CallerIdentity caller, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        TaskGraphCheckpoint? saved = null;
        await MutateTaskGraphAsync(graphId, async checkpoint =>
        {
            RequireTaskGraphCaller(checkpoint, caller);
            var index = TaskStateIndex(checkpoint, taskId);
            var state = checkpoint.Tasks[index];
            if (state.Status != TaskGraphTaskStatus.NeedsReconciliation || state.LeaseToken is not null || state.Result is not null)
                throw new WorkflowConflictException("Only an uncertain, unleased task can be reconciled.");
            if (checkpoint.AgentBinding is not null)
            {
                var childId = AgentTaskIdentity.ChildRunId(checkpoint.DefinitionFingerprint, taskId, state.AttemptId!);
                var child = await ReadOneAsync(childId.Value, ct).ConfigureAwait(false);
                if (child is not null && (child.LeaseOwner is not null || WorkflowIdentity.HasUnknownEffects(child)))
                    throw new WorkflowReconciliationRequiredException("The child has active or uncertain effects. Establish their outcomes before reconciling the task.");
            }
            ValidateTaskResult(checkpoint, taskId, result);
            var updated = state with { Status = ResultTaskStatus(result.Status), Result = result, ReconciliationNote = note, Error = null };
            return saved = await SaveTaskGraphAsync(checkpoint with { Tasks = checkpoint.Tasks.SetItem(index, updated) },
                "task_reconciled", taskId, state.AttemptId, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return saved!;
    }

    public async Task<TaskGraphCheckpoint> ControlTaskGraphAsync(string graphId, WorkflowControl control, CallerIdentity caller, CancellationToken ct)
    {
        if (!Enum.IsDefined(control)) throw new ArgumentOutOfRangeException(nameof(control));
        TaskGraphCheckpoint? saved = null;
        await MutateTaskGraphAsync(graphId, async checkpoint =>
        {
            RequireTaskGraphCaller(checkpoint, caller);
            if (checkpoint.Control == control) return saved = checkpoint;
            if (checkpoint.Control == WorkflowControl.Cancel) throw new WorkflowCancelledException("This task graph has been permanently cancelled.");
            if (control is WorkflowControl.Pause or WorkflowControl.Cancel && TaskGraphProgress.Inspect(checkpoint).Status is
                TaskGraphRunStatus.Succeeded or TaskGraphRunStatus.Failed or TaskGraphRunStatus.Cancelled or TaskGraphRunStatus.BudgetExhausted)
                throw new WorkflowConflictException("A completed task graph cannot be paused or cancelled.");
            if (control == WorkflowControl.None && checkpoint.Tasks.Any(task => task.LeaseToken is not null))
                throw new WorkflowConflictException("Active task claims must settle before clearing pause.");
            if (control == WorkflowControl.None && checkpoint.AgentBinding is { } binding &&
                (await ReadAllAsync(ct).ConfigureAwait(false)).Any(run => run.RootRunId == binding.RootRunId && run.LeaseOwner is not null))
                throw new WorkflowConflictException("Active child invocations must settle before clearing pause.");
            return saved = await SaveTaskGraphAsync(checkpoint with { Control = control }, "control_" + control.ToString().ToLowerInvariant(), null, null, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return saved!;
    }

    public async Task<IReadOnlyList<TaskGraphJournalEntry>> ReadTaskGraphJournalAsync(string graphId, CancellationToken ct)
    {
        RequireTaskGraphIdentity(graphId, nameof(graphId));
        var entries = new List<TaskGraphJournalEntry>();
        await InGateAsync(async () =>
        {
            var query = await _database.ExecuteAsync($"SELECT revision, timestamp, event_type, task_id, attempt_id FROM task_graph_journal WHERE graph_id = {Quote(graphId)} ORDER BY revision", ct).ConfigureAwait(false);
            try
            {
                while (await query.MoveNextAsync(ct).ConfigureAwait(false))
                    entries.Add(new(graphId, checked((int)query.Current[0].AsInteger), DateTimeOffset.Parse(query.Current[1].AsText,
                        System.Globalization.CultureInfo.InvariantCulture), query.Current[2].AsText,
                        query.Current[3].IsNull ? null : query.Current[3].AsText, query.Current[4].IsNull ? null : query.Current[4].AsText));
            }
            finally { await query.DisposeAsync().ConfigureAwait(false); }
        }, ct).ConfigureAwait(false);
        return entries;
    }

    private async Task InitializeTaskGraphsAsync(CancellationToken ct)
    {
        await EnsureTaskGraphTablesAsync(ct).ConfigureAwait(false);
        // Reopening fences claims from the previous store ownership. Physical workers
        // may still exist; their execution is uncertain and their old tokens cannot settle it.
        var graphs = await ReadTaskGraphsAsync(ct).ConfigureAwait(false);
        await TransactionAsync(async () =>
        {
            foreach (var graph in graphs)
            {
                var tasks = graph.Tasks.Select(state => state.Status == TaskGraphTaskStatus.Running
                    ? state with { Status = TaskGraphTaskStatus.NeedsReconciliation, LeaseToken = null, Error = "The executing host stopped before establishing an outcome." }
                    : state.LeaseToken is not null || state.Verifications.Any(invocation => invocation.State == TaskVerificationInvocationState.Started)
                        ? state with { LeaseToken = null, Verifications = InterruptVerifications(state) } : state).ToImmutableArray();
                if (tasks.Where((state, index) => state != graph.Tasks[index]).Any())
                    await SaveTaskGraphAsync(graph with { Tasks = tasks }, "recovered", null, null, ct).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task EnsureTaskGraphTablesAsync(CancellationToken ct)
    {
        await ExecuteAsync("CREATE TABLE IF NOT EXISTS task_graphs (id INTEGER PRIMARY KEY, graph_id TEXT, payload TEXT)", ct).ConfigureAwait(false);
        await ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS task_graphs_graph_id ON task_graphs (graph_id)", ct).ConfigureAwait(false);
        await ExecuteAsync("CREATE TABLE IF NOT EXISTS task_graph_journal (id INTEGER PRIMARY KEY, graph_id TEXT, revision INTEGER, timestamp TEXT, event_type TEXT, task_id TEXT, attempt_id TEXT)", ct).ConfigureAwait(false);
        await ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS task_graph_journal_revision ON task_graph_journal (graph_id, revision)", ct).ConfigureAwait(false);
    }

    private async Task<TaskGraphCheckpoint> MutateClaimAsync(TaskGraphClaim claim,
        Func<TaskGraphCheckpoint, int, TaskGraphTaskState, Task<TaskGraphCheckpoint>> action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        TaskGraphCheckpoint? saved = null;
        await MutateTaskGraphAsync(claim.GraphId, async checkpoint =>
        {
            var index = TaskStateIndex(checkpoint, claim.Task.TaskId);
            var state = checkpoint.Tasks[index];
            if (checkpoint.DefinitionFingerprint != claim.GraphFingerprint || state.AttemptId != claim.AttemptId ||
                state.LeaseToken is null || state.LeaseToken != claim.LeaseToken ||
                SerializeTaskGraph(checkpoint.Graph.Tasks[index]) != SerializeTaskGraph(claim.Task) ||
                !TaskDependencyResolver.SameInputs(state.Inputs, claim.Inputs))
                throw new WorkflowConflictException("The task claim is stale or does not match the accepted definition.");
            return saved = await action(checkpoint, index, state).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return saved!;
    }

    private async Task MutateTaskGraphAsync(string graphId, Func<TaskGraphCheckpoint, Task<TaskGraphCheckpoint?>> action, CancellationToken ct)
    {
        RequireTaskGraphIdentity(graphId, nameof(graphId));
        await InGateAsync(async () =>
        {
            await TransactionAsync(async () =>
            {
                var checkpoint = await ReadTaskGraphAsync(graphId, ct).ConfigureAwait(false)
                    ?? throw new WorkflowConflictException("The task graph does not exist.");
                await action(checkpoint).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task<TaskGraphCheckpoint> SaveTaskGraphAsync(TaskGraphCheckpoint checkpoint, string eventType,
        string? taskId, string? attemptId, CancellationToken ct)
    {
        var saved = ValidateTaskGraphCheckpoint(checkpoint with { Revision = checked(checkpoint.Revision + 1), UpdatedAt = _timeProvider.GetUtcNow() });
        await ExecuteAsync($"UPDATE task_graphs SET payload = {Quote(SerializeTaskGraph(saved))} WHERE graph_id = {Quote(saved.Graph.GraphId)}", ct).ConfigureAwait(false);
        await AppendTaskGraphJournalAsync(saved, eventType, taskId, attemptId, ct).ConfigureAwait(false);
        if (saved.AgentBinding is { } binding)
        {
            await SynchronizeAgentTaskAnchorAsync(saved, ct).ConfigureAwait(false);
            await UpdateClockAsync(binding.RootRunId, await ReadAllAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        return saved;
    }

    private async Task AppendTaskGraphJournalAsync(TaskGraphCheckpoint graph, string eventType, string? taskId, string? attemptId, CancellationToken ct)
    {
        var id = await NextIdAsync("task_graph_journal", ct).ConfigureAwait(false);
        await ExecuteAsync($"INSERT INTO task_graph_journal VALUES ({id}, {Quote(graph.Graph.GraphId)}, {graph.Revision}, {Quote(graph.UpdatedAt.ToString("O"))}, {Quote(eventType)}, {NullableQuote(taskId)}, {NullableQuote(attemptId)})", ct).ConfigureAwait(false);
    }

    private async Task<TaskGraphCheckpoint?> ReadTaskGraphAsync(string graphId, CancellationToken ct)
    {
        var query = await _database.ExecuteAsync($"SELECT payload FROM task_graphs WHERE graph_id = {Quote(graphId)}", ct).ConfigureAwait(false);
        TaskGraphCheckpoint? checkpoint;
        try { checkpoint = await query.MoveNextAsync(ct).ConfigureAwait(false) ? DeserializeTaskGraph(query.Current[0].AsText) : null; }
        finally { await query.DisposeAsync().ConfigureAwait(false); }
        if (checkpoint is not null) await ValidateAgentTaskAnchorAsync(checkpoint, ct).ConfigureAwait(false);
        return checkpoint;
    }

    private async Task<IReadOnlyList<TaskGraphCheckpoint>> ReadTaskGraphsAsync(CancellationToken ct)
    {
        var graphs = new List<TaskGraphCheckpoint>();
        var query = await _database.ExecuteAsync("SELECT payload FROM task_graphs ORDER BY id", ct).ConfigureAwait(false);
        try { while (await query.MoveNextAsync(ct).ConfigureAwait(false)) graphs.Add(DeserializeTaskGraph(query.Current[0].AsText)); }
        finally { await query.DisposeAsync().ConfigureAwait(false); }
        foreach (var graph in graphs) await ValidateAgentTaskAnchorAsync(graph, ct).ConfigureAwait(false);
        return graphs;
    }

    private static TaskGraphCheckpoint DeserializeTaskGraph(string payload) => ValidateTaskGraphCheckpoint(
        JsonSerializer.Deserialize<TaskGraphCheckpoint>(payload, TaskGraphJsonOptions) ?? throw new InvalidDataException("The task graph checkpoint is missing."));

    private static TaskGraphCheckpoint ValidateTaskGraphCheckpoint(TaskGraphCheckpoint checkpoint)
    {
        if (checkpoint.SchemaVersion is not (1 or 2 or 3 or 4) ||
            (checkpoint.SchemaVersion == 1) != (checkpoint.AgentBinding is null))
            throw new InvalidDataException("Unsupported task graph checkpoint schema or binding.");
        ValidateTaskGraphCaller(checkpoint.Caller);
        if (checkpoint.Revision < 0 || checkpoint.MaxConcurrency is < 1 or > 64 || !Enum.IsDefined(checkpoint.Control))
            throw new InvalidDataException("Task graph checkpoint metadata is invalid.");
        var definition = TaskGraphValidator.Validate(checkpoint.Graph);
        if (!definition.IsValid || checkpoint.DefinitionFingerprint != definition.DefinitionFingerprint)
            throw new InvalidDataException("The accepted task graph definition or fingerprint is invalid.");
        var binding = checkpoint.AgentBinding is { } agentBinding
            ? AgentTaskBindingValidator.Validate(definition.Graph!, checkpoint.Caller, agentBinding) : null;
        if (binding is not null && (checkpoint.SchemaVersion is 3 or 4) != (binding.DependencyMode == TaskDependencyMode.AcceptedChanges) ||
            (checkpoint.SchemaVersion == 4) != (binding?.VerificationAuthority == TaskVerificationAuthority.TrustedLocalProcess))
            throw new InvalidDataException("Task graph schema does not match its immutable dependency mode.");
        if (checkpoint.Tasks.IsDefault || checkpoint.Tasks.Length != definition.Graph!.Tasks.Length)
            throw new InvalidDataException("Task states do not match the accepted definition.");
        var attemptIds = new HashSet<string>(StringComparer.Ordinal);
        var leases = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < checkpoint.Tasks.Length; index++)
        {
            var state = checkpoint.Tasks[index];
            if (state is null || state.TaskId != definition.Graph.Tasks[index].TaskId || !Enum.IsDefined(state.Status))
                throw new InvalidDataException("Task state identities or statuses are invalid.");
            ValidateTaskVerifications(checkpoint, state);
            if (state.Status == TaskGraphTaskStatus.Pending)
            {
                if (state.AttemptId is not null || state.LeaseToken is not null || state.Result is not null || state.Acceptance is not null || state.Inputs is not null)
                    throw new InvalidDataException("A pending task cannot contain execution receipts.");
                continue;
            }
            if (!TaskGraphIdentity(state.AttemptId) || !attemptIds.Add(state.AttemptId!))
                throw new InvalidDataException("Task attempt identities must be nonblank and unique.");
            if (binding?.DependencyMode == TaskDependencyMode.AcceptedChanges)
            {
                var expectedInputs = TaskDependencyResolver.CreateInputs(checkpoint, state.TaskId, state.AttemptId!);
                if (state.Inputs is null || !TaskDependencyResolver.SameInputs(expectedInputs, state.Inputs))
                    throw new InvalidDataException("An attempted task does not match its frozen accepted predecessor inputs.");
            }
            else if (state.Inputs is not null)
                throw new InvalidDataException("Legacy task graphs cannot acquire accepted predecessor inputs.");
            if (!definition.Graph.Tasks[index].Dependencies.All(id => checkpoint.Tasks.Any(dependency =>
                dependency.TaskId == id && dependency.Status == TaskGraphTaskStatus.Succeeded &&
                dependency.Acceptance?.Outcome == TaskAcceptanceOutcome.Accepted)))
                throw new InvalidDataException("An attempted task requires accepted successful dependencies.");
            if (state.LeaseToken is not null && (!TaskGraphIdentity(state.LeaseToken) || !leases.Add(state.LeaseToken)))
                throw new InvalidDataException("Task claim tokens must be nonblank and unique.");
            if (state.Status == TaskGraphTaskStatus.Running)
            {
                if (state.LeaseToken is null || state.Result is not null || state.Acceptance is not null)
                    throw new InvalidDataException("A running task requires a claim and cannot have a result.");
                continue;
            }
            if (state.Status == TaskGraphTaskStatus.NeedsReconciliation)
            {
                if (state.LeaseToken is not null || state.Result is not null || state.Acceptance is not null)
                    throw new InvalidDataException("An uncertain task cannot have a live claim or established result.");
                continue;
            }
            if (state.Result is null) throw new InvalidDataException("A settled task requires an established result.");
            ValidateTaskResult(checkpoint, state.TaskId, state.Result);
            if (state.Status != TaskGraphTaskStatus.AwaitingAcceptance && state.LeaseToken is not null)
                throw new InvalidDataException("A terminal task cannot retain an active claim.");
            var expectedStatus = ResultTaskStatus(state.Result.Status);
            if (state.Acceptance is { } receipt)
            {
                ValidateTaskAcceptanceDecision(new(receipt.Outcome, receipt.VerifierId, receipt.Explanation));
                if (state.Result.Status is not (WorkflowTaskStatus.Succeeded or WorkflowTaskStatus.AwaitingVerification) || receipt.GraphFingerprint != checkpoint.DefinitionFingerprint ||
                    receipt.TaskId != state.TaskId || receipt.AttemptId != state.AttemptId || receipt.Revision != state.Result.Revision ||
                    receipt.ResultDigest != TaskResultDigest(state.Result))
                    throw new InvalidDataException("Task acceptance does not identify the exact established result and attempt.");
                if (receipt.Outcome == TaskAcceptanceOutcome.Accepted && !CanAcceptTaskProposal(checkpoint, state))
                    throw new InvalidDataException("An accepted task proposal does not satisfy the structural acceptance contract.");
                expectedStatus = receipt.Outcome switch { TaskAcceptanceOutcome.Accepted => TaskGraphTaskStatus.Succeeded,
                    TaskAcceptanceOutcome.Rejected => TaskGraphTaskStatus.Failed, TaskAcceptanceOutcome.BudgetExhausted => TaskGraphTaskStatus.BudgetExhausted,
                    _ => TaskGraphTaskStatus.AwaitingAcceptance };
            }
            if (state.Status != expectedStatus) throw new InvalidDataException("Task state disagrees with its result or acceptance receipt.");
        }
        if (leases.Count > checkpoint.MaxConcurrency) throw new InvalidDataException("Task graph exceeds its concurrency limit.");
        return checkpoint with { Graph = definition.Graph, AgentBinding = binding };
    }

    private static void ValidateTaskResult(TaskGraphCheckpoint checkpoint, string taskId, WorkflowTaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _ = ResultTaskStatus(result.Status);
        var validation = TaskResultValidator.Validate(checkpoint.Graph, taskId, result);
        if (!validation.IsValid) throw new WorkflowConflictException("The task result is invalid: " + string.Join(" ", validation.Errors.Select(x => x.Message)));
    }

    private static bool CanAcceptTaskProposal(TaskGraphCheckpoint checkpoint, TaskGraphTaskState state)
    {
        var definition = checkpoint.Graph.Tasks.Single(task => task.TaskId == state.TaskId);
        var required = definition.AcceptanceCriteria.Where(criterion => criterion.Required).Select(criterion => criterion.CriterionId).ToHashSet(StringComparer.Ordinal);
        var result = state.Result!;
        return required.Count > 0 && result.UnresolvedIssues.IsEmpty && RequiredCommandsPassed(checkpoint, state) &&
            definition.ExpectedArtifacts.Where(artifact => artifact.Required).All(requirement => result.Artifacts.Any(artifact => artifact.ArtifactId == requirement.ArtifactId)) &&
            !result.VerificationEvidence.Any(evidence => required.Contains(evidence.CriterionId) && evidence.Outcome != TaskVerificationOutcome.Passed);
    }

    private static TaskGraphTaskStatus ResultTaskStatus(WorkflowTaskStatus status) => status switch
    {
        WorkflowTaskStatus.Succeeded or WorkflowTaskStatus.AwaitingVerification => TaskGraphTaskStatus.AwaitingAcceptance,
        WorkflowTaskStatus.Failed => TaskGraphTaskStatus.Failed,
        WorkflowTaskStatus.Cancelled => TaskGraphTaskStatus.Cancelled,
        WorkflowTaskStatus.BudgetExhausted => TaskGraphTaskStatus.BudgetExhausted,
        _ => throw new WorkflowConflictException("A task executor must establish a terminal outcome.")
    };

    private static void ValidateTaskAcceptanceDecision(TaskAcceptanceDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!Enum.IsDefined(decision.Outcome) || !TaskGraphIdentity(decision.VerifierId) || string.IsNullOrWhiteSpace(decision.Explanation))
            throw new WorkflowConflictException("A verification decision requires a supported outcome, verifier identity, and explanation.");
    }

    private static void RequireTaskGraphCaller(TaskGraphCheckpoint checkpoint, CallerIdentity caller)
    {
        ValidateTaskGraphCaller(caller);
        if (checkpoint.Caller.Id != caller.Id || checkpoint.Caller.IsOwner != caller.IsOwner)
            throw new UnauthorizedAccessException("Only the original graph caller can control or resume this graph.");
    }

    private static void ValidateTaskGraphCaller(CallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        RequireTaskGraphIdentity(caller.Id, "caller.Id");
    }

    private static void RequireTaskGraphIdentity(string? identity, string name)
    {
        if (!TaskGraphIdentity(identity)) throw new ArgumentException("Identity must be nonblank, unpadded, and contain no control characters.", name);
    }
    private static bool TaskGraphIdentity(string? identity) => !string.IsNullOrWhiteSpace(identity) && identity == identity.Trim() && !identity.Any(char.IsControl);
    private static int TaskStateIndex(TaskGraphCheckpoint checkpoint, string taskId)
    {
        for (var index = 0; index < checkpoint.Tasks.Length; index++) if (checkpoint.Tasks[index].TaskId == taskId) return index;
        throw new WorkflowConflictException("The task is not declared in this graph.");
    }
    private static string NullableQuote(string? value) => value is null ? "NULL" : Quote(value);
    private static string SerializeTaskGraph<T>(T value) => JsonSerializer.Serialize(value, TaskGraphJsonOptions);
    private static string TaskResultDigest(WorkflowTaskResult result) => TaskDependencyResolver.ResultDigest(result);
}
