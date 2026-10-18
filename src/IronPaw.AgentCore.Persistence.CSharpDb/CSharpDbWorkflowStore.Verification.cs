using System.Collections.Immutable;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence.CSharpDb;

public sealed partial class CSharpDbWorkflowStore : ITaskVerificationStore
{
    public async Task<TaskVerificationAdmission> BeginTaskVerificationAsync(TaskGraphClaim claim, string profileId, CancellationToken ct)
    {
        TaskVerificationAdmission? admission = null;
        await MutateClaimAsync(claim, async (graph, index, state) =>
        {
            var profile = RequireVerificationProfile(graph, state, profileId);
            var id = TaskVerificationIdentity.InvocationId(claim, state.Result!, profile);
            var existing = state.Verifications.SingleOrDefault(item => item.InvocationId == id);
            if (existing is not null)
            {
                if (existing.State != TaskVerificationInvocationState.Completed)
                    throw new WorkflowReconciliationRequiredException("This verification invocation has active or uncertain effects and cannot be repeated.");
                admission = new(false, existing);
                return graph;
            }
            if (state.Verifications.Any(item => item.State != TaskVerificationInvocationState.Completed))
                throw new WorkflowReconciliationRequiredException("Establish the active or uncertain verification outcome before admitting another command.");
            if (graph.Control == WorkflowControl.Cancel) throw new WorkflowCancelledException("The task graph was permanently cancelled.");
            if (graph.Control != WorkflowControl.None) throw new WorkflowSuspendedException(WorkflowStatus.Paused, "The task graph is paused.");
            await RequireGraphBudgetAvailableAsync(graph, ct).ConfigureAwait(false);
            var invocation = new TaskVerificationInvocation(id, profile.ProfileId, TaskVerificationIdentity.ProfileFingerprint(profile),
                TaskInputIdentity.ResultDigest(state.Result!), state.Result!.Revision, TaskVerificationInvocationState.Started, _timeProvider.GetUtcNow());
            admission = new(true, invocation);
            return await SaveTaskGraphAsync(graph with { Tasks = graph.Tasks.SetItem(index, state with { Verifications = state.Verifications.Add(invocation) }) },
                "verification_started", state.TaskId, state.AttemptId, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return admission!;
    }

    public Task<TaskGraphCheckpoint> CompleteTaskVerificationAsync(TaskGraphClaim claim, string invocationId,
        TaskVerificationCompletion completion, CancellationToken ct) =>
        MutateClaimAsync(claim, async (graph, index, state) =>
        {
            var invocationIndex = VerificationIndex(state, invocationId);
            var invocation = state.Verifications[invocationIndex];
            var profile = RequireVerificationProfile(graph, state, invocation.ProfileId);
            TaskVerificationValidator.ValidateCompletion(profile, completion);
            if (invocation.State == TaskVerificationInvocationState.Completed)
            {
                if (SerializeTaskGraph(invocation.Completion) == SerializeTaskGraph(completion)) return graph;
                throw new WorkflowConflictException("An established verification receipt cannot be replaced.");
            }
            if (invocation.State != TaskVerificationInvocationState.Started)
                throw new WorkflowReconciliationRequiredException("An uncertain verification requires explicit reconciliation.");
            var next = invocation with { State = completion.Outcome == TaskCommandOutcome.NeedsReconciliation
                    ? TaskVerificationInvocationState.NeedsReconciliation : TaskVerificationInvocationState.Completed,
                Completion = completion, CompletedAt = completion.Outcome == TaskCommandOutcome.NeedsReconciliation ? null : Later(_timeProvider.GetUtcNow(), invocation.StartedAt) };
            return await SaveTaskGraphAsync(graph with { Tasks = graph.Tasks.SetItem(index, state with { Verifications = state.Verifications.SetItem(invocationIndex, next) }) },
                "verification_receipt", state.TaskId, state.AttemptId, ct).ConfigureAwait(false);
        }, ct);

    public async Task<TaskGraphCheckpoint> ReconcileTaskVerificationAsync(string graphId, string taskId, string invocationId,
        TaskVerificationCompletion completion, string note, CallerIdentity caller, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        TaskGraphCheckpoint? saved = null;
        await MutateTaskGraphAsync(graphId, async graph =>
        {
            RequireTaskGraphCaller(graph, caller);
            var index = TaskStateIndex(graph, taskId);
            var state = graph.Tasks[index];
            var invocationIndex = VerificationIndex(state, invocationId);
            var invocation = state.Verifications[invocationIndex];
            var profile = RequireVerificationProfile(graph, state, invocation.ProfileId);
            TaskVerificationValidator.ValidateCompletion(profile, completion);
            if (invocation.State != TaskVerificationInvocationState.NeedsReconciliation || state.LeaseToken is not null ||
                completion.Outcome == TaskCommandOutcome.NeedsReconciliation)
                throw new WorkflowConflictException("Only an unleased uncertain command can be explicitly reconciled to an established outcome.");
            var next = invocation with { State = TaskVerificationInvocationState.Completed, Completion = completion,
                CompletedAt = Later(_timeProvider.GetUtcNow(), invocation.StartedAt), ReconciliationNote = note };
            return saved = await SaveTaskGraphAsync(graph with { Tasks = graph.Tasks.SetItem(index, state with { Verifications = state.Verifications.SetItem(invocationIndex, next), Error = null }) },
                "verification_reconciled", taskId, state.AttemptId, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return saved!;
    }

    private static TaskVerificationProfile RequireVerificationProfile(TaskGraphCheckpoint graph, TaskGraphTaskState state, string profileId)
    {
        if (graph.SchemaVersion != 4 || graph.AgentBinding is not { VerificationAuthority: TaskVerificationAuthority.TrustedLocalProcess } binding ||
            state.Status != TaskGraphTaskStatus.AwaitingAcceptance || state.Result is null ||
            !binding.CommandVerificationRules.Any(rule => rule.TaskId == state.TaskId && rule.ProfileId == profileId))
            throw new WorkflowConflictException("Verification requires an exact saved candidate and a frozen profile mapped to this task.");
        return binding.VerificationProfiles.SingleOrDefault(profile => profile.ProfileId == profileId)
            ?? throw new WorkflowConflictException("The verification profile is not declared.");
    }

    private static int VerificationIndex(TaskGraphTaskState state, string invocationId)
    {
        for (var index = 0; index < state.Verifications.Length; index++)
            if (state.Verifications[index].InvocationId == invocationId) return index;
        throw new WorkflowConflictException("The verification invocation does not exist.");
    }

    private async Task RequireGraphBudgetAvailableAsync(TaskGraphCheckpoint graph, CancellationToken ct)
    {
        if (graph.AgentBinding is not { } binding) return;
        var workflows = await ReadAllAsync(ct).ConfigureAwait(false);
        var root = workflows.Single(item => item.RunId == binding.RootRunId);
        var clock = await ReadClockAsync(binding.RootRunId, ct).ConfigureAwait(false) ?? new ExecutionClock(0, null);
        var budget = Snapshot(root, workflows, EffectiveElapsed(clock, _timeProvider.GetUtcNow()));
        if (budget.RemainingExecutionTime is { } remaining && remaining <= TimeSpan.Zero ||
            budget.Limits.MaxTotalTokens is { } tokens && budget.ChargedTokens > tokens)
            throw new WorkflowBudgetExceededException("The shared graph budget is exhausted.");
    }

    private static ImmutableArray<TaskVerificationInvocation> InterruptVerifications(TaskGraphTaskState state) =>
        state.Verifications.Select(invocation => invocation.State == TaskVerificationInvocationState.Started
            ? invocation with { State = TaskVerificationInvocationState.NeedsReconciliation } : invocation).ToImmutableArray();

    private static void ValidateTaskVerifications(TaskGraphCheckpoint graph, TaskGraphTaskState state)
    {
        if (state.Verifications.IsDefault) throw new InvalidDataException("Task verification receipts must be initialized.");
        if (state.Verifications.IsEmpty) return;
        if (graph.SchemaVersion != 4 || graph.AgentBinding is not { } binding || state.Result?.Status is not (WorkflowTaskStatus.Succeeded or WorkflowTaskStatus.AwaitingVerification) ||
            state.Status is TaskGraphTaskStatus.Pending or TaskGraphTaskStatus.Running or TaskGraphTaskStatus.NeedsReconciliation)
            throw new InvalidDataException("Verification receipts require a saved candidate in a supported graph.");
        var claim = new TaskGraphClaim(graph.Graph.GraphId, graph.DefinitionFingerprint,
            graph.Graph.Tasks.Single(task => task.TaskId == state.TaskId), state.AttemptId!, "validation", state.Result, state.Inputs);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var profiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var invocation in state.Verifications)
        {
            if (invocation is null || !Enum.IsDefined(invocation.State) || !ids.Add(invocation.InvocationId) || !profiles.Add(invocation.ProfileId))
                throw new InvalidDataException("Verification receipt identities and states must be unique and supported.");
            var profile = binding.VerificationProfiles.SingleOrDefault(profile => profile.ProfileId == invocation.ProfileId);
            if (profile is null || !binding.CommandVerificationRules.Any(rule => rule.TaskId == state.TaskId && rule.ProfileId == profile.ProfileId) ||
                invocation.ProfileFingerprint != TaskVerificationIdentity.ProfileFingerprint(profile) ||
                invocation.ResultDigest != TaskInputIdentity.ResultDigest(state.Result) || invocation.Revision != state.Result.Revision ||
                invocation.InvocationId != TaskVerificationIdentity.InvocationId(claim, state.Result, profile) ||
                invocation.StartedAt == default || invocation.CompletedAt < invocation.StartedAt ||
                invocation.ReconciliationNote is not null && string.IsNullOrWhiteSpace(invocation.ReconciliationNote))
                throw new InvalidDataException("A verification receipt does not identify the exact frozen candidate and command.");
            if (invocation.Completion is { } completion) TaskVerificationValidator.ValidateCompletion(profile, completion);
            if (invocation.State == TaskVerificationInvocationState.Started && (state.LeaseToken is null || invocation.Completion is not null || invocation.CompletedAt is not null || invocation.ReconciliationNote is not null) ||
                invocation.State == TaskVerificationInvocationState.Completed && (invocation.Completion is null || invocation.CompletedAt is null || invocation.Completion.Outcome == TaskCommandOutcome.NeedsReconciliation) ||
                invocation.State == TaskVerificationInvocationState.NeedsReconciliation && (invocation.CompletedAt is not null || invocation.ReconciliationNote is not null ||
                    invocation.Completion is { Outcome: not TaskCommandOutcome.NeedsReconciliation }))
                throw new InvalidDataException("Verification state and completion evidence disagree.");
        }
        if (state.Verifications.Count(invocation => invocation.State != TaskVerificationInvocationState.Completed) > 1 ||
            state.Verifications.Any(invocation => invocation.State != TaskVerificationInvocationState.Completed) && state.Status != TaskGraphTaskStatus.AwaitingAcceptance)
            throw new InvalidDataException("Uncertain verification cannot establish a terminal task outcome.");
    }

    private static bool RequiredCommandsPassed(TaskGraphCheckpoint graph, TaskGraphTaskState state)
    {
        if (graph.AgentBinding is not { } binding) return true;
        var required = graph.Graph.Tasks.Single(task => task.TaskId == state.TaskId).AcceptanceCriteria.Where(criterion => criterion.Required)
            .Select(criterion => criterion.CriterionId).ToHashSet(StringComparer.Ordinal);
        return binding.CommandVerificationRules.Where(rule => rule.TaskId == state.TaskId && required.Contains(rule.CriterionId))
            .All(rule => state.Verifications.Any(invocation => invocation.ProfileId == rule.ProfileId &&
                invocation.State == TaskVerificationInvocationState.Completed && invocation.Completion?.Outcome == TaskCommandOutcome.Passed));
    }
}
