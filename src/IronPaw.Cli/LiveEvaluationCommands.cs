using System.Text.Json;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using Spectre.Console;

namespace IronPaw.Cli;

/// <summary>Separate campaign host: it never initializes normal app state, add-ins, or provider prompts.</summary>
internal static class LiveEvaluationCommands
{
    private const string DatabaseName = "evaluation.cdb";

    public static bool IsEvaluationCommand(CliOptions options) => options.RunEvaluationFile is not null ||
        options.ResumeEvaluationId is not null || options.ShowEvaluationId is not null || options.ReconcileEvaluationId is not null ||
        options.ReconcileEvaluationCommandId is not null || options.ReconcileEvaluationTaskId is not null || options.ReconcileEvaluationToolId is not null;

    public static async Task<int> ExecuteAsync(CliOptions options, CancellationToken ct, IAnsiConsole? console = null,
        Func<LiveEvaluationSettings, ILlmClient>? createClient = null)
    {
        console ??= AnsiConsole.Console;
        if (!options.TryValidate(out var error) || !IsEvaluationCommand(options))
            return Invalid(console, error ?? "An evaluation command is required.");
        try
        {
            LiveEvaluationSettings? settings = null;
            if (options.RunEvaluationFile is not null)
            {
                // A disabled or malformed file must reject before journal, directory, credentials, or process work.
                settings = (await TaskGraphDocumentJson.ReadAsync<LiveEvaluationSettings>(options.RunEvaluationFile, ct)).RequireEnabled();
            }
            var output = RequireOutputDirectory(options.EvaluationOutputDirectory!, options.RunEvaluationFile is not null);
            var path = Path.Combine(output, DatabaseName);
            if (options.RunEvaluationFile is null && !File.Exists(path))
                throw new ArgumentException("The campaign directory has no evaluation.cdb journal; inspection and recovery do not create a campaign.");
            await using var store = await CSharpDbWorkflowStore.OpenAsync(path, ct);
            var owner = CallerIdentity.LocalOwner();
            var campaignId = options.ResumeEvaluationId ?? options.ShowEvaluationId ?? options.ReconcileEvaluationId ??
                options.ReconcileEvaluationCommandId ?? options.ReconcileEvaluationTaskId ?? options.ReconcileEvaluationToolId ??
                "evaluation-" + Guid.NewGuid().ToString("n");
            if (options.RunEvaluationFile is null)
            {
                var checkpoint = await ((ILiveEvaluationStore)store).GetEvaluationAsync(campaignId, owner, ct)
                    ?? throw new ArgumentException("No saved evaluation campaign has that ID in this directory.");
                if (options.ResumeEvaluationId is not null)
                {
                    using var binding = JsonDocument.Parse(checkpoint.Definition.BindingJson);
                    if (binding.RootElement.ValueKind != JsonValueKind.Object || !binding.RootElement.TryGetProperty("settings", out var frozenSettings))
                        throw new InvalidDataException("The campaign binding has no valid frozen settings.");
                    settings = TaskGraphDocumentJson.Parse<LiveEvaluationSettings>(frozenSettings.GetRawText()).RequireEnabled();
                }
            }
            ILlmClient client = settings is not null
                ? createClient?.Invoke(settings) ?? new EvaluationProviderClient(settings)
                : new LazyLlmClient(() => throw new InvalidOperationException("Inspection and billing reconciliation cannot execute a model."));
            try
            {
                var runner = new LiveEvaluationRunner((ILiveEvaluationStore)store, store, store, client);
                LiveEvaluationCampaignReport report;
                if (options.RunEvaluationFile is not null)
                    report = await runner.StartAsync(settings!, campaignId, output, owner, options.AllowLocalVerification, ct);
                else if (options.ResumeEvaluationId is not null)
                    report = await runner.ResumeAsync(campaignId, output, owner, options.AllowLocalVerification, ct);
                else
                {
                    // Authorize the saved campaign and graph before consuming an external receipt file.
                    var inspected = await runner.InspectAsync(campaignId, output, owner, ct);
                    if (options.EvaluationGraphId is not null)
                    {
                        var binding = TaskGraphDocumentJson.Parse<LiveEvaluationBinding>(inspected.Checkpoint.Definition.BindingJson);
                        if (!binding.Schedule.Any(trial => trial.GraphId == options.EvaluationGraphId))
                            throw new UnauthorizedAccessException("This graph does not belong to the selected evaluation campaign.");
                        var graph = await store.GetTaskGraphAsync(options.EvaluationGraphId, ct);
                        if (graph is null || graph.Caller.Id != owner.Id || graph.Caller.IsOwner != owner.IsOwner ||
                            !graph.Tasks.Any(task => task.TaskId == options.GraphTaskId))
                            throw new UnauthorizedAccessException("The evaluation graph/task does not belong to this caller and campaign.");
                    }
                    if (options.ReconcileEvaluationId is not null)
                    {
                        var receipt = await TaskGraphDocumentJson.ReadAsync<LiveEvaluationReceipt>(options.GraphResultFile!, ct);
                        await ((ILiveEvaluationStore)store).ReconcileEvaluationRequestAsync(campaignId,
                            options.EvaluationRequestId!, receipt, options.ReconcileNote!, owner, ct);
                        report = await runner.ExportReportAsync(campaignId, output, owner, ct);
                    }
                    else if (options.ReconcileEvaluationCommandId is not null)
                        report = await runner.ReconcileCommandAsync(campaignId, options.EvaluationGraphId!, options.GraphTaskId!, options.VerificationInvocationId!,
                            await TaskGraphDocumentJson.ReadAsync<TaskVerificationCompletion>(options.GraphResultFile!, ct), options.ReconcileNote!, output, owner, ct);
                    else if (options.ReconcileEvaluationTaskId is not null)
                        report = await runner.ReconcileTaskAsync(campaignId, options.EvaluationGraphId!, options.GraphTaskId!,
                            await TaskGraphDocumentJson.ReadAsync<WorkflowTaskResult>(options.GraphResultFile!, ct), options.ReconcileNote!, output, owner, ct);
                    else if (options.ReconcileEvaluationToolId is not null)
                        report = await runner.ReconcileToolAsync(campaignId, options.EvaluationGraphId!, options.GraphTaskId!, options.GraphToolExecutionId!,
                            await TaskGraphDocumentJson.ReadAsync<ToolResult>(options.GraphResultFile!, ct), options.ReconcileNote!, output, owner, ct);
                    else report = inspected;
                }
                console.Write(new Text($"Evaluation {report.CampaignId}: {report.Status}\n" +
                    $"Live baseline qualification: {report.LiveBaselineQualification}\n" +
                    $"Isolated campaign directory: {output}\nReport: {Path.Combine(output, "report.json")}\n" +
                    $"Estimated charged/reserved spend: {report.Checkpoint.Definition.Currency} {report.Checkpoint.ChargedSpend} / {report.Checkpoint.Definition.MaxTotalSpend}; " +
                    $"pending requests: {report.Checkpoint.Requests.Count(request => request.State != LiveEvaluationRequestState.Completed)}\n"));
                foreach (var trial in report.Trials)
                    console.Write(new Text($"Trial {trial.TrialId}/{trial.Strategy}: {trial.Status}; verified={trial.VerifiedCompletion}; " +
                        $"estimated spend={trial.EstimatedSpend}; wall ms={trial.ElapsedWallMilliseconds:F0}\n"));
                foreach (var request in report.Checkpoint.Requests.Where(request => request.State != LiveEvaluationRequestState.Completed))
                    console.Write(new Text($"Request {request.Request.RequestId}: {request.State}; trial={request.Request.TrialId}/{request.Request.Strategy}; " +
                        $"reserved={request.Request.ReservedSpend}\n"));
                await RenderOpenGraphIssuesAsync(report, store, console, ct);
                if (options.ShowEvaluationId is not null) return 0;
                if (options.ReconcileEvaluationId is not null || options.ReconcileEvaluationCommandId is not null ||
                    options.ReconcileEvaluationTaskId is not null || options.ReconcileEvaluationToolId is not null)
                    return report.Checkpoint.NeedsReconciliation ? 2 : 0;
                return report.Status == "Completed" && report.Trials.All(trial => trial.VerifiedCompletion) ? 0 : 2;
            }
            finally { (client as IDisposable)?.Dispose(); }
        }
        catch (OperationCanceledException)
        {
            console.Write(new Text("Evaluation canceled. Inspect the saved campaign before resuming; unknown billable requests require reconciliation.\n"));
            return 130;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Invalid(console, ex.Message);
        }
    }

