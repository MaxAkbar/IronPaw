using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

/// <summary>
/// Verifies sealed file integrity only. Every required acceptance criterion must be explicitly
/// assigned to this verifier; semantic correctness and test execution need other host verifiers.
/// </summary>
internal sealed class ArtifactIntegrityTaskVerifier(IAgentTaskGraphStore graphStore, IWorkflowStore workflowStore,
    ITaskWorkspaceManager workspaces) : ITaskAcceptanceVerifier
{
    private const string VerifierId = "artifact-integrity/v1";
    public async Task<TaskAcceptanceDecision> VerifyAsync(TaskGraphClaim claim, WorkflowTaskResult result, CancellationToken ct)
    {
        var checkpoint = await AgentTaskClaim.RequireAsync(graphStore, claim, verification: true, ct).ConfigureAwait(false);
        var candidate = checkpoint.Tasks.Single(task => task.TaskId == claim.Task.TaskId).Result;
        if (JsonSerializer.Serialize(candidate) != JsonSerializer.Serialize(result))
            throw new WorkflowConflictException("Verification must inspect the exact saved candidate result.");
        if (!result.UnresolvedIssues.IsEmpty || result.VerificationEvidence.Any(evidence => evidence.Outcome != TaskVerificationOutcome.Passed))
            return new(TaskAcceptanceOutcome.Rejected, VerifierId, "The candidate retains unresolved issues or nonpassing verification evidence.");
        var binding = checkpoint.AgentBinding!;
        var state = checkpoint.Tasks.Single(task => task.TaskId == claim.Task.TaskId);
        if (state.Verifications.Any(invocation => invocation.State != TaskVerificationInvocationState.Completed))
            return new(TaskAcceptanceOutcome.Inconclusive, VerifierId,
                "A verification command has an uncertain outcome. Establish its result and reconcile it before acceptance.");
        var resources = await workflowStore.GetBudgetAsync(new RunId(binding.RootRunId), ct).ConfigureAwait(false);
        if (AgentTaskDeadline.Exhausted(resources, admittingModel: false)) return BudgetDecision();
        await using var deadline = new AgentTaskDeadline(resources.RemainingExecutionTime, ct);
        try
        {
            TaskWorkspace workspace;
            try { workspace = await workspaces.OpenExistingAsync(binding, claim, deadline.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return new(TaskAcceptanceOutcome.Rejected, VerifierId,
                    "The saved workspace or input identity is missing or changed: " + error.Message);
            }
            if (!await workspaces.VerifyAsync(workspace, result, deadline.Token).ConfigureAwait(false))
                return new(TaskAcceptanceOutcome.Rejected, VerifierId, "The saved artifact revision is missing, changed or does not match the candidate.");
            var latest = await workflowStore.GetBudgetAsync(new RunId(binding.RootRunId), deadline.Token).ConfigureAwait(false);
            if (AgentTaskDeadline.Exhausted(latest, admittingModel: false)) return BudgetDecision();
            var mappings = binding.ArtifactVerificationRules.Where(rule => rule.TaskId == claim.Task.TaskId)
                .Select(rule => rule.CriterionId).ToHashSet(StringComparer.Ordinal);
            foreach (var rule in binding.CommandVerificationRules.Where(rule => rule.TaskId == claim.Task.TaskId))
            {
                var profile = binding.VerificationProfiles.Single(profile => profile.ProfileId == rule.ProfileId);
                var invocationId = TaskVerificationIdentity.InvocationId(claim, result, profile);
                var invocation = state.Verifications.SingleOrDefault(invocation => invocation.InvocationId == invocationId);
                if (invocation?.Completion is not { } completion)
                    return new(TaskAcceptanceOutcome.Inconclusive, VerifierId,
                        $"Required command profile '{profile.ProfileId}' has no completed verification receipt.");
                if (completion.Outcome == TaskCommandOutcome.BudgetExhausted) return BudgetDecision();
                if (completion.Outcome != TaskCommandOutcome.Passed)
                    return new(TaskAcceptanceOutcome.Rejected, VerifierId,
                        $"Command profile '{profile.ProfileId}' did not pass: {completion.Explanation}");
                mappings.Add(rule.CriterionId);
            }
            if (!claim.Task.AcceptanceCriteria.Any(criterion => criterion.Required) ||
                claim.Task.AcceptanceCriteria.Any(criterion => criterion.Required && !mappings.Contains(criterion.CriterionId)))
                return new(TaskAcceptanceOutcome.Inconclusive, VerifierId,
                    "Artifacts are intact. Acceptance requires at least one required criterion and an explicit host verifier for every required criterion.");
            return new(TaskAcceptanceOutcome.Accepted, VerifierId,
                binding.CommandVerificationRules.Any(rule => rule.TaskId == claim.Task.TaskId)
                    ? "The sealed artifact revision and all required configured artifact and command checks were verified."
                    : "The sealed artifact revision and every explicitly mapped required artifact-integrity criterion were verified.");
        }
        catch (OperationCanceledException) when (deadline.Expired && !ct.IsCancellationRequested) { return BudgetDecision(); }
        catch (TaskInputConflictException error)
        {
            return new(TaskAcceptanceOutcome.Rejected, VerifierId, "The saved input baseline is missing or changed: " + error.Message);
        }
    }
    private static TaskAcceptanceDecision BudgetDecision() => new(TaskAcceptanceOutcome.BudgetExhausted, VerifierId,
        "The shared graph budget was exhausted before artifact verification could complete.");
}
