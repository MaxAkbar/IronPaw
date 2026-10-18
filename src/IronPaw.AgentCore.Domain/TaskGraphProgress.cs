using System.Collections.Immutable;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public enum TaskGraphRunStatus
{
    Queued, Running, AwaitingAcceptance, NeedsReconciliation,
    Succeeded, Failed, Cancelled, Paused, BudgetExhausted
}

public sealed record TaskGraphDependencyBlock(string TaskId, string DependencyId,
    TaskGraphTaskStatus DependencyStatus);

public sealed record TaskGraphProgressSummary(TaskGraphRunStatus Status,
    ImmutableArray<string> ReadyTaskIds, ImmutableArray<TaskGraphDependencyBlock> BlockedTasks,
    int ActiveClaims);

/// <summary>Inspects a store-validated checkpoint without changing claims or authorizing work.</summary>
public static class TaskGraphProgress
{
    public static TaskGraphProgressSummary Inspect(TaskGraphCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var states = checkpoint.Tasks.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
        var ready = ImmutableArray.CreateBuilder<string>();
        var blocked = ImmutableArray.CreateBuilder<TaskGraphDependencyBlock>();
        foreach (var task in checkpoint.Graph.Tasks)
        {
            if (states[task.TaskId].Status != TaskGraphTaskStatus.Pending) continue;
            var dependenciesAccepted = true;
            foreach (var dependencyId in task.Dependencies)
            {
                var dependency = states[dependencyId];
                if (IsAccepted(dependency)) continue;
                dependenciesAccepted = false;
                blocked.Add(new(task.TaskId, dependencyId, dependency.Status));
            }
            if (dependenciesAccepted && checkpoint.Control == WorkflowControl.None) ready.Add(task.TaskId);
        }

        var active = checkpoint.Tasks.Count(task => task.LeaseToken is not null);
        var status = checkpoint.Tasks.Any(task => task.Status == TaskGraphTaskStatus.NeedsReconciliation ||
                task.Verifications.Any(invocation => invocation.State == TaskVerificationInvocationState.NeedsReconciliation))
            ? TaskGraphRunStatus.NeedsReconciliation
            : checkpoint.Control == WorkflowControl.Cancel ? TaskGraphRunStatus.Cancelled
            : checkpoint.Control == WorkflowControl.Pause ? TaskGraphRunStatus.Paused
            : checkpoint.Tasks.All(IsAccepted) ? TaskGraphRunStatus.Succeeded
            : active > 0 ? TaskGraphRunStatus.Running
            : ready.Count > 0 ? TaskGraphRunStatus.Queued
            : checkpoint.Tasks.Any(task => task.Status == TaskGraphTaskStatus.AwaitingAcceptance)
                ? TaskGraphRunStatus.AwaitingAcceptance
            : checkpoint.Tasks.Any(task => task.Status == TaskGraphTaskStatus.Failed) ? TaskGraphRunStatus.Failed
            : checkpoint.Tasks.Any(task => task.Status == TaskGraphTaskStatus.BudgetExhausted)
                ? TaskGraphRunStatus.BudgetExhausted
            : checkpoint.Tasks.Any(task => task.Status == TaskGraphTaskStatus.Cancelled) ? TaskGraphRunStatus.Cancelled
            : TaskGraphRunStatus.Queued;
        return new(status, ready.ToImmutable(), blocked.ToImmutable(), active);
    }

    private static bool IsAccepted(TaskGraphTaskState state) =>
        state.Status == TaskGraphTaskStatus.Succeeded && state.Acceptance?.Outcome == TaskAcceptanceOutcome.Accepted;
}
