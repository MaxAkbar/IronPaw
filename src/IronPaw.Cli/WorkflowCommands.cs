using System.Text.Json;
using System.Text.Json.Serialization;
using IronPaw.AgentCore.Abstractions;
using Spectre.Console;

namespace IronPaw.Cli;

internal static class WorkflowCommands
{
    public static bool IsWorkflowCommand(CliOptions options) => options.ListWorkflows || options.ResumeRunId is not null;

    public static async Task<int> ExecuteAsync(
        CliOptions options,
        IWorkflowStore store,
        IResumableAgentOrchestrator orchestrator,
        IAgentEventSink eventSink,
        Func<string, (bool Configured, bool Quit)> ensureProvider,
        CancellationToken ct)
    {
        if (options.ListWorkflows)
        {
            RenderWorkflows(await store.ListAsync(ct));
            return 0;
        }

        var runId = new RunId(options.ResumeRunId!);
        var checkpoint = await store.GetAsync(runId, ct);
        if (checkpoint is null)
        {
            ConsoleUi.RenderError($"Workflow '{runId.Value}' was not found in this workspace. Use --list-workflows.");
            return 2;
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
                options.ReconcileNote!, CallerIdentity.LocalOwner(), ct);
        }

        if (checkpoint.Result is null)
        {
            var configuration = ensureProvider(checkpoint.Request.Provider);
            if (!configuration.Configured)
            {
                return configuration.Quit ? 0 : 2;
            }
        }

        var resumed = await orchestrator.ResumeAsync(runId, CallerIdentity.LocalOwner(), eventSink, ct);
        ConsoleUi.RenderFinal(resumed);
        return resumed.Status is AgentRunStatus.Succeeded or AgentRunStatus.AwaitingInput ? 0 : 2;
    }

    private static void RenderWorkflows(IReadOnlyList<WorkflowCheckpoint> workflows)
    {
        if (workflows.Count == 0)
        {
            ConsoleUi.RenderWarning("No saved workflows in this workspace.");
            return;
        }

        var table = new Table().RoundedBorder()
            .AddColumn("Run ID").AddColumn("State").AddColumn("Session")
            .AddColumn("Provider / model").AddColumn("Model / tool calls").AddColumn("Unresolved tool executions");
        foreach (var workflow in workflows.OrderByDescending(w => w.UpdatedAt))
        {
            var unresolved = string.Join("\n", (workflow.ToolExecutions ?? [])
                .Where(t => t.Result is null).Select(t => $"{t.ExecutionId} ({t.Call.ToolName})"));
            table.AddRow(new Text(workflow.RunId), new Text(workflow.Status.ToString()),
                new Text(workflow.Request.SessionKey.Value),
                new Text($"{workflow.Request.Provider} / {workflow.Request.Model}"),
                new Text($"{workflow.Usage?.ModelCalls ?? 0} / {workflow.Usage?.ToolCalls ?? 0}"),
                new Text(string.IsNullOrEmpty(unresolved) ? "-" : unresolved));
        }

        AnsiConsole.Write(table);
    }
}
