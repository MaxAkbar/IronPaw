using System.Collections.Immutable;
using System.Security.Cryptography;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore;

public sealed record LiveEvaluationTrialObservation(string TrialId, string CaseId, string Strategy, string GraphId,
    string BaseCommit, string InputDigest, TaskGraphRunStatus Status, bool VerifiedCompletion,
    double ElapsedWallMilliseconds, WorkflowUsage Usage, decimal EstimatedSpend,
    ImmutableArray<TaskGraphTaskState> Tasks, string? Error = null);
public sealed record LiveEvaluationStrategySummary(string Strategy, int Trials, int VerifiedCompletions,
    decimal EstimatedSpend, double MedianWallMilliseconds, double P95WallMilliseconds);
public sealed record LiveEvaluationPendingTrial(string TrialId, string Strategy, string GraphId,
    TaskGraphRunStatus Status, ImmutableArray<TaskGraphTaskState> Tasks);
public sealed record LiveEvaluationCampaignReport(string CampaignId, string Status, string LiveBaselineQualification,
    LiveEvaluationCheckpoint Checkpoint, ImmutableArray<LiveEvaluationTrialObservation> Trials,
    ImmutableArray<LiveEvaluationStrategySummary> Summary, ImmutableArray<string> Limitations)
{
    public ImmutableArray<LiveEvaluationPendingTrial> PendingTrials { get; init; } = [];
}

/// <summary>Opt-in paired trials sharing one durable monetary ledger and one fixed corpus/protocol.</summary>
public sealed class LiveEvaluationRunner
{
    private readonly ILiveEvaluationStore _evaluations;
    private readonly IAgentTaskGraphStore _graphs;
    private readonly IWorkflowStore _workflows;
    private readonly ILlmClient _client;
    private readonly GitTaskWorkspaceManager _workspaces = new();

    public LiveEvaluationRunner(ILiveEvaluationStore evaluations, IAgentTaskGraphStore graphs,
        IWorkflowStore workflows, ILlmClient client)
    {
        ArgumentNullException.ThrowIfNull(evaluations); ArgumentNullException.ThrowIfNull(graphs);
        ArgumentNullException.ThrowIfNull(workflows); ArgumentNullException.ThrowIfNull(client);
        if (!ReferenceEquals(evaluations, graphs) || !ReferenceEquals(graphs, workflows))
            throw new ArgumentException("Evaluation, graph and workflow ledgers must share one atomic store instance.");
        _evaluations = evaluations; _graphs = graphs; _workflows = workflows; _client = client;
    }

    public async Task<LiveEvaluationCampaignReport> StartAsync(LiveEvaluationSettings settings, string campaignId,
        string outputDirectory, CallerIdentity caller, bool allowLocalVerification, CancellationToken ct)
    {
        settings = settings.RequireEnabled();
        RequireExecution(caller, allowLocalVerification);
        var output = RequireOutput(outputDirectory);
        if (await _evaluations.GetEvaluationAsync(campaignId, caller, ct) is not null)
            throw new WorkflowConflictException("The campaign already exists; resume its immutable binding.");
        Directory.CreateDirectory(output);
        var dotnet = FindDotnet();
        var cases = await LiveEvaluationCorpus.CreateAsync(output, dotnet, ct);
        var binding = new LiveEvaluationBinding(settings, LiveEvaluationCorpus.Protocol, output, dotnet,
            ExecutableDigest(dotnet), cases, LiveEvaluationCorpus.Schedule(campaignId, settings));
        await _evaluations.CreateEvaluationAsync(new(campaignId, settings.Provider!, settings.Model!, settings.Currency,
            settings.MaxTotalSpend!.Value, TaskGraphDocumentJson.Serialize(binding)), caller, ct);
        return await ExecuteAsync(campaignId, binding, caller, ct);
    }

    public async Task<LiveEvaluationCampaignReport> ResumeAsync(string campaignId, string outputDirectory,
        CallerIdentity caller, bool allowLocalVerification, CancellationToken ct)
    {
        RequireExecution(caller, allowLocalVerification);
        var checkpoint = await RequireAsync(campaignId, caller, ct);
        var binding = Binding(checkpoint, outputDirectory);
        if (checkpoint.NeedsReconciliation || checkpoint.HasReservationOverrun || checkpoint.Control == WorkflowControl.Cancel)
            return await InspectAsync(campaignId, outputDirectory, caller, ct);
        if (checkpoint.Control == WorkflowControl.Pause)
            await _evaluations.SetEvaluationControlAsync(campaignId, WorkflowControl.None, caller, ct);
        return await ExecuteAsync(campaignId, binding, caller, ct);
    }

