using System.Collections.Concurrent;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore;

public sealed record TaskGraphHostDetail(TaskGraphCheckpoint Checkpoint, TaskGraphProgressSummary Progress,
    IReadOnlyList<WorkflowCheckpoint> Invocations, IReadOnlyList<TaskGraphJournalEntry> Journal,
    WorkflowUsage Usage, WorkflowBudgetSnapshot Budget)
{
    public IReadOnlyList<WorkflowJournalEntry> InvocationJournal { get; init; } = [];
}

/// <summary>Shared local host commands. A launch file cannot grant process authority or switch workspaces.</summary>
public sealed class TaskGraphHostController
{
    private readonly IAgentTaskGraphStore _graphs;
    private readonly IWorkflowStore _workflows;
    private readonly ILlmClient _client;
    private readonly ITaskWorkspaceManager _workspaces;
    private readonly ConcurrentDictionary<string, AgentTaskGraphRunner> _active = new(StringComparer.Ordinal);
    public string WorkspaceRoot { get; }
    public string WorkspaceStoreRoot { get; }

    public TaskGraphHostController(IAgentTaskGraphStore graphStore, IWorkflowStore workflowStore,
        ILlmClient llmClient, string workspaceRoot, string workspaceStoreRoot, ITaskWorkspaceManager? workspaces = null)
    {
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(workflowStore);
        ArgumentNullException.ThrowIfNull(llmClient);
        if (!ReferenceEquals(graphStore, workflowStore))
            throw new ArgumentException("Task graphs and workflows must share the same atomic store.");
        _graphs = graphStore;
        _workflows = workflowStore;
        _client = llmClient;
        _workspaces = workspaces ?? new GitTaskWorkspaceManager();
        WorkspaceRoot = Root(workspaceRoot);
        WorkspaceStoreRoot = Root(workspaceStoreRoot);
        if (Within(WorkspaceRoot, WorkspaceStoreRoot) || Within(WorkspaceStoreRoot, WorkspaceRoot))
            throw new ArgumentException("The managed graph store and source workspace must not overlap.");
    }

    public async Task<IReadOnlyList<TaskGraphCheckpoint>> ListAsync(CallerIdentity caller, CancellationToken ct) =>
        (await _graphs.ListTaskGraphsAsync(ct).ConfigureAwait(false))
            .Where(graph => graph.Caller.Id == caller.Id && graph.Caller.IsOwner == caller.IsOwner &&
                graph.AgentBinding is { } binding && Same(binding.SourceWorkspaceRoot, WorkspaceRoot) &&
                Same(binding.WorkspaceStoreRoot, WorkspaceStoreRoot)).OrderByDescending(graph => graph.UpdatedAt).ToArray();

    public async Task<TaskGraphHostDetail> InspectAsync(string graphId, CallerIdentity caller, CancellationToken ct)
    {
        var graph = await RequireAsync(graphId, caller, ct).ConfigureAwait(false);
        var root = new RunId(graph.AgentBinding!.RootRunId);
        var invocations = (await _workflows.ListAsync(ct).ConfigureAwait(false)).Where(run => run.RootRunId == root.Value &&
                run.Request.Caller.Id == caller.Id && run.Request.Caller.IsOwner == caller.IsOwner)
            .OrderBy(run => run.CreatedAt).ToArray();
        var invocationJournals = await Task.WhenAll(invocations.Select(run => _workflows.ReadJournalAsync(new(run.RunId), ct))).ConfigureAwait(false);
        return new(graph, TaskGraphProgress.Inspect(graph), invocations,
            await _graphs.ReadTaskGraphJournalAsync(graphId, ct).ConfigureAwait(false),
            await _workflows.GetUsageAsync(root, ct).ConfigureAwait(false),
            await _workflows.GetBudgetAsync(root, ct).ConfigureAwait(false)) {
                InvocationJournal = invocationJournals.SelectMany(journal => journal).ToArray()
            };
    }

