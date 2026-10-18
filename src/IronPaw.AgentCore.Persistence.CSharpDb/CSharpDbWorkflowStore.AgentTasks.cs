using System.Collections.Immutable;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence.CSharpDb;

public sealed partial class CSharpDbWorkflowStore
{
    public async Task<TaskGraphCheckpoint> AcceptAgentTaskGraphAsync(WorkflowTaskGraph graph, CallerIdentity caller,
        int maxConcurrency, TaskGraphAgentBinding binding, CancellationToken ct)
    {
        ValidateTaskGraphCaller(caller);
        if (maxConcurrency is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        var validation = TaskGraphValidator.Validate(graph);
        if (!validation.IsValid) throw new WorkflowConflictException("The task graph is invalid.");
        graph = validation.Graph!;
        binding = AgentTaskBindingValidator.Validate(graph, caller, binding);
        var now = _timeProvider.GetUtcNow();
        var proposed = new TaskGraphCheckpoint(graph, validation.DefinitionFingerprint!, caller, maxConcurrency,
            graph.Tasks.Select(task => new TaskGraphTaskState(task.TaskId)).ToImmutableArray(), now, now,
            SchemaVersion: binding.VerificationAuthority != TaskVerificationAuthority.None ? 4 : binding.DependencyMode == TaskDependencyMode.AcceptedChanges ? 3 : 2, AgentBinding: binding);
        TaskGraphCheckpoint? accepted = null;
        await InGateAsync(async () =>
        {
            await TransactionAsync(async () =>
            {
                var existing = await ReadTaskGraphAsync(graph.GraphId, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    RequireTaskGraphCaller(existing, caller);
                    if (existing.DefinitionFingerprint != proposed.DefinitionFingerprint || existing.MaxConcurrency != maxConcurrency ||
                        SerializeTaskGraph(existing.AgentBinding) != SerializeTaskGraph(binding))
                        throw new WorkflowConflictException("An accepted graph cannot change its agent execution binding.");
                    accepted = existing;
                    return;
                }
                var workflows = await ReadAllAsync(ct).ConfigureAwait(false);
                if (workflows.Any(run => run.RunId == binding.RootRunId || run.Request.SessionKey == AgentTaskIdentity.RootSession(graph.GraphId)))
                    throw new WorkflowConflictException("The graph root identity is already owned by another workflow.");
                var request = AgentTaskBindingValidator.CreateRootRequest(graph, caller, binding);
                var anchor = new WorkflowCheckpoint(binding.RootRunId, binding.RootRunId, null, "task-graph-root", request,
                    now, now, Budget: binding.SharedBudget, Usage: new());
                Validate(anchor);
                var rootId = await NextIdAsync("workflows", ct).ConfigureAwait(false);
                await ExecuteAsync($"INSERT INTO workflows VALUES ({rootId}, {Quote(anchor.RunId)}, {Quote(Serialize(anchor))})", ct).ConfigureAwait(false);
                await AppendJournalAsync(anchor, "task_graph_accepted", ct).ConfigureAwait(false);
                var graphId = await NextIdAsync("task_graphs", ct).ConfigureAwait(false);
                await ExecuteAsync($"INSERT INTO task_graphs VALUES ({graphId}, {Quote(graph.GraphId)}, {Quote(SerializeTaskGraph(proposed))})", ct).ConfigureAwait(false);
                await AppendTaskGraphJournalAsync(proposed, "accepted", null, null, ct).ConfigureAwait(false);
                await UpdateClockAsync(binding.RootRunId, workflows.Append(anchor).ToArray(), ct).ConfigureAwait(false);
                accepted = proposed;
            }, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return accepted!;
    }

    public async Task<TaskGraphClaim> ReclaimAgentTaskAsync(string graphId, string taskId, CallerIdentity caller, CancellationToken ct)
    {
        TaskGraphClaim? claim = null;
        await MutateTaskGraphAsync(graphId, async graph =>
        {
            RequireTaskGraphCaller(graph, caller);
            var binding = graph.AgentBinding ?? throw new WorkflowConflictException("This graph has no agent execution binding.");
            if (graph.Control == WorkflowControl.Cancel) throw new WorkflowCancelledException("The task graph was permanently cancelled.");
            if (graph.Control != WorkflowControl.None) throw new WorkflowSuspendedException(WorkflowStatus.Paused, "The task graph is paused.");
            if (graph.Tasks.Count(task => task.LeaseToken is not null) >= graph.MaxConcurrency)
                throw new WorkflowConflictException("The graph concurrency limit is occupied.");
            var index = TaskStateIndex(graph, taskId);
            var state = graph.Tasks[index];
            if (state.Status != TaskGraphTaskStatus.NeedsReconciliation || state.LeaseToken is not null || state.Result is not null)
                throw new WorkflowConflictException("Only an interrupted agent task can be explicitly reclaimed.");
            var childId = AgentTaskIdentity.ChildRunId(graph.DefinitionFingerprint, taskId, state.AttemptId!);
            var child = await ReadOneAsync(childId.Value, ct).ConfigureAwait(false);
            if (child is not null && (child.RootRunId != binding.RootRunId || child.ParentRunId != binding.RootRunId ||
                child.LeaseOwner is not null || WorkflowIdentity.HasUnknownEffects(child)))
                throw new WorkflowReconciliationRequiredException("The child has active or uncertain effects. Establish their outcomes before reclaiming it.");
            var token = Guid.NewGuid().ToString("n");
            var next = state with { Status = TaskGraphTaskStatus.Running, LeaseToken = token, Error = null };
            claim = new(graphId, graph.DefinitionFingerprint, graph.Graph.Tasks[index], state.AttemptId!, token, Inputs: state.Inputs);
            return await SaveTaskGraphAsync(graph with { Tasks = graph.Tasks.SetItem(index, next) }, "agent_task_reclaimed", taskId, state.AttemptId, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return claim!;
    }

    private async Task ValidateAgentTaskAnchorAsync(TaskGraphCheckpoint graph, CancellationToken ct)
    {
        if (graph.AgentBinding is not { } binding) return;
        var anchor = await ReadOneAsync(binding.RootRunId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The bound graph root workflow is missing.");
        var expected = AgentTaskBindingValidator.CreateRootRequest(graph.Graph, graph.Caller, binding);
        if (anchor.RunId != anchor.RootRunId || anchor.ParentRunId is not null || anchor.TaskId != "task-graph-root" ||
            anchor.LeaseOwner is not null || anchor.ModelCalls?.Count > 0 || anchor.ToolExecutions?.Count > 0 ||
            anchor.InitialTranscript is not null || anchor.Transcript is not null || anchor.HumanRequests?.Count > 0 ||
            anchor.Result is not null || anchor.Suspension is not null ||
            anchor.Status != (graph.Control == WorkflowControl.Cancel ? WorkflowStatus.Cancelled : graph.Control == WorkflowControl.Pause ? WorkflowStatus.Paused : WorkflowStatus.Queued) ||
            Serialize(anchor.Request) != Serialize(expected) || anchor.Budget != binding.SharedBudget || anchor.Control != graph.Control ||
            anchor.Usage is { } usage && usage != new WorkflowUsage())
            throw new InvalidDataException("The bound graph root workflow does not match its immutable execution binding.");
    }

    private async Task SynchronizeAgentTaskAnchorAsync(TaskGraphCheckpoint graph, CancellationToken ct)
    {
        var binding = graph.AgentBinding!;
        var anchor = await ReadOneAsync(binding.RootRunId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The bound graph root workflow is missing.");
        if (anchor.Control == graph.Control) return;
        var next = anchor with { Control = graph.Control,
            Status = graph.Control == WorkflowControl.Cancel ? WorkflowStatus.Cancelled : graph.Control == WorkflowControl.Pause ? WorkflowStatus.Paused : WorkflowStatus.Queued,
            Revision = checked(anchor.Revision + 1), UpdatedAt = _timeProvider.GetUtcNow() };
        await WriteCheckpointAsync(next, ct).ConfigureAwait(false);
        await AppendJournalAsync(next, "task_graph_control", ct).ConfigureAwait(false);
    }

    private async Task ValidateAgentTaskWorkflowWriteAsync(IReadOnlyList<WorkflowCheckpoint> workflows,
        WorkflowCheckpoint? previous, WorkflowCheckpoint proposed, CancellationToken ct)
    {
        var graph = (await ReadTaskGraphsAsync(ct).ConfigureAwait(false)).SingleOrDefault(item => item.AgentBinding?.RootRunId == proposed.RootRunId);
        if (graph?.AgentBinding is not { } binding) return;
        if (proposed.RunId == binding.RootRunId)
            throw new WorkflowConflictException("The graph root is a non-executable ledger anchor. Use graph controls to change it.");
        var oldUsage = previous?.Usage ?? new WorkflowUsage();
        var usage = proposed.Usage ?? new WorkflowUsage();
        var admitting = previous is null || previous.LeaseOwner is null && proposed.LeaseOwner is not null ||
            usage.ModelCalls > oldUsage.ModelCalls || usage.ToolCalls > oldUsage.ToolCalls ||
            (proposed.ModelCalls ?? []).SelectMany(call => call.Attempts ?? []).Count() > (previous?.ModelCalls ?? []).SelectMany(call => call.Attempts ?? []).Count() ||
            (proposed.ToolExecutions ?? []).Any(tool => tool.State is WorkflowToolState.Started or WorkflowToolState.WaitingForChild &&
                !(previous?.ToolExecutions?.Any(old => old.ExecutionId == tool.ExecutionId && old.State == tool.State) ?? false));
        if (!admitting) return;
        if (graph.Control == WorkflowControl.Cancel) throw new WorkflowCancelledException("The task graph was permanently cancelled.");
        if (graph.Control == WorkflowControl.Pause) throw new WorkflowSuspendedException(WorkflowStatus.Paused, "The task graph is paused.");
        if (proposed.ParentRunId != binding.RootRunId)
            throw new WorkflowConflictException("Bound task execution does not permit nested agent invocations.");
        var taskState = graph.Tasks.SingleOrDefault(state => state.AttemptId is not null &&
            AgentTaskIdentity.ChildRunId(graph.DefinitionFingerprint, state.TaskId, state.AttemptId).Value == proposed.RunId);
        if (taskState is null || taskState.Status != TaskGraphTaskStatus.Running || taskState.LeaseToken is null)
            throw new WorkflowConflictException("The child requires a live running graph task claim.");
        var claim = new TaskGraphClaim(graph.Graph.GraphId, graph.DefinitionFingerprint,
            graph.Graph.Tasks[TaskStateIndex(graph, taskState.TaskId)], taskState.AttemptId!, taskState.LeaseToken, Inputs: taskState.Inputs);
        var expected = AgentTaskBindingValidator.CreateChildRequest(graph.Graph, graph.Caller, binding, claim.Task, claim.AttemptId, taskState.Inputs);
        var request = proposed.Request;
        // Runtime initialization may enrich only memories and caller-derived ownership.
        if (previous is not null)
            request = request with { ActiveMemories = expected.ActiveMemories,
                ToolPolicy = request.ToolPolicy with { SenderIsOwner = expected.ToolPolicy.SenderIsOwner } };
        if (Serialize(request) != Serialize(expected) || proposed.Budget != expected.Budget)
            throw new WorkflowConflictException("The child request does not match its accepted graph execution binding.");
    }
}