    public async Task<LiveEvaluationCampaignReport> InspectAsync(string campaignId, string outputDirectory,
        CallerIdentity caller, CancellationToken ct)
    {
        var checkpoint = await RequireAsync(campaignId, caller, ct);
        var binding = Binding(checkpoint, outputDirectory);
        var report = Report(checkpoint, binding);
        var pending = ImmutableArray.CreateBuilder<LiveEvaluationPendingTrial>();
        foreach (var trial in binding.Schedule.Where(s => !checkpoint.Trials.Any(t => t.TrialId == s.TrialId && t.Strategy == s.Strategy)))
        {
            var graph = await _graphs.GetTaskGraphAsync(trial.GraphId, ct);
            if (graph is null) continue;
            ValidateGraph(graph, LiveEvaluationCorpus.Graph(trial), Options(binding, binding.Cases.Single(c => c.CaseId == trial.CaseId), LiveEvaluationCorpus.Graph(trial)), caller);
            pending.Add(new(trial.TrialId, trial.Strategy, trial.GraphId, TaskGraphProgress.Inspect(graph).Status, graph.Tasks));
        }
        return report with { PendingTrials = pending.ToImmutable(), Status = report.Status == "Incomplete" &&
            pending.Any(t => t.Status == TaskGraphRunStatus.NeedsReconciliation) ? "NeedsReconciliation" : report.Status };
    }

    public async Task<LiveEvaluationCampaignReport> ExportReportAsync(string campaignId, string outputDirectory,
        CallerIdentity caller, CancellationToken ct)
    {
        var report = await InspectAsync(campaignId, outputDirectory, caller, ct);
        await SaveReportAsync(report, RequireOutput(outputDirectory), ct);
        return report;
    }

    public async Task<LiveEvaluationCampaignReport> ReconcileCommandAsync(string campaignId, string graphId, string taskId,
        string invocationId, TaskVerificationCompletion receipt, string note, string outputDirectory, CallerIdentity caller, CancellationToken ct)
    {
        var host = await ReconciliationHostAsync(campaignId, graphId, outputDirectory, caller, ct);
        await host.ReconcileVerificationAsync(graphId, taskId, invocationId, receipt, note, caller, ct);
        return await ExportReportAsync(campaignId, outputDirectory, caller, ct);
    }

    public async Task<LiveEvaluationCampaignReport> ReconcileTaskAsync(string campaignId, string graphId, string taskId,
        WorkflowTaskResult receipt, string note, string outputDirectory, CallerIdentity caller, CancellationToken ct)
    {
        var host = await ReconciliationHostAsync(campaignId, graphId, outputDirectory, caller, ct);
        await host.ReconcileTaskAsync(graphId, taskId, receipt, note, caller, ct);
        return await ExportReportAsync(campaignId, outputDirectory, caller, ct);
    }

    public async Task<LiveEvaluationCampaignReport> ReconcileToolAsync(string campaignId, string graphId, string taskId,
        string toolExecutionId, ToolResult receipt, string note, string outputDirectory, CallerIdentity caller, CancellationToken ct)
    {
        var host = await ReconciliationHostAsync(campaignId, graphId, outputDirectory, caller, ct);
        await host.ReconcileChildToolAsync(graphId, taskId, toolExecutionId, receipt, note, caller, ct);
        return await ExportReportAsync(campaignId, outputDirectory, caller, ct);
    }