    public void ValidateLaunch(TaskGraphLaunchDocument document, CallerIdentity caller, bool allowLocalVerification = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(document.Graph);
        ArgumentNullException.ThrowIfNull(document.Options);
        if (document.SchemaVersion != 1) throw new ArgumentException("Unsupported graph launch document schema.");
        if (document.MaxConcurrency is < 1 or > 64) throw new ArgumentException("Graph concurrency must be between 1 and 64.");
        var options = document.Options;
        RequirePaths(options.SourceWorkspaceRoot, options.WorkspaceStoreRoot);
        AgentTaskBindingValidator.Validate(document.Graph, caller, new(AgentTaskIdentity.RootRunId(document.Graph.GraphId).Value,
            options.SourceWorkspaceRoot, new string('0', 40), options.WorkspaceStoreRoot, options.Provider, options.Model,
            options.RootToolPolicy, options.SharedBudget, options.AgentId, options.SystemPrompt, options.MaxRounds, options.MaxOutputTokens) {
                DependencyMode = options.DependencyMode, ArtifactVerificationRules = options.ArtifactVerificationRules,
                VerificationAuthority = options.VerificationAuthority, VerificationProfiles = options.VerificationProfiles,
                CommandVerificationRules = options.CommandVerificationRules
            });
        if (!options.VerificationProfiles.IsEmpty && !allowLocalVerification)
            throw new UnauthorizedAccessException("This graph requires explicit local process verification permission from the host user.");
    }

    public Task<TaskGraphCheckpoint> StartAsync(TaskGraphLaunchDocument document, CallerIdentity caller,
        bool allowLocalVerification, CancellationToken ct)
    {
        ValidateLaunch(document, caller, allowLocalVerification);
        var runner = Runner(document.Options.VerificationProfiles, allowLocalVerification);
        return ExecuteAsync(document.Graph.GraphId, runner,
            () => runner.StartAsync(document.Graph, caller, document.MaxConcurrency, document.Options, ct));
    }

    public async Task<TaskGraphCheckpoint> ResumeAsync(string graphId, CallerIdentity caller,
        bool allowLocalVerification, CancellationToken ct)
    {
        var graph = await RequireAsync(graphId, caller, ct).ConfigureAwait(false);
        var runner = Runner(graph.AgentBinding!.VerificationProfiles, allowLocalVerification);
        return await ExecuteAsync(graphId, runner, () => runner.ResumeAsync(graphId, caller, ct)).ConfigureAwait(false);
    }

    public async Task<TaskGraphCheckpoint> RecoverTaskAsync(string graphId, string taskId, CallerIdentity caller,
        bool allowLocalVerification, CancellationToken ct)
    {
        var graph = await RequireAsync(graphId, caller, ct).ConfigureAwait(false);
        RequireTask(graph, taskId);
        var runner = Runner(graph.AgentBinding!.VerificationProfiles, allowLocalVerification);
        return await ExecuteAsync(graphId, runner, () => runner.RecoverTaskAsync(graphId, taskId, caller, ct)).ConfigureAwait(false);
    }

    public async Task PauseAsync(string graphId, CallerIdentity caller, CancellationToken ct)
    {
        await RequireAsync(graphId, caller, ct).ConfigureAwait(false);
        if (_active.TryGetValue(graphId, out var runner)) await runner.PauseAsync(graphId, caller, ct).ConfigureAwait(false);
        else await _graphs.ControlTaskGraphAsync(graphId, WorkflowControl.Pause, caller, ct).ConfigureAwait(false);
    }

    public async Task CancelAsync(string graphId, CallerIdentity caller, CancellationToken ct)
    {
        await RequireAsync(graphId, caller, ct).ConfigureAwait(false);
        if (_active.TryGetValue(graphId, out var runner)) await runner.CancelAsync(graphId, caller, ct).ConfigureAwait(false);
        else await _graphs.ControlTaskGraphAsync(graphId, WorkflowControl.Cancel, caller, ct).ConfigureAwait(false);
    }

    public async Task<TaskGraphCheckpoint> ReconcileTaskAsync(string graphId, string taskId,
        WorkflowTaskResult result, string note, CallerIdentity caller, CancellationToken ct)
    {
        var graph = await RequireAsync(graphId, caller, ct).ConfigureAwait(false);
        RequireTask(graph, taskId);
        return await _graphs.ReconcileTaskAsync(graphId, taskId, result, note, caller, ct).ConfigureAwait(false);
    }

    public async Task<TaskGraphCheckpoint> ReconcileVerificationAsync(string graphId, string taskId, string invocationId,
        TaskVerificationCompletion completion, string note, CallerIdentity caller, CancellationToken ct)
    {
        var graph = await RequireAsync(graphId, caller, ct).ConfigureAwait(false);
        RequireTask(graph, taskId);
        return await (_graphs as ITaskVerificationStore ?? throw new NotSupportedException("The store has no command verification ledger."))
            .ReconcileTaskVerificationAsync(graphId, taskId, invocationId, completion, note, caller, ct).ConfigureAwait(false);
    }

