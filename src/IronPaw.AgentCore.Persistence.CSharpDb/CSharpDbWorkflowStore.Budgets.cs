using System.Text;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence.CSharpDb;

public sealed partial class CSharpDbWorkflowStore
{
    private sealed record ExecutionClock(long ElapsedTicks, DateTimeOffset? ActiveSince);

    public async Task<WorkflowBudgetSnapshot> GetBudgetAsync(RunId rootRunId, CancellationToken ct)
    {
        WorkflowBudgetSnapshot? snapshot = null;
        await InGateAsync(async () =>
        {
            var checkpoints = await ReadAllAsync(ct).ConfigureAwait(false);
            var root = checkpoints.SingleOrDefault(x => x.RunId == rootRunId.Value && x.RootRunId == x.RunId)
                ?? throw new WorkflowConflictException("Root workflow was not found.");
            var clock = await ReadClockAsync(rootRunId.Value, ct).ConfigureAwait(false) ?? new ExecutionClock(0, null);
            snapshot = Snapshot(root, checkpoints, EffectiveElapsed(clock, _timeProvider.GetUtcNow()));
        }, ct).ConfigureAwait(false);
        return snapshot!;
    }

    private async Task ValidateBudgetAdmissionAsync(IReadOnlyList<WorkflowCheckpoint> existing,
        WorkflowCheckpoint? previous, WorkflowCheckpoint proposed, CancellationToken ct)
    {
        var oldUsage = previous?.Usage ?? new WorkflowUsage();
        var usage = proposed.Usage ?? new WorkflowUsage();
        var oldAttemptIds = (previous?.ModelCalls ?? []).SelectMany(x => x.Attempts ?? []).Select(x => x.AttemptId).ToHashSet(StringComparer.Ordinal);
        var newAttempts = (proposed.ModelCalls ?? []).SelectMany(x => x.Attempts ?? []).Where(x => !oldAttemptIds.Contains(x.AttemptId)).ToArray();
        var newModel = usage.ModelCalls > oldUsage.ModelCalls || newAttempts.Length > 0;
        var newTool = usage.ToolCalls > oldUsage.ToolCalls || (proposed.ToolExecutions ?? []).Any(tool =>
            tool.State is WorkflowToolState.Started or WorkflowToolState.WaitingForChild &&
            !(previous?.ToolExecutions?.Any(old => old.ExecutionId == tool.ExecutionId && old.State == tool.State) ?? false));
        var finishing = previous?.Result is null && proposed.Result?.Status == AgentRunStatus.Succeeded ||
            previous?.Status != WorkflowStatus.Succeeded && proposed.Status == WorkflowStatus.Succeeded;
        if (!newModel && !newTool && !finishing) return;
        var root = proposed.RunId == proposed.RootRunId ? proposed
            : existing.Single(x => x.RunId == proposed.RootRunId);
        var clock = await ReadClockAsync(root.RunId, ct).ConfigureAwait(false) ?? new ExecutionClock(0, null);
        var elapsed = EffectiveElapsed(clock, _timeProvider.GetUtcNow());
        if (root.Budget?.MaxExecutionTime is { } maxTime && elapsed >= maxTime)
            throw new WorkflowBudgetExceededException("The shared workflow execution time budget is exhausted.");
        if (root.Budget?.MaxTotalTokens is { } tokenLimit)
        {
            if (newModel && newAttempts.Any(x => x.ReservedTokens < x.MaxOutputTokens))
                throw new WorkflowConflictException("A token-limited model attempt must reserve at least its output allowance.");
            if (newModel && newAttempts.Length == 0)
                throw new WorkflowConflictException("A token-limited model admission requires a durable reservation.");
            var snapshot = Snapshot(root, ReplaceCheckpoint(existing, proposed), elapsed);
            if (newModel && checked(snapshot.ChargedTokens + snapshot.ReservedTokens) > tokenLimit ||
                newTool && checked(snapshot.ChargedTokens + snapshot.ReservedTokens) >= tokenLimit ||
                finishing && snapshot.ChargedTokens > tokenLimit)
                throw new WorkflowBudgetExceededException("The shared workflow token budget is exhausted.");
        }
        if (newAttempts.Length > usage.ModelCalls - oldUsage.ModelCalls)
            throw new WorkflowConflictException("Every new model attempt must consume a model call.");
    }

    private static WorkflowBudgetSnapshot Snapshot(WorkflowCheckpoint root,
        IReadOnlyList<WorkflowCheckpoint> checkpoints, TimeSpan elapsed)
    {
        long charged = 0, reserved = 0;
        foreach (var checkpoint in checkpoints.Where(x => x.RootRunId == root.RunId))
        {
            charged = checked(charged + KnownTokens(checkpoint.Usage));
            foreach (var attempt in (checkpoint.ModelCalls ?? []).SelectMany(x => x.Attempts ?? []))
            {
                var remainder = Math.Max(0, attempt.ReservedTokens - attempt.ReportedTokens);
                if (attempt.State == WorkflowReservationState.Running) reserved = checked(reserved + remainder);
                else if (attempt.State == WorkflowReservationState.Unknown || !attempt.UsageComplete)
                    charged = checked(charged + remainder);
            }
        }
        var limits = root.Budget ?? new WorkflowBudget();
        return new(root.RunId, limits, charged, reserved, elapsed,
            limits.MaxExecutionTime is { } limit ? limit > elapsed ? limit - elapsed : TimeSpan.Zero : null);
    }