    private async Task<TaskGraphHostController> ReconciliationHostAsync(string campaignId, string graphId,
        string outputDirectory, CallerIdentity caller, CancellationToken ct)
    {
        var campaign = await RequireAsync(campaignId, caller, ct);
        var binding = Binding(campaign, outputDirectory);
        var scheduled = binding.Schedule.SingleOrDefault(t => t.GraphId == graphId)
            ?? throw new ArgumentException("The selected graph does not belong to this campaign.", nameof(graphId));
        var fixture = binding.Cases.Single(c => c.CaseId == scheduled.CaseId);
        var graph = await _graphs.GetTaskGraphAsync(graphId, ct) ?? throw new ArgumentException("This trial graph has not started.", nameof(graphId));
        ValidateGraph(graph, LiveEvaluationCorpus.Graph(scheduled), Options(binding, fixture, LiveEvaluationCorpus.Graph(scheduled)), caller);
        return new(_graphs, _workflows, _client, fixture.SourceDirectory, Path.Combine(binding.OutputDirectory, "workspaces"), _workspaces);
    }

    private async Task<LiveEvaluationCampaignReport> ExecuteAsync(string campaignId, LiveEvaluationBinding binding,
        CallerIdentity caller, CancellationToken ct)
    {
        if (ExecutableDigest(binding.DotnetExecutable) != binding.DotnetExecutableSha256)
            throw new WorkflowConflictException("The reviewed verification executable changed after campaign admission.");
        foreach (var item in binding.Cases) await LiveEvaluationCorpus.ValidateAsync(item, ct);
        foreach (var scheduled in binding.Schedule)
        {
            var campaign = await RequireAsync(campaignId, caller, ct);
            if (campaign.Trials.Any(t => t.TrialId == scheduled.TrialId && t.Strategy == scheduled.Strategy)) continue;
            if (campaign.NeedsReconciliation || campaign.HasReservationOverrun || campaign.Control != WorkflowControl.None) break;
            var fixture = binding.Cases.Single(c => c.CaseId == scheduled.CaseId);
            var graph = LiveEvaluationCorpus.Graph(scheduled);
            var options = Options(binding, fixture, graph);
            var budgeted = new EvaluationBudgetedLlmClient(_client, _evaluations, binding.Settings, campaignId,
                scheduled.TrialId, scheduled.Strategy, caller, campaign.Definition.BindingJson);
            var runner = new AgentTaskGraphRunner(_graphs, _workflows, _workspaces, budgeted,
                new TrustedLocalTaskVerificationWorker(options.VerificationProfiles, TaskVerificationAuthority.TrustedLocalProcess));
            var checkpoint = await _graphs.GetTaskGraphAsync(scheduled.GraphId, ct);
            try
            {
                if (checkpoint is null)
                    checkpoint = await runner.StartAsync(graph, caller, scheduled.Strategy == "graph" ? 2 : 1, options, ct);
                else
                {
                    ValidateGraph(checkpoint, graph, options, caller);
                    if (TaskGraphProgress.Inspect(checkpoint).Status == TaskGraphRunStatus.NeedsReconciliation)
                    {
                        // Only explicit campaign resume reaches here. Unknown external effects remain blocked.
                        foreach (var task in checkpoint.Tasks.Where(t => t.Status == TaskGraphTaskStatus.NeedsReconciliation))
                        {
                            if (task.Verifications.Any(v => v.State != TaskVerificationInvocationState.Completed)) break;
                            var runId = AgentTaskIdentity.ChildRunId(checkpoint.DefinitionFingerprint, task.TaskId, task.AttemptId!);
                            var child = await _workflows.GetAsync(runId, ct);
                            if (child is null || WorkflowIdentity.HasUnknownEffects(child)) break;
                            checkpoint = await runner.RecoverTaskAsync(scheduled.GraphId, task.TaskId, caller, ct);
                        }
                    }
                    else if (!IsTerminal(TaskGraphProgress.Inspect(checkpoint).Status))
                        checkpoint = await runner.ResumeAsync(scheduled.GraphId, caller, ct);
                }
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _evaluations.SetEvaluationControlAsync(campaignId, WorkflowControl.Pause, caller, settle.Token);
                await SaveReportAsync(await InspectAsync(campaignId, binding.OutputDirectory, caller, settle.Token), binding.OutputDirectory, settle.Token);
                throw;
            }
            // A failed/interrupted provider never becomes a successful trial merely because the loop ended.
            campaign = await RequireAsync(campaignId, caller, ct);
            var status = TaskGraphProgress.Inspect(checkpoint).Status;
            if (campaign.NeedsReconciliation || !IsTerminal(status)) break;
            var usage = await _workflows.GetUsageAsync(AgentTaskIdentity.RootRunId(scheduled.GraphId), ct);
            var final = checkpoint.Tasks.Single(t => t.TaskId is "join" or "direct");
            var verified = status == TaskGraphRunStatus.Succeeded && final.Acceptance?.Outcome == TaskAcceptanceOutcome.Accepted &&
                final.Verifications.Length == 2 && final.Verifications.All(v => v.State == TaskVerificationInvocationState.Completed &&
                    v.Completion?.Outcome == TaskCommandOutcome.Passed && v.Completion.ExitCode == 0) &&
                final.Verifications.Single(v => v.ProfileId == "tests").Completion?.Tests is { Total: 5, Passed: 5, Failed: 0, Skipped: 0 };
            var observation = new LiveEvaluationTrialObservation(scheduled.TrialId, fixture.CaseId, scheduled.Strategy,
                scheduled.GraphId, fixture.BaseCommit, fixture.InputDigest, status, verified,
                Math.Max(0, (checkpoint.UpdatedAt - checkpoint.CreatedAt).TotalMilliseconds), usage,
                campaign.Requests.Where(r => r.Request.TrialId == scheduled.TrialId && r.Request.Strategy == scheduled.Strategy).Sum(r => r.ChargedSpend),
                checkpoint.Tasks, string.Join("; ", checkpoint.Tasks.Select(t => t.Error).Where(e => !string.IsNullOrEmpty(e))));
            await _evaluations.RecordEvaluationTrialAsync(campaignId, new(scheduled.TrialId, scheduled.Strategy,
                TrialFingerprint(campaign, scheduled, fixture), TaskGraphDocumentJson.Serialize(observation)), caller, ct);
            await SaveReportAsync(await InspectAsync(campaignId, binding.OutputDirectory, caller, ct), binding.OutputDirectory, ct);
            if (status is TaskGraphRunStatus.BudgetExhausted or TaskGraphRunStatus.Cancelled) break;
        }
        var report = await InspectAsync(campaignId, binding.OutputDirectory, caller, ct);
        await SaveReportAsync(report, binding.OutputDirectory, ct);
        return report;
    }

