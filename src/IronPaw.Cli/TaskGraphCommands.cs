using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using Spectre.Console;

namespace IronPaw.Cli;

internal static class TaskGraphCommands
{
    public static bool IsGraphCommand(CliOptions options) => options.StartGraphFile is not null || options.ListGraphs ||
        options.ShowGraphId is not null || options.ResumeGraphId is not null || options.PauseGraphId is not null ||
        options.CancelGraphId is not null || options.RecoverGraphId is not null || options.ReconcileTaskGraphId is not null ||
        options.ReconcileCommandGraphId is not null || options.ReconcileToolGraphId is not null;

    public static async Task<int> ExecuteAsync(CliOptions options, TaskGraphHostController controller,
        Func<string, (bool Configured, bool Quit)> ensureProvider, CancellationToken ct, IAnsiConsole? console = null)
    {
        console ??= AnsiConsole.Console;
        if (!options.TryValidate(out var error) || !IsGraphCommand(options))
            throw new ArgumentException(error ?? "A graph command is required.");
        var caller = CallerIdentity.LocalOwner();
        if (options.ListGraphs)
        {
            console.Write(new Text($"Source workspace: {controller.WorkspaceRoot}\nManaged workspaces: {controller.WorkspaceStoreRoot}\n"));
            var graphs = await controller.ListAsync(caller, ct);
            if (graphs.Count == 0)
                console.Write(new Text("No saved task graphs in this workspace.\n"));
            foreach (var graph in graphs.OrderByDescending(graph => graph.UpdatedAt))
            {
                var progress = TaskGraphProgress.Inspect(graph);
                console.Write(new Text($"Graph {graph.Graph.GraphId}: {progress.Status}; tasks={graph.Tasks.Length}; " +
                    $"ready={progress.ReadyTaskIds.Length}; blocked={progress.BlockedTasks.Length}; " +
                    $"active={progress.ActiveClaims}; control={graph.Control}\n{graph.Graph.Objective}\n"));
            }
            return 0;
        }
        if (options.StartGraphFile is not null)
        {
            var document = await TaskGraphDocumentJson.ReadAsync<TaskGraphLaunchDocument>(options.StartGraphFile, ct);
            controller.ValidateLaunch(document, caller, options.AllowLocalVerification);
            var configuration = ensureProvider(document.Options.Provider);
            if (!configuration.Configured) return configuration.Quit ? 0 : 2;
            var started = await controller.StartAsync(document, caller, options.AllowLocalVerification, ct);
            RenderDetail(await controller.InspectAsync(started.Graph.GraphId, caller, ct), console);
            return ExitCode(TaskGraphProgress.Inspect(started).Status);
        }

        var graphId = (options.ShowGraphId ?? options.ResumeGraphId ?? options.PauseGraphId ?? options.CancelGraphId ??
            options.RecoverGraphId ?? options.ReconcileTaskGraphId ?? options.ReconcileCommandGraphId ?? options.ReconcileToolGraphId)!;
        // Inspect and authorize before resolving credentials or consuming reconciliation files.
        await controller.InspectAsync(graphId, caller, ct);
        if (options.PauseGraphId is not null) await controller.PauseAsync(graphId, caller, ct);
        else if (options.CancelGraphId is not null) await controller.CancelAsync(graphId, caller, ct);
        else if (options.ReconcileTaskGraphId is not null)
        {
            var result = await TaskGraphDocumentJson.ReadAsync<WorkflowTaskResult>(options.GraphResultFile!, ct);
            await controller.ReconcileTaskAsync(graphId, options.GraphTaskId!, result, options.ReconcileNote!, caller, ct);
        }
        else if (options.ReconcileCommandGraphId is not null)
        {
            var result = await TaskGraphDocumentJson.ReadAsync<TaskVerificationCompletion>(options.GraphResultFile!, ct);
            await controller.ReconcileVerificationAsync(graphId, options.GraphTaskId!, options.VerificationInvocationId!,
                result, options.ReconcileNote!, caller, ct);
        }
        else if (options.ReconcileToolGraphId is not null)
        {
            var result = await TaskGraphDocumentJson.ReadAsync<ToolResult>(options.GraphResultFile!, ct);
            await controller.ReconcileChildToolAsync(graphId, options.GraphTaskId!, options.GraphToolExecutionId!,
                result, options.ReconcileNote!, caller, ct);
        }
        else if (options.ResumeGraphId is not null || options.RecoverGraphId is not null)
        {
            var requiredProvider = await controller.GetRequiredProviderAsync(graphId, caller, ct, options.GraphTaskId, options.AllowLocalVerification);
            if (requiredProvider is not null)
            {
                var configuration = ensureProvider(requiredProvider);
                if (!configuration.Configured) return configuration.Quit ? 0 : 2;
            }
            if (options.RecoverGraphId is not null)
                await controller.RecoverTaskAsync(graphId, options.GraphTaskId!, caller, options.AllowLocalVerification, ct);
            else
                await controller.ResumeAsync(graphId, caller, options.AllowLocalVerification, ct);
        }
        var detail = await controller.InspectAsync(graphId, caller, ct);
        RenderDetail(detail, console);
        return options.ResumeGraphId is not null || options.RecoverGraphId is not null ? ExitCode(detail.Progress.Status) : 0;
    }