    private async Task UpdateClockAsync(string rootRunId, IReadOnlyList<WorkflowCheckpoint> next,
        CancellationToken ct, IReadOnlyList<WorkflowCheckpoint>? legacySource = null)
    {
        var clock = await ReadClockAsync(rootRunId, ct).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        if (clock is null)
        {
            var unclean = (legacySource ?? []).Where(x => x.RootRunId == rootRunId &&
                (x.LeaseOwner is not null || x.Status == WorkflowStatus.Running)).ToArray();
            // Older schemas did not keep an execution clock. Charge the whole
            // uncertain interval rather than granting a fresh budget on upgrade.
            clock = new ExecutionClock(0, unclean.Length > 0 ? unclean.Min(x => x.CreatedAt) : null);
        }
        var elapsed = EffectiveElapsed(clock, now);
        var active = next.Any(x => x.RootRunId == rootRunId && x.LeaseOwner is not null) ||
            (await ReadTaskGraphsAsync(ct).ConfigureAwait(false)).Any(graph => graph.AgentBinding?.RootRunId == rootRunId &&
                graph.Tasks.Any(task => task.LeaseToken is not null || task.Verifications.Any(invocation => invocation.State is
                    TaskVerificationInvocationState.Started or TaskVerificationInvocationState.NeedsReconciliation)));
        var updated = new ExecutionClock(elapsed.Ticks, active ? Later(now, clock.ActiveSince) : null);
        var payload = Quote(Serialize(updated));
        if (await ClockExistsAsync(rootRunId, ct).ConfigureAwait(false))
            await ExecuteAsync($"UPDATE workflow_clocks SET payload = {payload} WHERE root_run_id = {Quote(rootRunId)}", ct).ConfigureAwait(false);
        else
        {
            var id = await NextIdAsync("workflow_clocks", ct).ConfigureAwait(false);
            await ExecuteAsync($"INSERT INTO workflow_clocks VALUES ({id}, {Quote(rootRunId)}, {payload})", ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> ClockExistsAsync(string rootRunId, CancellationToken ct) =>
        await ReadClockAsync(rootRunId, ct).ConfigureAwait(false) is not null;

    private async Task<ExecutionClock?> ReadClockAsync(string rootRunId, CancellationToken ct)
    {
        var query = await _database.ExecuteAsync($"SELECT payload FROM workflow_clocks WHERE root_run_id = {Quote(rootRunId)}", ct).ConfigureAwait(false);
        try
        {
            if (!await query.MoveNextAsync(ct).ConfigureAwait(false)) return null;
            var clock = JsonSerializer.Deserialize<ExecutionClock>(query.Current[0].AsText, JsonOptions)
                ?? throw new InvalidDataException("The workflow execution clock is missing.");
            if (clock.ElapsedTicks < 0) throw new InvalidDataException("The workflow execution clock is invalid.");
            return clock;
        }
        finally { await query.DisposeAsync().ConfigureAwait(false); }
    }

    private static DateTimeOffset Later(DateTimeOffset now, DateTimeOffset? previous) => previous > now ? previous.Value : now;
    private static TimeSpan EffectiveElapsed(ExecutionClock clock, DateTimeOffset now) => TimeSpan.FromTicks(checked(
        clock.ElapsedTicks + (clock.ActiveSince is { } since && now > since ? (now - since).Ticks : 0)));
    private static IReadOnlyList<WorkflowCheckpoint> ReplaceCheckpoint(IReadOnlyList<WorkflowCheckpoint> existing,
        WorkflowCheckpoint proposed) => existing.Where(x => x.RunId != proposed.RunId).Append(proposed).ToArray();
    private static long KnownTokens(WorkflowUsage? usage) => usage is null ? 0 : checked(
        usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens + usage.CacheWriteTokens);
    private static bool HasRunningAttempts(WorkflowCheckpoint checkpoint) =>
        (checkpoint.ModelCalls ?? []).SelectMany(x => x.Attempts ?? []).Any(x => x.State == WorkflowReservationState.Running);

    private static void ValidateAttempts(WorkflowCheckpoint checkpoint)
    {
        var attempts = (checkpoint.ModelCalls ?? []).SelectMany(x => x.Attempts ?? []).ToArray();
        if (attempts.Select(x => x.AttemptId).Distinct(StringComparer.Ordinal).Count() != attempts.Length)
            throw new WorkflowConflictException("Model attempt identities must be unique within a workflow.");
        long reported = 0;
        var runningCount = 0;
        foreach (var attempt in attempts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(attempt.AttemptId);
            if (attempt.ReservedTokens < 0 || attempt.ReportedTokens < 0 || attempt.MaxOutputTokens <= 0)
                throw new WorkflowConflictException("Model attempt reservations and reported usage must be nonnegative with a positive output allowance.");
            if (!Enum.IsDefined(attempt.State)) throw new NotSupportedException("This model reservation state is not supported.");
            if (!attempt.UsageReported && attempt.ReportedTokens != 0)
                throw new WorkflowConflictException("Reported model tokens require a usage receipt.");
            if (attempt.UsageComplete && !attempt.UsageReported)
                throw new WorkflowConflictException("Complete model usage requires a recorded usage receipt.");
            if (attempt.State == WorkflowReservationState.Running) runningCount++;
            reported = checked(reported + attempt.ReportedTokens);
        }
        if (runningCount > 1)
            throw new WorkflowConflictException("An invocation can have only one active model attempt.");
        foreach (var call in checkpoint.ModelCalls ?? [])
        {
            var callAttempts = call.Attempts ?? [];
            if (callAttempts.Take(Math.Max(0, callAttempts.Count - 1)).Any(x => x.State == WorkflowReservationState.Running) ||
                call.Response is not null && callAttempts.Any(x => x.State == WorkflowReservationState.Running))
                throw new WorkflowConflictException("A superseded or completed model attempt cannot remain active.");
        }
        if (reported > KnownTokens(checkpoint.Usage))
            throw new WorkflowConflictException("Model attempt receipts exceed recorded workflow token usage.");
    }

    private static void ValidateAttemptUpdates(WorkflowCheckpoint previous, WorkflowCheckpoint next)
    {
        long reportedIncrease = 0;
        var oldCalls = previous.ModelCalls ?? [];
        var newCalls = next.ModelCalls ?? [];
        for (var i = 0; i < newCalls.Count; i++)
        {
            var before = i < oldCalls.Count ? oldCalls[i].Attempts ?? [] : [];
            var after = newCalls[i].Attempts ?? [];
            if (after.Count < before.Count)
                throw new WorkflowConflictException("Recorded model attempts cannot be removed.");
            if (i < oldCalls.Count && oldCalls[i].Response is not null && after.Count != before.Count)
                throw new WorkflowConflictException("A completed model call cannot add attempts.");
            for (var j = 0; j < before.Count; j++)
            {
                var old = before[j];
                var updated = after[j];
                if (old.AttemptId != updated.AttemptId || old.ReservedTokens != updated.ReservedTokens ||
                    old.MaxOutputTokens != updated.MaxOutputTokens || updated.ReportedTokens < old.ReportedTokens ||
                    old.UsageReported && !updated.UsageReported || old.UsageComplete && !updated.UsageComplete ||
                    !ValidAttemptTransition(old.State, updated.State) ||
                    old.State == WorkflowReservationState.Settled && Serialize(old) != Serialize(updated))
                    throw new WorkflowConflictException("Model reservations and receipts cannot be removed, rewritten, or reopened.");
                reportedIncrease = checked(reportedIncrease + updated.ReportedTokens - old.ReportedTokens);
            }
            foreach (var added in after.Skip(before.Count))
            {
                if (added.State != WorkflowReservationState.Running || added.UsageReported || added.UsageComplete || added.ReportedTokens != 0)
                    throw new WorkflowConflictException("A new model attempt must begin as an unreported running reservation.");
                reportedIncrease = checked(reportedIncrease + added.ReportedTokens);
            }
        }
        if ((newCalls.Any(x => x.Attempts?.Count > 0) || oldCalls.Any(x => x.Attempts?.Count > 0)) &&
            KnownTokens(next.Usage) - KnownTokens(previous.Usage) != reportedIncrease)
            throw new WorkflowConflictException("Workflow token usage and model attempt receipts must be committed together.");
    }

    private static bool ValidAttemptTransition(WorkflowReservationState before, WorkflowReservationState after) => before == after ||
        before == WorkflowReservationState.Running && after is WorkflowReservationState.Settled or WorkflowReservationState.Unknown ||
        before == WorkflowReservationState.Unknown && after == WorkflowReservationState.Settled;

    private static WorkflowCheckpoint MigrateBudgetAttempts(WorkflowCheckpoint checkpoint)
    {
        return checkpoint with { ModelCalls = checkpoint.ModelCalls?.Select((call, index) =>
        {
            if (call.Attempts is not null) return call;
            if (call.Response is not null) return call; // Completed replay remains historical evidence.
            var input = Encoding.UTF8.GetByteCount(call.SystemPrompt ?? checkpoint.Request.SystemPromptOverride ?? "") +
                Encoding.UTF8.GetByteCount(checkpoint.Request.UserInput) +
                (checkpoint.InitialTranscript ?? []).Sum(x => (long)Encoding.UTF8.GetByteCount(x.Content));
            var output = Math.Max(1, checkpoint.Request.MaxOutputTokens);
            return call with { Attempts = [new WorkflowModelAttempt($"legacy:{checkpoint.RunId}:{index}",
                checked(input + output), output, State: WorkflowReservationState.Unknown)] };
        }).ToArray() };
    }
}
