using System.Text.Json;
using System.Text.Json.Serialization;
using IronPaw.AgentCore.Abstractions;
using Spectre.Console;

namespace IronPaw.Cli;

internal static class WorkflowCommands
{
    public static bool IsWorkflowCommand(CliOptions options) => options.ListWorkflows || options.ResumeRunId is not null ||
        options.PauseRunId is not null || options.CancelRunId is not null;

    public static async Task<int> ExecuteAsync(
        CliOptions options,
        IWorkflowStore store,
        IResumableAgentOrchestrator orchestrator,
        IAgentEventSink eventSink,
        Func<string, (bool Configured, bool Quit)> ensureProvider,
        CancellationToken ct,
        IAnsiConsole? console = null)
    {
        console ??= AnsiConsole.Console;
        if (options.ListWorkflows)
        {
            var savedWorkflows = await store.ListAsync(ct);
            var budgets = new Dictionary<string, WorkflowBudgetSnapshot>(StringComparer.Ordinal);
            foreach (var rootId in savedWorkflows.Select(w => w.RootRunId).Distinct(StringComparer.Ordinal))
            {
                budgets[rootId] = await store.GetBudgetAsync(new RunId(rootId), ct);
            }
            RenderWorkflows(savedWorkflows, budgets, console);
            return 0;
        }

        var runId = new RunId((options.ResumeRunId ?? options.PauseRunId ?? options.CancelRunId)!);
        var checkpoint = await store.GetAsync(runId, ct);
        if (checkpoint is null)
        {
            ConsoleUi.RenderError($"Workflow '{runId.Value}' was not found in this workspace. Use --list-workflows.");
            return 2;
        }

        var caller = CallerIdentity.LocalOwner();
        if (options.PauseRunId is not null)
        {
            await orchestrator.PauseAsync(runId, caller, ct);
            console.Write(new Text($"Pause requested for workflow '{runId.Value}'. Use --resume to continue.\n"));
            return 0;
        }

        if (options.CancelRunId is not null)
        {
            await orchestrator.CancelAsync(runId, caller, ct);
            console.Write(new Text($"Cancellation requested for workflow '{runId.Value}'.\n"));
            return 0;
        }

        if (options.AnswerRequestId is not null)
        {
            await orchestrator.AnswerAsync(runId, options.AnswerRequestId, options.Answer!, caller, ct);
        }

        if (options.ApproveRequestId is not null || options.DenyRequestId is not null)
        {
            await orchestrator.DecideApprovalAsync(runId, (options.ApproveRequestId ?? options.DenyRequestId)!,
                options.ApproveRequestId is not null, caller, ct);
        }

        if (options.ReconcileToolExecutionId is not null)
        {
            await using var input = File.OpenRead(options.ReconcileResultFile!);
            var serializerOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            serializerOptions.Converters.Add(new JsonStringEnumConverter());
            var result = await JsonSerializer.DeserializeAsync<ToolResult>(input,
                serializerOptions, ct)
                ?? throw new ArgumentException("The reconciliation result file must contain a JSON ToolResult object.");
            if (result.Content is null)
            {
                throw new ArgumentException("The reconciliation ToolResult must contain Content.");
            }

            await orchestrator.ReconcileToolAsync(runId, options.ReconcileToolExecutionId, result,
                options.ReconcileNote!, caller, ct);
        }

        // Responses can change the saved state, including a descendant's suspension.
        checkpoint = await store.GetAsync(runId, ct) ?? throw new InvalidOperationException("The workflow disappeared while resuming.");
        var workflows = await store.ListAsync(ct);
        var descendantIds = new HashSet<string>(StringComparer.Ordinal) { checkpoint.RunId };
        var previousCount = 0;
        while (previousCount != descendantIds.Count)
        {
            previousCount = descendantIds.Count;
            descendantIds.UnionWith(workflows.Where(w => w.ParentRunId is not null && descendantIds.Contains(w.ParentRunId))
                .Select(w => w.RunId));
        }

        var workflowTree = workflows.Where(w => descendantIds.Contains(w.RunId)).Append(checkpoint).ToArray();
        var hasPendingHumanRequest = workflowTree.Any(w => w.HumanRequests?.Any(r => r.Response is null) == true);
        var hasUnknownEffects = workflowTree.Any(WorkflowIdentity.HasUnknownEffects);
        if (checkpoint.Result is null && checkpoint.Control != WorkflowControl.Cancel &&
            !hasPendingHumanRequest && !hasUnknownEffects)
        {
            var configuration = ensureProvider(checkpoint.Request.Provider);
            if (!configuration.Configured)
            {
                return configuration.Quit ? 0 : 2;
            }
        }

        var resumed = await orchestrator.ResumeAsync(runId, caller, eventSink, ct);
        ConsoleUi.RenderFinal(resumed, console);
        return resumed.Status is AgentRunStatus.Succeeded or AgentRunStatus.AwaitingInput or AgentRunStatus.Paused ? 0 : 2;
    }

    private static void RenderWorkflows(IReadOnlyList<WorkflowCheckpoint> workflows,
        IReadOnlyDictionary<string, WorkflowBudgetSnapshot> budgets, IAnsiConsole console)
    {
        if (workflows.Count == 0)
        {
            console.Write(new Text("No saved workflows in this workspace.\n", new Style(Color.Yellow)));
            return;
        }

        var table = new Table().RoundedBorder()
            .AddColumn("Run ID").AddColumn("State").AddColumn("Session")
            .AddColumn("Provider / model").AddColumn("Model / tool calls").AddColumn("Control");
        foreach (var workflow in workflows.OrderByDescending(w => w.UpdatedAt))
        {
            table.AddRow(new Text(workflow.RunId), new Text(workflow.Status.ToString()),
                new Text(workflow.Request.SessionKey.Value),
                new Text($"{workflow.Request.Provider} / {workflow.Request.Model}"),
                new Text($"{workflow.Usage?.ModelCalls ?? 0} / {workflow.Usage?.ToolCalls ?? 0}"),
                new Text(workflow.Control.ToString()));
        }

        console.Write(table);
        foreach (var budget in budgets.Values)
        {
            ConsoleUi.RenderWorkflowBudget(budget, console);
        }
        foreach (var workflow in workflows.OrderByDescending(w => w.UpdatedAt))
        {
            console.Write(new Text($"\nRun {workflow.RunId}: root={workflow.RootRunId}; parent={workflow.ParentRunId ?? "-"}; task={workflow.TaskId}\n"));
            foreach (var request in workflow.HumanRequests?.Where(r => r.Response is null) ?? [])
            {
                console.Write(new Text($"Pending {request.Kind}: request={request.RequestId}\n{request.Prompt}\n"));
                if (request.ExecutionId is not null)
                    console.Write(new Text($"Operation: {request.ExecutionId}; fingerprint={request.OperationFingerprint ?? "-"}\n"));
            }

            foreach (var tool in workflow.ToolExecutions?.Where(t => t.Result is null) ?? [])
            {
                var state = tool.State == WorkflowToolState.Started ? "Outcome unknown" : tool.State.ToString();
                console.Write(new Text($"Tool {tool.ExecutionId} ({tool.Call.ToolName}): {state}" +
                    (tool.ChildRunId is null ? "\n" : $"; child={tool.ChildRunId}\n")));
            }
        }
    }
}