    private static bool IsTerminal(TaskGraphRunStatus status) => status is TaskGraphRunStatus.Succeeded or TaskGraphRunStatus.Failed
        or TaskGraphRunStatus.Cancelled or TaskGraphRunStatus.BudgetExhausted;

    private static TaskGraphAgentOptions Options(LiveEvaluationBinding binding, LiveEvaluationCaseBinding fixture, WorkflowTaskGraph graph) =>
        new(fixture.SourceDirectory, Path.Combine(binding.OutputDirectory, "workspaces"), fixture.BaseCommit,
            binding.Settings.Provider!, binding.Settings.Model!, new(ToolProfile.Coding), LiveEvaluationCorpus.Budget,
            MaxRounds: 4, MaxOutputTokens: binding.Settings.MaxOutputTokensPerRequest!.Value) {
            DependencyMode = TaskDependencyMode.AcceptedChanges,
            ArtifactVerificationRules = graph.Tasks.Select(t => new TaskArtifactVerificationRule(t.TaskId, "integrity")).ToImmutableArray(),
            VerificationAuthority = TaskVerificationAuthority.TrustedLocalProcess,
            VerificationProfiles = [new("build", binding.DotnetExecutable, binding.DotnetExecutableSha256,
                ["build", "Fixture.csproj", "--configuration", "Release", "--verbosity", "quiet"], TimeSpan.FromMinutes(2)) { MaxOutputBytes = 8192 },
                new("tests", binding.DotnetExecutable, binding.DotnetExecutableSha256,
                ["run", "--project", "Fixture.csproj", "--configuration", "Release", "--no-launch-profile", "--", "results.trx"], TimeSpan.FromMinutes(2))
                { Kind = TaskVerificationKind.Tests, TestResultsPath = "results.trx", MaxOutputBytes = 8192 }],
            CommandVerificationRules = [new(graph.Tasks[^1].TaskId, "build", "build"), new(graph.Tasks[^1].TaskId, "tests", "tests")]
        };

