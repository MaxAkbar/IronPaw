using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed record ExecutionTimelineEntry(string Id, DateTimeOffset Timestamp, string Actor, string Title,
    string EventType, int Revision)
{
    public string TimeLabel => Timestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

public sealed record ExecutionTimelineTask(string Id, string Title, string Status, string WaitingFor,
    string Activity, string Checks, string Details, bool Accepted, bool NeedsAttention);

public sealed record ExecutionTimelineSnapshot(string Id, string Title, string Status, string Summary,
    DateTimeOffset ObservedAt, ImmutableArray<ExecutionTimelineTask> Tasks,
    ImmutableArray<ExecutionTimelineEntry> Events, int TotalEvents)
{
    public int AcceptedCount => Tasks.Count(task => task.Accepted);
    public int TotalTasks => Tasks.Length;
    public int HiddenEvents => Math.Max(0, TotalEvents - Events.Length);
}

/// <summary>Read-only projections of established checkpoints and receipts. Never infers verification from model prose.</summary>
public static class ExecutionTimelineBuilder
{
    public const int MaxVisibleEvents = 250;

    public static ExecutionTimelineSnapshot ForGraph(TaskGraphHostDetail detail, DateTimeOffset observedAt)
    {
        var graph = detail.Checkpoint;
        var definitions = graph.Graph.Tasks.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
        var states = graph.Tasks.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
        var runs = detail.Invocations.Where(run => run.RootRunId == graph.AgentBinding?.RootRunId &&
            SameCaller(run.Request.Caller, graph.Caller)).ToDictionary(run => run.RunId, StringComparer.Ordinal);
        bool Accepted(string id) => states[id].Status == TaskGraphTaskStatus.Succeeded &&
            states[id].Acceptance?.Outcome == TaskAcceptanceOutcome.Accepted;
        var rows = graph.Graph.Tasks.Select(task => {
            var state = states[task.TaskId];
            var childId = state.AttemptId is { } attempt ? AgentTaskIdentity.ChildRunId(graph.DefinitionFingerprint, task.TaskId, attempt).Value : null;
            var child = childId is null ? null : runs.GetValueOrDefault(childId);
            var waiting = task.Dependencies.Where(id => !Accepted(id)).ToArray();
            var uncertain = state.Status == TaskGraphTaskStatus.NeedsReconciliation ||
                state.Verifications.Any(command => command.State == TaskVerificationInvocationState.NeedsReconciliation ||
                    command.State == TaskVerificationInvocationState.Started && state.LeaseToken is null) ||
                child is not null && WorkflowIdentity.HasUnknownEffects(child) && child.LeaseOwner is null;
            var status = uncertain ? "Needs review" : Accepted(task.TaskId) ? "Accepted" : state.Status switch {
                TaskGraphTaskStatus.Pending when graph.Control == WorkflowControl.Cancel => "Cancelled",
                TaskGraphTaskStatus.Pending when graph.Control == WorkflowControl.Pause => "Paused",
                TaskGraphTaskStatus.Pending when waiting.Length > 0 => "Waiting",
                TaskGraphTaskStatus.Pending => "Ready",
                TaskGraphTaskStatus.Running => "Working",
                TaskGraphTaskStatus.AwaitingAcceptance or TaskGraphTaskStatus.Succeeded => "Awaiting checks",
                TaskGraphTaskStatus.BudgetExhausted => "Limit reached",
                TaskGraphTaskStatus.Cancelled => "Cancelled",
                TaskGraphTaskStatus.Failed => "Failed",
                _ => "Needs review"
            };
            var checks = CheckSummary(state, graph.AgentBinding);
            var details = new StringBuilder().AppendLine(Clean(task.Objective, 1600)).AppendLine()
                .AppendLine("Graph: " + Clean(graph.Graph.GraphId)).AppendLine("Task: " + Clean(task.TaskId))
                .AppendLine("Attempt: " + Clean(state.AttemptId ?? "not started"))
                .AppendLine("Child invocation: " + Clean(childId ?? "not created"))
                .AppendLine("Status: " + status).AppendLine("Checks: " + checks.Text);
            if (child is not null) AppendInvocationDetails(details, child);
            if (state.Error is not null) details.AppendLine("Problem: " + Clean(state.Error, 1600));
            if (state.Acceptance is { } acceptance) details.AppendLine("Host decision: " + Clean(acceptance.Explanation, 1600));
            if (state.Result is { } result)
            {
                details.AppendLine().AppendLine(Accepted(task.TaskId) ? "Accepted outputs:" : "Proposed outputs (not yet accepted):");
                foreach (var artifact in result.Artifacts)
                    details.AppendLine($"{Clean(artifact.ArtifactId)}: {Clean(artifact.Location, 800)}\n  {artifact.ContentDigest}");
                details.AppendLine("Candidate revision: " + Clean(result.Revision));
            }
            foreach (var command in state.Verifications)
            {
                details.AppendLine().AppendLine($"Check: {Clean(command.ProfileId)} / {command.State}");
                if (command.Completion is not { } completion) { details.AppendLine("No established outcome; do not retry automatically."); continue; }
                details.AppendLine(Clean(completion.Explanation, 1600)).AppendLine($"Exit code: {completion.ExitCode?.ToString() ?? "unknown"}");
                if (completion.Tests is { } tests) details.AppendLine($"Tests: {tests.Passed}/{tests.Total} passed, {tests.Failed} failed, {tests.Skipped} skipped");
                if (!string.IsNullOrWhiteSpace(completion.StandardOutput.Text)) details.AppendLine("Output: " + Clean(completion.StandardOutput.Text, 2000));
                if (!string.IsNullOrWhiteSpace(completion.StandardError.Text)) details.AppendLine("Error output: " + Clean(completion.StandardError.Text, 2000));
                details.AppendLine($"Saved output digest: {completion.StandardOutput.Digest}");
            }
            return new ExecutionTimelineTask(task.TaskId, Clean(task.Objective, 120), status,
                waiting.Length == 0 ? "—" : Clean(string.Join(", ", waiting), 400), TaskActivity(state, child), checks.Text, details.ToString(),
                Accepted(task.TaskId), uncertain || checks.Failed || state.Status is TaskGraphTaskStatus.Failed or TaskGraphTaskStatus.BudgetExhausted);
        }).ToImmutableArray();

        var history = graph.Graph.GraphId;
        var entries = detail.Journal.Where(entry => entry.GraphId == history && entry.Revision <= graph.Revision).Select(entry =>
            (Stream: "graph/" + history, Entry: new ExecutionTimelineEntry("graph/" + history + "/" + entry.Revision,
                entry.Timestamp, entry.TaskId is { } id && definitions.ContainsKey(id) ? Clean(id) : "Workflow",
                EventTitle(entry.EventType), Clean(entry.EventType), entry.Revision))).ToList();
        entries.AddRange(detail.InvocationJournal.Where(entry => runs.TryGetValue(entry.RunId, out var run) && entry.Revision <= run.Revision)
            .Select(entry => (Stream: "run/" + entry.RunId, Entry: new ExecutionTimelineEntry("run/" + entry.RunId + "/" + entry.Revision,
                entry.Timestamp, Clean(runs[entry.RunId].Request.AgentId ?? runs[entry.RunId].TaskId), EventTitle(entry.EventType), Clean(entry.EventType), entry.Revision))));
        var events = OrderEvents(entries);
        var progress = TaskGraphProgress.Inspect(graph);
        var statusText = rows.Any(task => task.Status == "Needs review") ? "Needs review"
            : progress.ActiveClaims > 0 && progress.Status == TaskGraphRunStatus.Cancelled ? "Stopping; cancellation recorded"
            : progress.ActiveClaims > 0 && progress.Status == TaskGraphRunStatus.Paused ? "Pausing; work is settling" : GraphStatus(progress.Status);
        return new(history, Clean(graph.Graph.Objective, 240), statusText,
            $"{rows.Count(task => task.Accepted)} of {rows.Length} tasks accepted · {rows.Count(task => task.NeedsAttention)} need attention\n" +
            BudgetSummary(detail.Usage, detail.Budget) + "\nAcceptance reflects the configured host checks; model completion claims do not verify outputs.",
            observedAt, rows, events.TakeLast(MaxVisibleEvents).ToImmutableArray(), events.Length);
    }

    public static ExecutionTimelineSnapshot ForWorkflow(WorkflowCheckpoint root, IReadOnlyList<WorkflowCheckpoint> invocations,
        IReadOnlyList<WorkflowJournalEntry> journal, WorkflowUsage usage, WorkflowBudgetSnapshot budget, DateTimeOffset observedAt)
    {
        var runs = invocations.Where(run => run.RootRunId == root.RunId && SameCaller(run.Request.Caller, root.Request.Caller))
            .ToDictionary(run => run.RunId, StringComparer.Ordinal);
        var rows = runs.Values.OrderBy(run => run.CreatedAt).ThenBy(run => run.RunId, StringComparer.Ordinal).Select(run => {
            var status = WorkflowStatusText(run);
            var details = $"{Clean(run.Request.UserInput, 1600)}\n\nAgent: {Clean(run.Request.AgentId ?? "Main agent")}\nStatus: {status}\n" +
                "A finished agent run is not independent build or test verification.";
            if (run.Error is { } error) details += "\nProblem: " + Clean(error, 1600);
            var invocationDetails = new StringBuilder();
            AppendInvocationDetails(invocationDetails, run);
            details += "\n" + invocationDetails;
            return new ExecutionTimelineTask(run.RunId, Clean(run.Request.AgentId ?? "Main agent"), status,
                run.ParentRunId is null ? "—" : Clean(run.ParentRunId), Activity(run), "No independent verification recorded", details, false,
                run.Status is WorkflowStatus.Failed or WorkflowStatus.NeedsReconciliation or WorkflowStatus.BudgetExhausted ||
                run.LeaseOwner is null && WorkflowIdentity.HasUnknownEffects(run));
        }).ToImmutableArray();
        var events = OrderEvents(journal.Where(entry => runs.TryGetValue(entry.RunId, out var run) && entry.Revision <= run.Revision)
            .Select(entry => (Stream: "run/" + entry.RunId, Entry: new ExecutionTimelineEntry("run/" + entry.RunId + "/" + entry.Revision,
                entry.Timestamp, Clean(runs[entry.RunId].Request.AgentId ?? "Main agent"), EventTitle(entry.EventType), Clean(entry.EventType), entry.Revision))));
        var statusText = runs.Values.Any(run => run.LeaseOwner is null && WorkflowIdentity.HasUnknownEffects(run)) ? "Needs review"
            : root.Control == WorkflowControl.Cancel && runs.Values.Any(run => run.LeaseOwner is not null) ? "Stopping; cancellation recorded"
            : root.Control == WorkflowControl.Pause && runs.Values.Any(run => run.LeaseOwner is not null) ? "Pausing; work is settling" : WorkflowStatusText(root);
        return new(root.RunId, Clean(root.Request.UserInput, 240), statusText,
            $"{rows.Length} agent invocations · {rows.Count(task => task.NeedsAttention)} need attention\n" + BudgetSummary(usage, budget) + "\nRun completion records the agent outcome; independent artifact checks are separate.",
            observedAt, rows, events.TakeLast(MaxVisibleEvents).ToImmutableArray(), events.Length);
    }

    private static (string Text, bool Failed) CheckSummary(TaskGraphTaskState state, TaskGraphAgentBinding? binding)
    {
        var required = binding?.CommandVerificationRules.Where(rule => rule.TaskId == state.TaskId).Select(rule => rule.ProfileId).Distinct().ToArray() ?? [];
        if (required.Length == 0) return ("Artifact checks only; no build/test check configured", false);
        var failed = false;
        var descriptions = required.Select(id => {
            var invocation = state.Verifications.LastOrDefault(command => command.ProfileId == id);
            var profile = binding!.VerificationProfiles.Single(profile => profile.ProfileId == id);
            var name = profile.Kind == TaskVerificationKind.Tests ? "Tests" : "Build";
            if (invocation is null) return name + ": waiting";
            if (invocation.State != TaskVerificationInvocationState.Completed)
                return name + (invocation.State == TaskVerificationInvocationState.Started && state.LeaseToken is not null ? ": running" : ": outcome unknown");
            var receipt = invocation.Completion;
            if (receipt?.Outcome != TaskCommandOutcome.Passed || receipt.ExitCode != 0) { failed = true; return name + ": not passed"; }
            if (profile.Kind == TaskVerificationKind.Tests)
            {
                if (receipt.Tests is { Total: > 0, Failed: 0, Skipped: 0 } tests && tests.Passed == tests.Total)
                    return $"Tests: {tests.Passed}/{tests.Total} passed";
                failed = true;
                return "Tests: no complete passing report";
            }
            return "Build: passed";
        });
        var text = string.Join(" · ", descriptions);
        return (text, failed);
    }

    private static string TaskActivity(TaskGraphTaskState task, WorkflowCheckpoint? run)
    {
        if (task.Verifications.Any(command => command.State == TaskVerificationInvocationState.NeedsReconciliation))
            return "Verification outcome unknown";
        if (task.Verifications.Any(command => command.State == TaskVerificationInvocationState.Started))
            return task.LeaseToken is not null ? "Host verification in progress" : "Verification outcome unknown";
        if (run is not null) return Activity(run);
        return task.Result is not null ? "Candidate saved" : task.AttemptId is not null ? "Task attempt recorded" : "Not started";
    }

    private static void AppendInvocationDetails(StringBuilder details, WorkflowCheckpoint run)
    {
        details.AppendLine("Invocation: " + Clean(run.RunId)).AppendLine("Root: " + Clean(run.RootRunId))
            .AppendLine("Parent: " + Clean(run.ParentRunId ?? "none"))
            .AppendLine("Agent: " + Clean(run.Request.AgentId ?? "Main agent"));
        foreach (var tool in run.ToolExecutions ?? [])
            details.AppendLine($"Tool {Clean(tool.Call.ToolName)}: {(tool.Result is { } result ? result.IsError ? "reported an error" : "result saved" : tool.State == WorkflowToolState.Started ? "outcome not established" : "waiting")} / {Clean(tool.ExecutionId)}");
    }

    private static string Activity(WorkflowCheckpoint? run)
    {
        if (run is null) return "Not started";
        var started = (run.ToolExecutions ?? []).Where(tool => tool.State == WorkflowToolState.Started && tool.Result is null).ToArray();
        if (started.Length > 0) return (run.LeaseOwner is null ? "Outcome unknown: " : "Tool in progress: ") + Clean(string.Join(", ", started.Select(tool => tool.Call.ToolName)), 400);
        if ((run.HumanRequests ?? []).Any(request => request.Response is null)) return "Waiting for your response";
        if ((run.ToolExecutions ?? []).Any(tool => tool.State == WorkflowToolState.WaitingForChild && tool.Result is null)) return "Waiting for a specialist";
        return run.LeaseOwner is not null ? "Agent working" : WorkflowStatusText(run);
    }

    private static string WorkflowStatusText(WorkflowCheckpoint run) => run.LeaseOwner is null && WorkflowIdentity.HasUnknownEffects(run)
        ? "Needs review" : run.Status switch {
        WorkflowStatus.Succeeded => "Run finished", WorkflowStatus.NeedsReconciliation => "Needs review",
        WorkflowStatus.BudgetExhausted => "Limit reached", WorkflowStatus.AwaitingInput => "Waiting for input",
        _ => run.Status.ToString()
    };
    private static string GraphStatus(TaskGraphRunStatus status) => status switch {
        TaskGraphRunStatus.Succeeded => "All tasks accepted", TaskGraphRunStatus.NeedsReconciliation => "Needs review",
        TaskGraphRunStatus.BudgetExhausted => "Limit reached", TaskGraphRunStatus.AwaitingAcceptance => "Awaiting checks",
        _ => status.ToString()
    };
    private static string BudgetSummary(WorkflowUsage usage, WorkflowBudgetSnapshot budget) =>
        $"Model calls: {usage.ModelCalls}/{budget.Limits.MaxModelCalls} · Tool calls: {usage.ToolCalls}/{budget.Limits.MaxToolCalls}\n" +
        $"Tokens charged: {budget.ChargedTokens}; reserved: {budget.ReservedTokens}; remaining: {budget.RemainingTokens?.ToString() ?? "no limit"}\n" +
        $"Execution time: {budget.ExecutionTime.TotalSeconds:F1}s; remaining: {(budget.RemainingExecutionTime is { } time ? $"{time.TotalSeconds:F1}s" : "no limit")}";

    // Merge journal streams by timestamp while preserving revision order within each stream,
    // including after wall-clock corrections. No global causal order is invented for parallel runs.
    private static ImmutableArray<ExecutionTimelineEntry> OrderEvents(IEnumerable<(string Stream, ExecutionTimelineEntry Entry)> entries)
    {
        var streams = entries.GroupBy(item => item.Stream, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new Queue<ExecutionTimelineEntry>(group.Select(item => item.Entry).DistinctBy(entry => entry.Id).OrderBy(entry => entry.Revision))).ToArray();
        var ordered = ImmutableArray.CreateBuilder<ExecutionTimelineEntry>();
        var comparer = Comparer<(DateTimeOffset Time, string Id)>.Create((left, right) => {
            var time = left.Time.CompareTo(right.Time);
            return time != 0 ? time : StringComparer.Ordinal.Compare(left.Id, right.Id);
        });
        var pending = new PriorityQueue<Queue<ExecutionTimelineEntry>, (DateTimeOffset Time, string Id)>(comparer);
        foreach (var stream in streams.Where(stream => stream.Count > 0)) pending.Enqueue(stream, (stream.Peek().Timestamp, stream.Peek().Id));
        while (pending.TryDequeue(out var next, out _))
        {
            ordered.Add(next.Dequeue());
            if (next.Count > 0) pending.Enqueue(next, (next.Peek().Timestamp, next.Peek().Id));
        }
        return ordered.ToImmutable();
    }

    private static string EventTitle(string type) => type switch {
        "accepted" or "created" or "graph_root" => "Work saved",
        "task_claimed" or "agent_task_reclaimed" => "Task started or recovered",
        "verification_claimed" or "verification_started" => "Host verification started",
        "verification_receipt" => "Verification result saved",
        "verification_reconciled" or "tool_reconciled" or "task_reconciled" => "Independently established outcome recorded",
        "task_result" => "Task result saved; acceptance may still be pending",
        "task_acceptance" => "Host acceptance decision recorded",
        "task_abandoned" or "recovered" or "interrupted" => "Execution interrupted; inspect its saved state",
        "control_pause" or "paused" => "Pause recorded", "control_cancel" or "cancelled" => "Cancellation recorded",
        "control_none" => "Pause cleared", "model_started" => "Model request started",
        "model_completed" or "model_response" => "Model response saved", "tool_started" => "Tool started",
        "tool_completed" or "tool_result" => "Tool result saved", "succeeded" => "Agent run finished; verification is separate",
        "failed" => "Failure recorded", "budget_exhausted" => "Execution limit reached",
        _ => "Saved state updated"
    };
    private static bool SameCaller(CallerIdentity left, CallerIdentity right) => left.Id == right.Id && left.IsOwner == right.IsOwner;
    public static string Clean(string? text, int limit = 240)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var safe = new string(text.Take(limit).Where(character => !char.IsControl(character) || character is '\n' or '\t').ToArray());
        return text.Length > limit ? safe + "…" : safe;
    }
}
