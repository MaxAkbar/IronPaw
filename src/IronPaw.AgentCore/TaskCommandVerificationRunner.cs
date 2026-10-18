using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

/// <summary>Records intent before any command and retains uncertain outcomes instead of retrying effects.</summary>
internal sealed class TaskCommandVerificationRunner(IAgentTaskGraphStore graphStore, IWorkflowStore workflowStore,
    ITaskWorkspaceManager workspaces, ITaskVerificationWorker? worker) : ITaskVerificationRunner
{
    public async Task RunAsync(TaskGraphClaim claim, WorkflowTaskResult result, CancellationToken ct)
    {
        var graph = await AgentTaskClaim.RequireAsync(graphStore, claim, verification: true, ct).ConfigureAwait(false);
        var binding = graph.AgentBinding!;
        var profileIds = binding.CommandVerificationRules.Where(rule => rule.TaskId == claim.Task.TaskId)
            .Select(rule => rule.ProfileId).Distinct(StringComparer.Ordinal).ToArray();
        if (profileIds.Length == 0) return;
        if (graphStore is not ITaskVerificationStore commands)
            throw new NotSupportedException("The graph store cannot durably record command verification.");
        var state = graph.Tasks.Single(task => task.TaskId == claim.Task.TaskId);
        if (JsonSerializer.Serialize(state.Result) != JsonSerializer.Serialize(result))
            throw new WorkflowConflictException("Command verification must inspect the exact saved candidate.");

        foreach (var profileId in profileIds)
        {
            ct.ThrowIfCancellationRequested();
            graph = await AgentTaskClaim.RequireAsync(graphStore, claim, verification: true, ct).ConfigureAwait(false);
            state = graph.Tasks.Single(task => task.TaskId == claim.Task.TaskId);
            if (state.Verifications.Any(invocation => invocation.State != TaskVerificationInvocationState.Completed)) return;
            var profile = binding.VerificationProfiles.Single(profile => profile.ProfileId == profileId);
            var invocationId = TaskVerificationIdentity.InvocationId(claim, result, profile);
            var saved = state.Verifications.SingleOrDefault(invocation => invocation.InvocationId == invocationId);
            if (saved is not null)
            {
                if (saved.Completion?.Outcome != TaskCommandOutcome.Passed) return;
                continue;
            }
            // A missing runtime grant never changes the accepted policy or launches a command.
            if (worker is null) return;
            worker.ValidateProfile(profile, graph.Caller);
            var budget = await workflowStore.GetBudgetAsync(new RunId(binding.RootRunId), ct).ConfigureAwait(false);
            if (AgentTaskDeadline.Exhausted(budget, admittingModel: false)) return;
            await using var deadline = new AgentTaskDeadline(budget.RemainingExecutionTime, ct);
            TaskVerificationCopy copy;
            try
            {
                var workspace = await workspaces.OpenExistingAsync(binding, claim, deadline.Token).ConfigureAwait(false);
                if (!await workspaces.VerifyAsync(workspace, result, deadline.Token).ConfigureAwait(false)) return;
                // Copying is reversible host I/O. Only process execution requires a durable effect intent.
                copy = await workspaces.CreateVerificationCopyAsync(workspace, result, invocationId, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.Expired && !ct.IsCancellationRequested) { return; }
            if (deadline.Expired && !ct.IsCancellationRequested) return;
            deadline.Token.ThrowIfCancellationRequested();
            TaskVerificationAdmission admission;
            try { admission = await commands.BeginTaskVerificationAsync(claim, profileId, deadline.Token).ConfigureAwait(false); }
            catch (WorkflowBudgetExceededException) { return; }
            catch (OperationCanceledException) when (deadline.Expired && !ct.IsCancellationRequested) { return; }
            if (!admission.ShouldExecute)
            {
                // Admission is authoritative if another host settled this exact invocation
                // after our read. A failed or uncertain receipt must stop later profiles.
                if (admission.Invocation.State != TaskVerificationInvocationState.Completed ||
                    admission.Invocation.Completion?.Outcome != TaskCommandOutcome.Passed) return;
                continue;
            }

            TaskVerificationCompletion completion;
            try
            {
                completion = await worker.ExecuteAsync(new(claim, result, profile,
                    TaskVerificationIdentity.ProfileFingerprint(profile), copy), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // After intent, an exception cannot establish whether repository code executed.
                completion = new(TaskCommandOutcome.NeedsReconciliation, "verification-host/v1",
                    $"The worker stopped without establishing a command outcome: {error.GetType().Name}: {error.Message}", null,
                    EmptyLog(), EmptyLog());
            }
            using var receiptCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await commands.CompleteTaskVerificationAsync(claim, invocationId, completion, receiptCt.Token).ConfigureAwait(false);
            if (completion.Outcome != TaskCommandOutcome.Passed) return;
        }
    }

    private static TaskVerificationLog EmptyLog() => new("", TaskVerificationIdentity.EmptyLogDigest, 0, false);
}