    private static void ValidateGraph(TaskGraphCheckpoint checkpoint, WorkflowTaskGraph graph, TaskGraphAgentOptions options, CallerIdentity caller)
    {
        var expected = AgentTaskBindingValidator.Validate(graph, caller, new TaskGraphAgentBinding(
            AgentTaskIdentity.RootRunId(graph.GraphId).Value, options.SourceWorkspaceRoot, options.BaseRevision,
            options.WorkspaceStoreRoot, options.Provider, options.Model, options.RootToolPolicy, options.SharedBudget,
            options.AgentId, options.SystemPrompt, options.MaxRounds, options.MaxOutputTokens) {
            ArtifactVerificationRules = options.ArtifactVerificationRules, DependencyMode = options.DependencyMode,
            VerificationAuthority = options.VerificationAuthority, VerificationProfiles = options.VerificationProfiles,
            CommandVerificationRules = options.CommandVerificationRules });
        if (checkpoint.Caller.Id != caller.Id || checkpoint.Caller.IsOwner != caller.IsOwner || checkpoint.AgentBinding is null ||
            TaskGraphDocumentJson.Serialize(checkpoint.Graph) != TaskGraphDocumentJson.Serialize(graph) ||
            checkpoint.MaxConcurrency != (graph.Tasks.Length == 1 ? 1 : 2) ||
            TaskGraphDocumentJson.Serialize(checkpoint.AgentBinding) != TaskGraphDocumentJson.Serialize(expected))
            throw new WorkflowConflictException("The existing trial graph differs from the immutable campaign binding.");
    }

    private static LiveEvaluationBinding Binding(LiveEvaluationCheckpoint checkpoint, string output)
    {
        var binding = TaskGraphDocumentJson.Parse<LiveEvaluationBinding>(checkpoint.Definition.BindingJson);
        binding.Settings.RequireEnabled();
        if (binding.ProtocolVersion != LiveEvaluationCorpus.Protocol || binding.OutputDirectory != RequireOutput(output) ||
            checkpoint.DefinitionFingerprint != LiveEvaluationIdentity.Fingerprint(checkpoint.Definition) ||
            checkpoint.Definition.Provider != binding.Settings.Provider || checkpoint.Definition.Model != binding.Settings.Model ||
            checkpoint.Definition.Currency != binding.Settings.Currency || checkpoint.Definition.MaxTotalSpend != binding.Settings.MaxTotalSpend ||
            binding.DotnetExecutable != Path.GetFullPath(binding.DotnetExecutable) ||
            TaskGraphDocumentJson.Serialize(binding.Schedule) != TaskGraphDocumentJson.Serialize(LiveEvaluationCorpus.Schedule(checkpoint.Definition.CampaignId, binding.Settings)) ||
            binding.Cases.Length != 3 || binding.Cases.Select(c => c.CaseId).Distinct().Count() != 3 ||
            binding.Cases.Select(c => c.SdkVersion).Distinct().Count() != 1 ||
            binding.Cases.Any(c => c.SourceDirectory != Path.Combine(binding.OutputDirectory, "source", c.CaseId) || c.InputDigest != LiveEvaluationCorpus.InputDigest(c.CaseId, c.SdkVersion)))
            throw new WorkflowConflictException("The evaluation protocol, output directory or immutable configuration changed.");
        return binding;
    }

    private static string TrialFingerprint(LiveEvaluationCheckpoint checkpoint, LiveEvaluationScheduledTrial trial, LiveEvaluationCaseBinding fixture) =>
        LiveEvaluationIdentity.Digest(TaskGraphDocumentJson.Serialize(new { checkpoint.DefinitionFingerprint, trial, fixture }));