    public async Task ReconcileChildToolAsync(string graphId, string taskId, string executionId,
        ToolResult result, string note, CallerIdentity caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(result.Content);
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        var graph = await RequireAsync(graphId, caller, ct).ConfigureAwait(false);
        var task = RequireTask(graph, taskId);
        if (task.LeaseToken is not null || task.AttemptId is null)
            throw new WorkflowConflictException("Only an idle task with an established attempt can have tool effects reconciled.");
        var runId = AgentTaskIdentity.ChildRunId(graph.DefinitionFingerprint, taskId, task.AttemptId);
        var child = await _workflows.GetAsync(runId, ct).ConfigureAwait(false)
            ?? throw new WorkflowConflictException("The selected task has no saved child invocation.");
        if (child.LeaseOwner is not null || child.RootRunId != graph.AgentBinding!.RootRunId ||
            child.Request.Caller.Id != caller.Id || child.Request.Caller.IsOwner != caller.IsOwner)
            throw new WorkflowConflictException("The child invocation is active or does not belong to this graph caller.");
        var tools = (child.ToolExecutions ?? []).ToArray();
        var index = Array.FindIndex(tools, tool => tool.ExecutionId == executionId);
        if (index < 0 || tools[index].State != WorkflowToolState.Started || tools[index].Result is not null)
            throw new WorkflowConflictException("Select a tool execution with an unknown outcome.");
        tools[index] = tools[index] with { Result = result, ReconciliationNote = note, State = WorkflowToolState.Completed };
        await _workflows.SaveAsync(child with { ToolExecutions = tools,
            Status = tools.Any(tool => tool.Result is null && tool.State == WorkflowToolState.Started) ? WorkflowStatus.NeedsReconciliation
                : child.Control == WorkflowControl.Cancel ? WorkflowStatus.Cancelled : WorkflowStatus.Interrupted,
            UpdatedAt = DateTimeOffset.UtcNow }, child.Revision, "tool_reconciled", ct).ConfigureAwait(false);
    }

