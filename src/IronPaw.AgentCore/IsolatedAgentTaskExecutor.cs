using System.Collections.Immutable;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore;

/// <summary>Only mediated file tools are available; the model cannot execute arbitrary code.</summary>
internal sealed class IsolatedAgentTaskExecutor(IAgentTaskGraphStore graphStore, IWorkflowStore workflowStore,
    ITaskWorkspaceManager workspaces, ILlmClient llmClient) : IWorkflowTaskExecutor
{
    public async Task<WorkflowTaskResult> ExecuteAsync(TaskGraphClaim claim, CancellationToken ct)
    {
        var checkpoint = await AgentTaskClaim.RequireAsync(graphStore, claim, verification: false, ct).ConfigureAwait(false);
        var binding = checkpoint.AgentBinding!;
        var request = AgentTaskBindingValidator.CreateChildRequest(checkpoint.Graph, checkpoint.Caller, binding,
            claim.Task, claim.AttemptId, claim.Inputs);
        var previous = await workflowStore.GetAsync(request.RequestedRunId!.Value, ct).ConfigureAwait(false);
        if (previous?.LeaseOwner is not null)
            throw new WorkflowConflictException("The task invocation is still active. Wait for its owner to settle it before recovery.");
        if (previous is not null && WorkflowIdentity.HasUnknownEffects(previous))
            throw new WorkflowReconciliationRequiredException("The saved task invocation contains tool actions with unknown outcomes.");
        var resources = await workflowStore.GetBudgetAsync(new RunId(binding.RootRunId), ct).ConfigureAwait(false);
        if (AgentTaskDeadline.Exhausted(resources, admittingModel: previous is null))
            return BudgetResult(claim, "The shared graph budget was exhausted before task execution.");
        if (previous is null && (await workflowStore.GetUsageAsync(new RunId(binding.RootRunId), ct).ConfigureAwait(false))
            .ModelCalls >= binding.SharedBudget.MaxModelCalls)
            return BudgetResult(claim, "The shared graph model-call budget was exhausted before workspace preparation.");

        await using var deadline = new AgentTaskDeadline(resources.RemainingExecutionTime, ct);
        try
        {
            var workspace = binding.DependencyMode == TaskDependencyMode.AcceptedChanges
                ? await workspaces.PrepareAsync(binding, claim, TaskDependencyResolver.Resolve(checkpoint, claim), deadline.Token).ConfigureAwait(false)
                : await workspaces.OpenOrCreateAsync(binding, claim, deadline.Token).ConfigureAwait(false);
            var afterPreparation = await workflowStore.GetBudgetAsync(new RunId(binding.RootRunId), deadline.Token).ConfigureAwait(false);
            if (AgentTaskDeadline.Exhausted(afterPreparation, admittingModel: previous is null))
                return BudgetResult(claim, "The shared graph budget was exhausted during workspace preparation.");
            if (binding.DependencyMode == TaskDependencyMode.AcceptedChanges)
                await workspaces.ValidateWorkingStateAsync(workspace, (previous?.ToolExecutions ?? []).ToImmutableArray(), deadline.Token).ConfigureAwait(false);
            var tools = ScopedTaskToolRegistry.Create(workspace, claim.Task.Resources);
            // Projections live outside the model's scoped workspace. The workflow store remains authoritative.
            var runtime = Path.Combine(workspace.AttemptPath, "runtime");
            Directory.CreateDirectory(runtime);
            using var lanes = new SessionLaneScheduler();
            var orchestrator = new AgentOrchestrator(lanes,
                new FileSessionStore(new(Path.Combine(runtime, "sessions.json"), TimeSpan.Zero)),
                new JsonTranscriptStore(Path.Combine(runtime, "transcripts")), tools,
                new ToolExecutor(), new ToolSchemaNormalizer(), llmClient,
                new TranscriptSanitizer(), new ToolResultTruncator(), new TaskPromptBuilder(claim.Task, claim.Inputs),
                workflowStore: workflowStore);
            AgentRunResult outcome;
            if (previous is { Result: null, Status: WorkflowStatus.BudgetExhausted })
                return BudgetResult(claim, "The saved task invocation exhausted its persistent budget.");
            outcome = await orchestrator.RunAsync(request, NullAgentEventSink.Instance, deadline.Token).ConfigureAwait(false);
            var finishedChild = await RequireKnownEffectsAsync(request.RequestedRunId.Value).ConfigureAwait(false);
            if (outcome.Status is AgentRunStatus.Paused or AgentRunStatus.AwaitingInput)
                throw new WorkflowSuspendedException(WorkflowStatus.Paused, "The graph task was suspended; recover its saved invocation explicitly.");
            if (outcome.Status == AgentRunStatus.Cancelled)
                return new(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId, WorkflowTaskStatus.Cancelled,
                    "cancelled", "The task invocation was cancelled. Its workspace and durable tool receipts are preserved.");
            // Budget expiry cannot authorize additional capture work. Keep the original workspace for inspection.
            deadline.Token.ThrowIfCancellationRequested();
            if (binding.DependencyMode == TaskDependencyMode.AcceptedChanges)
                await workspaces.ValidateWorkingStateAsync(workspace, (finishedChild?.ToolExecutions ?? []).ToImmutableArray(), deadline.Token).ConfigureAwait(false);
            var capture = await workspaces.CaptureAsync(workspace, claim.Task.ExpectedArtifacts, deadline.Token).ConfigureAwait(false);
            var status = outcome.Status switch
            {
                AgentRunStatus.Succeeded when capture.MissingRequiredArtifacts.IsEmpty => WorkflowTaskStatus.AwaitingVerification,
                AgentRunStatus.BudgetExhausted => WorkflowTaskStatus.BudgetExhausted,
                AgentRunStatus.Cancelled => WorkflowTaskStatus.Cancelled,
                _ => WorkflowTaskStatus.Failed
            };
            return new(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId, status, capture.Revision,
                outcome.FinalAssistantMessage.Length == 0 ? $"Agent loop ended with {outcome.Status}." : outcome.FinalAssistantMessage)
            {
                Artifacts = capture.Artifacts,
                UnresolvedIssues = capture.MissingRequiredArtifacts.Select(id => $"Required artifact '{id}' was not produced.")
                    .Concat(status == WorkflowTaskStatus.Failed && outcome.Status != AgentRunStatus.Succeeded
                        ? [$"Agent loop ended with {outcome.Status}."] : Array.Empty<string>()).ToImmutableArray()
            };
        }
        catch (OperationCanceledException) when (deadline.Expired && !ct.IsCancellationRequested)
        {
            await RequireKnownEffectsAsync(request.RequestedRunId.Value).ConfigureAwait(false);
            return BudgetResult(claim, "The shared graph execution-time budget expired. The task workspace is preserved.");
        }
        catch (WorkflowBudgetExceededException error)
        {
            await RequireKnownEffectsAsync(request.RequestedRunId.Value).ConfigureAwait(false);
            return BudgetResult(claim, error.Message);
        }
        catch (TaskInputConflictException error)
        {
            await RequireKnownEffectsAsync(request.RequestedRunId.Value).ConfigureAwait(false);
            return new(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId, WorkflowTaskStatus.Failed,
                claim.Inputs?.Digest ?? "input-conflict", "The accepted task inputs could not be composed: " + error.Message)
            { UnresolvedIssues = [error.Message] };
        }
    }

    private async Task<WorkflowCheckpoint?> RequireKnownEffectsAsync(RunId childId)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var child = await workflowStore.GetAsync(childId, cleanup.Token).ConfigureAwait(false);
        if (child is not null && WorkflowIdentity.HasUnknownEffects(child))
            throw new WorkflowReconciliationRequiredException("A task tool outcome is unknown. Reconcile its durable operation before recovering the task.");
        return child;
    }

    private static WorkflowTaskResult BudgetResult(TaskGraphClaim claim, string explanation) => new(
        claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId, WorkflowTaskStatus.BudgetExhausted,
        "budget-exhausted", explanation);

    private sealed class TaskPromptBuilder(WorkflowTaskDefinition task, TaskAttemptInputs? inputs) : ISystemPromptBuilder
    {
        public string BuildPrompt(AgentRunRequest request, IReadOnlyList<ITool> tools) =>
            new DefaultSystemPromptBuilder().BuildPrompt(request with { SystemPromptOverride = null }, tools) +
            "\n\nTask execution boundary:\n" +
            "All tool paths are relative to this task's isolated workspace. The available file tools enforce its resource scope. " +
            "Produce the declared files and report unresolved issues. Loop completion is a candidate for separate host verification.\n" +
            "Readable paths: " + JsonSerializer.Serialize(task.Resources.ReadPaths) + "\n" +
            "Writable paths: " + JsonSerializer.Serialize(task.Resources.WritePaths) + "\n" +
            "Expected artifacts: " + JsonSerializer.Serialize(task.ExpectedArtifacts) +
            (inputs is null ? "" : "\nAccepted predecessor changes have been composed into this workspace. " +
                "The readable and writable scopes above still apply. Input revision: " + inputs.Digest) +
            (request.SystemPromptOverride is null ? "" : "\n\nHost task instructions:\n" + request.SystemPromptOverride);
    }
}