    private static async Task RenderOpenGraphIssuesAsync(LiveEvaluationCampaignReport report, CSharpDbWorkflowStore store,
        IAnsiConsole console, CancellationToken ct)
    {
        var binding = TaskGraphDocumentJson.Parse<LiveEvaluationBinding>(report.Checkpoint.Definition.BindingJson);
        foreach (var scheduled in binding.Schedule)
        {
            var graph = await store.GetTaskGraphAsync(scheduled.GraphId, ct);
            if (graph is null) continue;
            var status = TaskGraphProgress.Inspect(graph).Status;
            if (status is TaskGraphRunStatus.Succeeded or TaskGraphRunStatus.Failed or TaskGraphRunStatus.Cancelled or TaskGraphRunStatus.BudgetExhausted) continue;
            console.Write(new Text($"Open graph {scheduled.GraphId}: {status}; trial={scheduled.TrialId}/{scheduled.Strategy}\n"));
            foreach (var task in graph.Tasks.Where(task => task.Status == TaskGraphTaskStatus.NeedsReconciliation ||
                task.Verifications.Any(command => command.State != TaskVerificationInvocationState.Completed)))
            {
                console.Write(new Text($"Task {task.TaskId}: {task.Status}; attempt={task.AttemptId ?? "-"}\n"));
                foreach (var command in task.Verifications.Where(command => command.State != TaskVerificationInvocationState.Completed))
                    console.Write(new Text($"Command {command.InvocationId}: profile={command.ProfileId}; state={command.State}\n"));
                if (task.AttemptId is null) continue;
                var child = await store.GetAsync(AgentTaskIdentity.ChildRunId(graph.DefinitionFingerprint, task.TaskId, task.AttemptId), ct);
                foreach (var tool in child?.ToolExecutions?.Where(tool => tool.Result is null) ?? [])
                    console.Write(new Text($"Tool {tool.ExecutionId}: name={tool.Call.ToolName}; state={tool.State}\n"));
            }
        }
    }

    private static string RequireOutputDirectory(string path, bool creates)
    {
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Directory.GetCurrentDirectory()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (output == Path.GetPathRoot(output) || output.Equals(workspace, comparison) ||
            output.StartsWith(workspace + Path.DirectorySeparatorChar, comparison) ||
            workspace.StartsWith(output + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("The evaluation campaign directory must be outside the current source workspace and must not contain it.");
        for (var current = output; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Evaluation directories cannot traverse filesystem links.");
        }
        if (creates && Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new ArgumentException("A new evaluation requires a new or empty campaign directory; use --resume-evaluation for an existing campaign.");
        return output;
    }

    private static int Invalid(IAnsiConsole console, string error)
    {
        console.Write(new Text("Evaluation command rejected.\n" + error + "\n"));
        return 2;
    }
}
