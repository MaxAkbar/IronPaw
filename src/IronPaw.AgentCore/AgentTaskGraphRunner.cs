using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

/// <summary>
/// Host entry point for commit-isolated file tasks. Definitions, authority and shared limits
/// are frozen together before any task runs. Recovery reuses the original task invocation.
/// </summary>
public sealed class AgentTaskGraphRunner
{
    private readonly IAgentTaskGraphStore _store;
    private readonly ITaskWorkspaceManager _workspaces;
    private readonly TaskGraphScheduler _scheduler;
    private readonly ITaskVerificationWorker? _verificationWorker;

    public AgentTaskGraphRunner(IAgentTaskGraphStore graphStore, IWorkflowStore workflowStore,
        ITaskWorkspaceManager workspaces, ILlmClient llmClient, ITaskVerificationWorker? verificationWorker = null)
    {
        _store = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        ArgumentNullException.ThrowIfNull(workflowStore);
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        ArgumentNullException.ThrowIfNull(llmClient);
        _verificationWorker = verificationWorker;
        // Both interfaces must address the same atomic ledger and control boundary.
        if (!ReferenceEquals(graphStore, workflowStore))
            throw new ArgumentException("Graph and workflow storage must share one atomic store instance.", nameof(workflowStore));
        _scheduler = new TaskGraphScheduler(graphStore,
            new IsolatedAgentTaskExecutor(graphStore, workflowStore, workspaces, llmClient),
            new ArtifactIntegrityTaskVerifier(graphStore, workflowStore, workspaces),
            new TaskCommandVerificationRunner(graphStore, workflowStore, workspaces, verificationWorker));
    }

    public async Task<TaskGraphCheckpoint> StartAsync(WorkflowTaskGraph graph, CallerIdentity caller,
        int maxConcurrency, TaskGraphAgentOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        // Validate capabilities and paths before launching even read-only Git commands.
        var binding = AgentTaskBindingValidator.Validate(graph, caller, new TaskGraphAgentBinding(
            AgentTaskIdentity.RootRunId(graph.GraphId).Value, options.SourceWorkspaceRoot,
            new string('0', 40), options.WorkspaceStoreRoot, options.Provider, options.Model,
            options.RootToolPolicy, options.SharedBudget, options.AgentId, options.SystemPrompt,
            options.MaxRounds, options.MaxOutputTokens) { ArtifactVerificationRules = options.ArtifactVerificationRules,
                DependencyMode = options.DependencyMode, VerificationAuthority = options.VerificationAuthority,
                VerificationProfiles = options.VerificationProfiles, CommandVerificationRules = options.CommandVerificationRules });
        if (maxConcurrency is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        if (!binding.VerificationProfiles.IsEmpty)
        {
            if (_verificationWorker is null || _store is not ITaskVerificationStore)
                throw new InvalidOperationException("Command verification requires an explicitly configured trusted worker and durable verification store.");
            foreach (var profile in binding.VerificationProfiles) _verificationWorker.ValidateProfile(profile, caller);
        }
        var commit = await _workspaces.ResolveCommitAsync(binding.SourceWorkspaceRoot, options.BaseRevision, ct).ConfigureAwait(false);
        binding = AgentTaskBindingValidator.Validate(graph, caller, binding with { BaseCommit = commit });
        var checkpoint = await _store.AcceptAgentTaskGraphAsync(graph, caller, maxConcurrency, binding, ct).ConfigureAwait(false);
        return await _scheduler.ResumeAsync(checkpoint.Graph.GraphId, caller, ct).ConfigureAwait(false);
    }

    public async Task<TaskGraphCheckpoint> ResumeAsync(string graphId, CallerIdentity caller, CancellationToken ct)
    {
        await RequireBindingAsync(graphId, caller, ct).ConfigureAwait(false);
        return await _scheduler.ResumeAsync(graphId, caller, ct).ConfigureAwait(false);
    }

    public async Task<TaskGraphCheckpoint> RecoverTaskAsync(string graphId, string taskId,
        CallerIdentity caller, CancellationToken ct)
    {
        await RequireBindingAsync(graphId, caller, ct).ConfigureAwait(false);
        return await _scheduler.RecoverAgentTaskAsync(graphId, taskId, caller, ct).ConfigureAwait(false);
    }

    public Task PauseAsync(string graphId, CallerIdentity caller, CancellationToken ct) => _scheduler.PauseAsync(graphId, caller, ct);
    public Task CancelAsync(string graphId, CallerIdentity caller, CancellationToken ct) => _scheduler.CancelAsync(graphId, caller, ct);

    public Task<TaskGraphCheckpoint> ReconcileVerificationAsync(string graphId, string taskId, string invocationId,
        TaskVerificationCompletion completion, string note, CallerIdentity caller, CancellationToken ct) =>
        (_store as ITaskVerificationStore ?? throw new NotSupportedException("Verification reconciliation requires durable command storage."))
            .ReconcileTaskVerificationAsync(graphId, taskId, invocationId, completion, note, caller, ct);

    private async Task RequireBindingAsync(string graphId, CallerIdentity caller, CancellationToken ct)
    {
        var checkpoint = await _store.GetTaskGraphAsync(graphId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException("The task graph was not found.", nameof(graphId));
        if (checkpoint.Caller.Id != caller.Id || checkpoint.Caller.IsOwner != caller.IsOwner)
            throw new UnauthorizedAccessException("Only the graph's original caller and ownership level can control it.");
        if (checkpoint.AgentBinding is null)
            throw new WorkflowConflictException("The task graph has no accepted agent execution binding.");
    }
}