internal static class AgentTaskClaim
{
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 256 };

    public static async Task<TaskGraphCheckpoint> RequireAsync(ITaskGraphStore store, TaskGraphClaim claim,
        bool verification, CancellationToken ct)
    {
        var checkpoint = await store.GetTaskGraphAsync(claim.GraphId, ct).ConfigureAwait(false)
            ?? throw new WorkflowConflictException("The task graph was not found.");
        if (checkpoint.AgentBinding is null || checkpoint.DefinitionFingerprint != claim.GraphFingerprint)
            throw new WorkflowConflictException("The task has no matching accepted execution binding.");
        var definition = checkpoint.Graph.Tasks.SingleOrDefault(task => task.TaskId == claim.Task.TaskId);
        var state = checkpoint.Tasks.SingleOrDefault(task => task.TaskId == claim.Task.TaskId);
        if (definition is null || state is null || state.AttemptId != claim.AttemptId || state.LeaseToken != claim.LeaseToken ||
            state.Status != (verification ? TaskGraphTaskStatus.AwaitingAcceptance : TaskGraphTaskStatus.Running) ||
            JsonSerializer.Serialize(state.Inputs, JsonOptions) != JsonSerializer.Serialize(claim.Inputs, JsonOptions) ||
            JsonSerializer.Serialize(definition, JsonOptions) != JsonSerializer.Serialize(claim.Task, JsonOptions))
            throw new WorkflowConflictException("The task claim is stale or differs from the accepted definition.");
        if (checkpoint.Control == WorkflowControl.Cancel) throw new WorkflowCancelledException("The task graph was permanently cancelled.");
        if (checkpoint.Control == WorkflowControl.Pause) throw new WorkflowSuspendedException(WorkflowStatus.Paused, "The task graph is paused.");
        return checkpoint;
    }
}