    /// <summary>Provider configuration hint; execution still validates live controls, claims and budgets.</summary>
    public async Task<string?> GetRequiredProviderAsync(string graphId, CallerIdentity caller, CancellationToken ct,
        string? recoverTaskId = null, bool allowLocalVerification = false)
    {
        var graph = await RequireAsync(graphId, caller, ct).ConfigureAwait(false);
        if (graph.Control == WorkflowControl.Cancel || graph.Tasks.Any(task => task.LeaseToken is not null)) return null;
        var budget = await _workflows.GetBudgetAsync(new RunId(graph.AgentBinding!.RootRunId), ct).ConfigureAwait(false);
        if (AgentTaskDeadline.Exhausted(budget, admittingModel: true) ||
            (await _workflows.GetUsageAsync(new RunId(graph.AgentBinding.RootRunId), ct).ConfigureAwait(false)).ModelCalls >= budget.Limits.MaxModelCalls)
            return null;
        string? recoveredTaskId = null;
        var recoveredTaskCanAccept = false;
        if (recoverTaskId is not null)
        {
            var task = RequireTask(graph, recoverTaskId);
            if (task.Status != TaskGraphTaskStatus.NeedsReconciliation || task.AttemptId is null || task.Result is not null) return null;
            var child = await _workflows.GetAsync(AgentTaskIdentity.ChildRunId(graph.DefinitionFingerprint, task.TaskId, task.AttemptId), ct).ConfigureAwait(false);
            // Reclaim fails before scheduling any other task if this child's effects are still active or unknown.
            if (child is not null && (child.LeaseOwner is not null || WorkflowIdentity.HasUnknownEffects(child))) return null;
            if (child is null || child.Result is null && child.Control == WorkflowControl.None &&
                child.Status is not (WorkflowStatus.Failed or WorkflowStatus.BudgetExhausted) &&
                !(child.HumanRequests?.Any(request => request.Response is null) ?? false))
                return graph.AgentBinding.Provider;
            recoveredTaskId = task.TaskId;
            recoveredTaskCanAccept = child.Result?.Status == AgentRunStatus.Succeeded;
            // A saved child result needs no provider itself. Recovery then resumes the graph,
            // which can admit independent work or dependants of a newly accepted saved result.
        }
        var states = graph.Tasks.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
        var definitions = graph.Graph.Tasks.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
        var possible = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var id in TaskGraphValidator.Validate(graph.Graph).TopologicalTaskIds)
        {
            var state = states[id];
            var recovering = id == recoveredTaskId;
            var canProgress = (recovering ? recoveredTaskCanAccept :
                state.Status is (TaskGraphTaskStatus.Succeeded or TaskGraphTaskStatus.Pending or TaskGraphTaskStatus.AwaitingAcceptance)) &&
                !state.Verifications.Any(command => command.State != TaskVerificationInvocationState.Completed || command.Completion?.Outcome != TaskCommandOutcome.Passed);
            if (recovering || state.Status == TaskGraphTaskStatus.AwaitingAcceptance)
            {
                var criteria = definitions[id].AcceptanceCriteria.Where(criterion => criterion.Required).ToArray();
                canProgress &= (state.Result is null || state.Result.UnresolvedIssues.IsEmpty &&
                    state.Result.VerificationEvidence.All(evidence => evidence.Outcome == TaskVerificationOutcome.Passed)) &&
                    criteria.Length > 0 && criteria.All(criterion =>
                    graph.AgentBinding.ArtifactVerificationRules.Any(rule => rule.TaskId == id && rule.CriterionId == criterion.CriterionId) ||
                    graph.AgentBinding.CommandVerificationRules.Any(rule => rule.TaskId == id && rule.CriterionId == criterion.CriterionId &&
                        (allowLocalVerification || state.Verifications.Any(command => command.ProfileId == rule.ProfileId &&
                            command.State == TaskVerificationInvocationState.Completed && command.Completion?.Outcome == TaskCommandOutcome.Passed))));
            }
            possible[id] = canProgress && definitions[id].Dependencies.All(dependency => possible[dependency]);
        }
        return graph.Tasks.Any(task => task.Status == TaskGraphTaskStatus.Pending && possible[task.TaskId])
            ? graph.AgentBinding.Provider : null;
    }

    private AgentTaskGraphRunner Runner(IEnumerable<TaskVerificationProfile> profiles, bool allowLocalVerification) =>
        new(_graphs, _workflows, _workspaces, _client, allowLocalVerification
            ? new TrustedLocalTaskVerificationWorker(profiles, TaskVerificationAuthority.TrustedLocalProcess) : null);

    private async Task<TaskGraphCheckpoint> ExecuteAsync(string graphId, AgentTaskGraphRunner runner,
        Func<Task<TaskGraphCheckpoint>> execute)
    {
        if (!_active.TryAdd(graphId, runner)) throw new WorkflowConflictException("This host is already running the graph.");
        try { return await execute().ConfigureAwait(false); }
        finally { _active.TryRemove(graphId, out _); }
    }

    private async Task<TaskGraphCheckpoint> RequireAsync(string graphId, CallerIdentity caller, CancellationToken ct)
    {
        var graph = await _graphs.GetTaskGraphAsync(graphId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException("The task graph was not found in this workspace.", nameof(graphId));
        if (graph.Caller.Id != caller.Id || graph.Caller.IsOwner != caller.IsOwner)
            throw new UnauthorizedAccessException("Only the graph's original caller and ownership level can control it.");
        if (graph.AgentBinding is not { } binding) throw new WorkflowConflictException("The graph has no agent execution binding.");
        RequirePaths(binding.SourceWorkspaceRoot, binding.WorkspaceStoreRoot);
        return graph;
    }

    private void RequirePaths(string source, string store)
    {
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(store) || !Same(source, WorkspaceRoot) || !Same(store, WorkspaceStoreRoot))
            throw new UnauthorizedAccessException("Graph source and managed workspace paths must match this host's configured workspace.");
    }
    private static TaskGraphTaskState RequireTask(TaskGraphCheckpoint graph, string taskId) =>
        graph.Tasks.SingleOrDefault(task => task.TaskId == taskId) ?? throw new ArgumentException("The graph task was not found.", nameof(taskId));
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string Root(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Same(string left, string right) => string.Equals(Root(left), Root(right), Comparison);
    private static bool Within(string root, string path) => Same(root, path) || Root(path).StartsWith(
        Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Comparison);
}