    private static LiveEvaluationCampaignReport Report(LiveEvaluationCheckpoint checkpoint, LiveEvaluationBinding binding)
    {
        var trials = checkpoint.Trials.Select(t => {
            var scheduled = binding.Schedule.Single(s => s.TrialId == t.TrialId && s.Strategy == t.Strategy);
            var fixture = binding.Cases.Single(c => c.CaseId == scheduled.CaseId);
            var observation = TaskGraphDocumentJson.Parse<LiveEvaluationTrialObservation>(t.ResultJson);
            if (t.BindingFingerprint != TrialFingerprint(checkpoint, scheduled, fixture) || observation.TrialId != scheduled.TrialId ||
                observation.Strategy != scheduled.Strategy || observation.GraphId != scheduled.GraphId || observation.BaseCommit != fixture.BaseCommit || observation.InputDigest != fixture.InputDigest)
                throw new WorkflowConflictException("A saved trial does not match its frozen workload and strategy.");
            return observation;
        }).ToImmutableArray();
        var status = checkpoint.NeedsReconciliation ? "NeedsReconciliation" : checkpoint.HasReservationOverrun ? "ReservationAssumptionsExceeded"
            : checkpoint.Control == WorkflowControl.Cancel ? "Cancelled" : checkpoint.Control == WorkflowControl.Pause ? "Paused"
            : trials.Length == binding.Schedule.Length ? "Completed" : checkpoint.RemainingSpend == 0 || trials.Any(t => t.Status == TaskGraphRunStatus.BudgetExhausted) ? "BudgetExhausted" : "Incomplete";
        var summaries = new[] { "graph", "direct" }.Select(strategy => {
            var group = trials.Where(t => t.Strategy == strategy).ToArray();
            var times = group.Select(t => t.ElapsedWallMilliseconds).Order().ToArray();
            double Quantile(double p) => times.Length == 0 ? 0 : times[(int)Math.Ceiling(p * times.Length) - 1];
            return new LiveEvaluationStrategySummary(strategy, group.Length, group.Count(t => t.VerifiedCompletion), group.Sum(t => t.EstimatedSpend), Quantile(.5), Quantile(.95));
        }).ToImmutableArray();
        return new(checkpoint.Definition.CampaignId, status, "NOT_ESTABLISHED", checkpoint, trials, summaries,
            ["Costs are estimates under declared rates and token ceilings; external provider billing has not been reconciled.",
             "This corpus contains three variants of one small coding/documentation workflow; it does not establish general orchestration quality.",
             "No independent human quality review or statistical superiority qualification has been recorded.",
             "The seed fixes strategy order only. Provider sampling and SDK/provider cache conditions are uncontrolled and no reproducibility of live outputs is asserted.",
             "The installed SDK version is pinned with roll-forward disabled and the launcher is hashed; transitive runtime/toolchain files and server-resolved model revisions are not independently attested.",
             "Latency includes model/tool/verification work and recovery downtime from durable graph admission; setup is excluded.",
             "All trials and failures are retained. A completed campaign describes execution, not promotion or live baseline qualification.",
             "Verification executes generated code with the local owner's OS authority; isolated workspaces are not a process sandbox."]);
    }

    private async Task<LiveEvaluationCheckpoint> RequireAsync(string campaignId, CallerIdentity caller, CancellationToken ct) =>
        await _evaluations.GetEvaluationAsync(campaignId, caller, ct) ?? throw new ArgumentException("The live evaluation campaign was not found.", nameof(campaignId));

    private static void RequireExecution(CallerIdentity caller, bool allowLocalVerification)
    {
        if (!caller.IsOwner) throw new UnauthorizedAccessException("Live evaluation requires the local owner.");
        if (!allowLocalVerification) throw new UnauthorizedAccessException("Live evaluation requires explicit trusted local process verification authority.");
    }

    private static string RequireOutput(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Evaluation output must be an absolute directory.", nameof(path));
        var result = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var cwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.CurrentDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (result == Path.GetPathRoot(result) || result.Equals(cwd, comparison) || result.StartsWith(cwd + Path.DirectorySeparatorChar, comparison) || cwd.StartsWith(result + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Evaluation output must be separate from the current project checkout.", nameof(path));
        for (var current = new DirectoryInfo(result); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Evaluation output cannot traverse filesystem links.", nameof(path));
        return result;
    }

    private static string FindDotnet()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new InvalidOperationException("Live evaluation requires a .NET 10 SDK on PATH.");
    }
    private static string ExecutableDigest(string path) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static async Task SaveReportAsync(LiveEvaluationCampaignReport report, string output, CancellationToken ct)
    {
        var pending = Path.Combine(output, "report-" + Guid.NewGuid().ToString("n") + ".tmp");
        await File.WriteAllTextAsync(pending, TaskGraphDocumentJson.Serialize(report), ct);
        File.Move(pending, Path.Combine(output, "report.json"), overwrite: true);
    }
}