/// <summary>Includes workspace preparation and capture in the shared elapsed-time limit.</summary>
internal sealed class AgentTaskDeadline : IAsyncDisposable
{
    private readonly CancellationTokenSource _expired = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _linked;
    private readonly Task _timer;
    public AgentTaskDeadline(TimeSpan? remaining, CancellationToken ct)
    {
        _linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _expired.Token);
        _timer = remaining is { } duration ? ExpireAsync(duration) : Task.CompletedTask;
    }
    public CancellationToken Token => _linked.Token;
    public bool Expired => _expired.IsCancellationRequested;
    public static bool Exhausted(WorkflowBudgetSnapshot budget, bool admittingModel) =>
        budget.RemainingExecutionTime is { } time && time <= TimeSpan.Zero ||
        budget.Limits.MaxTotalTokens is { } limit && (budget.ChargedTokens > limit || admittingModel && budget.RemainingTokens <= 0);
    private async Task ExpireAsync(TimeSpan remaining)
    {
        try
        {
            while (remaining > TimeSpan.Zero)
            {
                var interval = remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining;
                await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
                remaining -= interval;
            }
            _expired.Cancel();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _timer.ConfigureAwait(false);
        _linked.Dispose(); _expired.Dispose(); _stop.Dispose();
    }
}