    private static int ExitCode(TaskGraphRunStatus status) => status is TaskGraphRunStatus.Failed or
        TaskGraphRunStatus.Cancelled or TaskGraphRunStatus.BudgetExhausted or TaskGraphRunStatus.NeedsReconciliation ? 2 : 0;

    private static void RenderDetail(TaskGraphHostDetail detail, IAnsiConsole console)
    {
        var graph = detail.Checkpoint;
        console.Write(new Text($"Graph {graph.Graph.GraphId}: {detail.Progress.Status}; control={graph.Control}; revision={graph.Revision}\n" +
            $"{graph.Graph.Objective}\nFingerprint: {graph.DefinitionFingerprint}\n"));
        if (graph.AgentBinding is { } binding)
            console.Write(new Text($"Provider: {binding.Provider}; model: {binding.Model}; base: {binding.BaseCommit}; root: {binding.RootRunId}\n"));
        foreach (var task in graph.Tasks)
        {
            console.Write(new Text($"\nTask {task.TaskId}: {task.Status}; attempt={task.AttemptId ?? "-"}; " +
                $"child={(task.AttemptId is null || graph.AgentBinding is null ? "-" : AgentTaskIdentity.ChildRunId(graph.DefinitionFingerprint, task.TaskId, task.AttemptId).Value)}\n"));
            if (task.Error is not null) console.Write(new Text($"Error: {task.Error}\n"));
            if (task.ReconciliationNote is not null) console.Write(new Text($"Reconciliation: {task.ReconciliationNote}\n"));
            foreach (var block in detail.Progress.BlockedTasks.Where(block => block.TaskId == task.TaskId))
                console.Write(new Text($"Blocked by {block.DependencyId}: {block.DependencyStatus}\n"));
            if (task.Acceptance is { } accepted)
                console.Write(new Text($"Acceptance: {accepted.Outcome}; verifier={accepted.VerifierId}; {accepted.Explanation}\n"));
            if (task.Result is { } result)
            {
                console.Write(new Text($"Result: {result.Status}; revision={result.Revision}; {result.Explanation}\n"));
                foreach (var issue in result.UnresolvedIssues) console.Write(new Text($"Unresolved: {issue}\n"));
                foreach (var artifact in result.Artifacts)
                    console.Write(new Text($"Artifact {artifact.ArtifactId}: {artifact.Location}; digest={artifact.ContentDigest}; revision={artifact.Revision}\n"));
            }
            foreach (var command in task.Verifications)
            {
                console.Write(new Text($"Command {command.InvocationId}: profile={command.ProfileId}; state={command.State}; revision={command.Revision}\n"));
                if (command.Completion is { } completion)
                {
                    console.Write(new Text($"Outcome: {completion.Outcome}; exit={completion.ExitCode?.ToString() ?? "-"}; worker={completion.WorkerId}; {completion.Explanation}\n"));
                    if (completion.Tests is { } tests)
                        console.Write(new Text($"Tests: total={tests.Total}; passed={tests.Passed}; failed={tests.Failed}; skipped={tests.Skipped}\n"));
                    RenderLog("stdout", completion.StandardOutput, console);
                    RenderLog("stderr", completion.StandardError, console);
                }
                if (command.ReconciliationNote is not null)
                    console.Write(new Text($"Command reconciliation: {command.ReconciliationNote}\n"));
            }
        }
        if (detail.Budget is { } budget) ConsoleUi.RenderWorkflowBudget(budget, console);
        console.Write(new Text($"Usage: model calls={detail.Usage.ModelCalls}; tool calls={detail.Usage.ToolCalls}\n"));
        foreach (var invocation in detail.Invocations.Where(invocation => invocation.ParentRunId is not null))
        {
            console.Write(new Text($"\nChild {invocation.RunId}: task={invocation.TaskId}; state={invocation.Status}; control={invocation.Control}\n"));
            foreach (var request in invocation.HumanRequests?.Where(request => request.Response is null) ?? [])
                console.Write(new Text($"Pending {request.Kind}: request={request.RequestId}; {request.Prompt}\n"));
            foreach (var tool in invocation.ToolExecutions?.Where(tool => tool.Result is null) ?? [])
                console.Write(new Text($"Child tool {tool.ExecutionId}: {tool.Call.ToolName}; state={tool.State}\n"));
        }
        console.Write(new Text("\nGraph journal:\n"));
        foreach (var entry in detail.Journal)
            console.Write(new Text($"{entry.Timestamp:O} revision={entry.Revision} {entry.EventType}; task={entry.TaskId ?? "-"}; attempt={entry.AttemptId ?? "-"}\n"));
    }

    private static void RenderLog(string name, TaskVerificationLog log, IAnsiConsole console)
    {
        console.Write(new Text($"{name}: bytes={log.ByteCount}; truncated={log.Truncated}; digest={log.Digest}\n"));
        if (log.Text.Length > 0) console.Write(new Text(log.Text + "\n"));
    }
}
