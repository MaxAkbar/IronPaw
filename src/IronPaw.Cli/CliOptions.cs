using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using Spectre.Console;

namespace IronPaw.Cli;

internal sealed record CliOptions(
    string Provider,
    string? Model,
    string Session,
    string? Prompt,
    string? PromptFile,
    ToolProfile Profile,
    int MessageTtlDays,
    bool AutoCreateSkills,
    bool ListSkills,
    bool NoEvents,
    bool ShowHelp,
    int? LlmContextWindowTokens = null,
    int? LlmSafetyMarginTokens = null,
    int? LlmMinOutputTokensPerRequest = null,
    int? LlmMaxAutoContinuationPasses = null,
    string? ResumeRunId = null,
    bool ListWorkflows = false,
    string? ReconcileToolExecutionId = null,
    string? ReconcileResultFile = null,
    string? ReconcileNote = null,
    string? PauseRunId = null,
    string? CancelRunId = null,
    string? AnswerRequestId = null,
    string? Answer = null,
    string? ApproveRequestId = null,
    string? DenyRequestId = null,
    IReadOnlyCollection<string>? ApprovalRequiredTools = null,
    long? MaxTotalTokens = null,
    int? MaxExecutionSeconds = null,
    string? StartGraphFile = null,
    bool ListGraphs = false,
    string? ShowGraphId = null,
    string? ResumeGraphId = null,
    string? PauseGraphId = null,
    string? CancelGraphId = null,
    string? RecoverGraphId = null,
    string? ReconcileTaskGraphId = null,
    string? ReconcileCommandGraphId = null,
    string? ReconcileToolGraphId = null,
    string? GraphTaskId = null,
    string? VerificationInvocationId = null,
    string? GraphToolExecutionId = null,
    string? GraphResultFile = null,
    bool AllowLocalVerification = false,
    IReadOnlyCollection<string>? GraphBindingOverrides = null,
    string? ValidateEvaluationConfigFile = null,
    IReadOnlyCollection<string>? EvaluationConfigurationOverrides = null,
    string? RunEvaluationFile = null,
    string? ResumeEvaluationId = null,
    string? ShowEvaluationId = null,
    string? ReconcileEvaluationId = null,
    string? EvaluationOutputDirectory = null,
    string? EvaluationRequestId = null,
    IReadOnlyCollection<string>? EvaluationRuntimeOverrides = null,
    string? ReconcileEvaluationCommandId = null,
    string? ReconcileEvaluationTaskId = null,
    string? ReconcileEvaluationToolId = null,
    string? EvaluationGraphId = null,
    string? ConfigFile = null,
    string? ValidateConfigFile = null,
    IReadOnlyCollection<string>? ConfigurationOverrides = null)
{
    public static CliOptions Parse(string[] args)
    {
        if (args.Any(arg => arg == "--store" || arg.StartsWith("--store=", StringComparison.Ordinal)))
            throw new ArgumentException("--store has been removed. IronPaw now uses CSharpDB (application.cdb). JSON data imports automatically; SQLite files are preserved but are not imported. Remove --store and its value.");
        var provider = "local";
        string? model = null;
        var session = "default";
        string? prompt = null;
        string? promptFile = null;
        var profile = ToolProfile.Coding;
        var messageTtlDays = 30;
        var autoCreateSkills = false;
        var listSkills = false;
        var noEvents = false;
        var showHelp = false;
        int? llmContextWindowTokens = null;
        int? llmSafetyMarginTokens = null;
        int? llmMinOutputTokensPerRequest = null;
        int? llmMaxAutoContinuationPasses = null;
        string? resumeRunId = null;
        var listWorkflows = false;
        string? reconcileToolExecutionId = null;
        string? reconcileResultFile = null;
        string? reconcileNote = null;
        string? pauseRunId = null;
        string? cancelRunId = null;
        string? answerRequestId = null;
        string? answer = null;
        string? approveRequestId = null;
        string? denyRequestId = null;
        IReadOnlyCollection<string>? approvalRequiredTools = null;
        long? maxTotalTokens = null;
        int? maxExecutionSeconds = null;
        string? startGraphFile = null;
        var listGraphs = false;
        string? showGraphId = null;
        string? resumeGraphId = null;
        string? pauseGraphId = null;
        string? cancelGraphId = null;
        string? recoverGraphId = null;
        string? reconcileTaskGraphId = null;
        string? reconcileCommandGraphId = null;
        string? reconcileToolGraphId = null;
        string? graphTaskId = null;
        string? verificationInvocationId = null;
        string? graphToolExecutionId = null;
        string? graphResultFile = null;
        var allowLocalVerification = false;
        var graphBindingOverrides = new List<string>();
        string? validateEvaluationConfigFile = null;
        var evaluationConfigurationOverrides = new List<string>();
        string? runEvaluationFile = null;
        string? resumeEvaluationId = null;
        string? showEvaluationId = null;
        string? reconcileEvaluationId = null;
        string? evaluationOutputDirectory = null;
        string? evaluationRequestId = null;
        var evaluationRuntimeOverrides = new List<string>();
        string? reconcileEvaluationCommandId = null;
        string? reconcileEvaluationTaskId = null;
        string? reconcileEvaluationToolId = null;
        string? evaluationGraphId = null;
        string? configFile = null;
        string? validateConfigFile = null;
        var configurationOverrides = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("-", StringComparison.Ordinal) && arg is not "--validate-config" and not "-h" and not "--help")
                configurationOverrides.Add(arg);
            if (arg.StartsWith("-", StringComparison.Ordinal) && arg is not "--validate-evaluation-config" and not "-h" and not "--help")
                evaluationConfigurationOverrides.Add(arg);
            if (arg.StartsWith("-", StringComparison.Ordinal) && arg is not "--run-evaluation" and not
                "--resume-evaluation" and not "--show-evaluation" and not "--reconcile-evaluation" and not
                "--reconcile-evaluation-command" and not "--reconcile-evaluation-task" and not "--reconcile-evaluation-tool" and not
                "--evaluation-output" and not "--request" and not "--result-file" and not "--reconcile-note" and not
                "--allow-local-verification" and not "--evaluation-graph" and not "--task" and not "--invocation" and not
                "--execution" and not "--tool-execution" and not "-h" and not "--help")
                evaluationRuntimeOverrides.Add(arg);
            if (arg is "--provider" or "-p" or "--model" or "-m" or "--session" or "-s" or "--profile" or
                "--max-total-tokens" or "--max-execution-seconds" or "--config" || arg.StartsWith("--llm-", StringComparison.Ordinal))
                graphBindingOverrides.Add(arg);
            switch (arg)
            {
                case "--config":
                    configFile = ReadValue(args, ref i, arg);
                    break;
                case "--validate-config":
                    validateConfigFile = ReadValue(args, ref i, arg);
                    break;
                case "-h":
                case "--help":
                    showHelp = true;
                    break;

                case "--provider":
                case "-p":
                    provider = ReadValue(args, ref i, arg);
                    break;

                case "--model":
                case "-m":
                    model = ReadValue(args, ref i, arg);
                    break;

                case "--session":
                case "-s":
                    session = ReadValue(args, ref i, arg);
                    break;

                case "--prompt":
                    prompt = ReadValue(args, ref i, arg);
                    break;

                case "--prompt-file":
                    promptFile = ReadValue(args, ref i, arg);
                    break;

                case "--profile":
                    var profileValue = ReadValue(args, ref i, arg);
                    if (!Enum.TryParse<ToolProfile>(profileValue, ignoreCase: true, out profile))
                    {
                        throw new ArgumentException($"Invalid --profile value '{profileValue}'.");
                    }

                    break;

                case "--message-ttl-days":
                    var ttlValue = ReadValue(args, ref i, arg);
                    if (!int.TryParse(ttlValue, out messageTtlDays) || messageTtlDays <= 0)
                    {
                        throw new ArgumentException($"Invalid --message-ttl-days value '{ttlValue}'. Use a positive integer.");
                    }

                    break;

                case "--llm-context-window-tokens":
                    llmContextWindowTokens = ReadIntOption(args, ref i, arg, minInclusive: 1);
                    break;

                case "--llm-safety-margin-tokens":
                    llmSafetyMarginTokens = ReadIntOption(args, ref i, arg, minInclusive: 0);
                    break;

                case "--llm-min-output-tokens-per-request":
                    llmMinOutputTokensPerRequest = ReadIntOption(args, ref i, arg, minInclusive: 1);
                    break;

                case "--llm-max-auto-continuation-passes":
                    llmMaxAutoContinuationPasses = ReadIntOption(args, ref i, arg, minInclusive: 0);
                    break;

                case "--max-total-tokens":
                    maxTotalTokens = ReadPositiveLongOption(args, ref i, arg);
                    break;

                case "--max-execution-seconds":
                    maxExecutionSeconds = ReadIntOption(args, ref i, arg, minInclusive: 1);
                    break;

                case "--auto-create-skills":
                    autoCreateSkills = true;
                    break;

                case "--no-events":
                    noEvents = true;
                    break;

                case "--list-skills":
                    listSkills = true;
                    break;

                case "--validate-evaluation-config":
                    if (validateEvaluationConfigFile is not null)
                        throw new ArgumentException("Use --validate-evaluation-config only once.");
                    validateEvaluationConfigFile = ReadValue(args, ref i, arg);
                    break;

                case "--run-evaluation":
                    if (runEvaluationFile is not null) throw new ArgumentException("Use --run-evaluation only once.");
                    runEvaluationFile = ReadValue(args, ref i, arg);
                    break;
                case "--resume-evaluation":
                    if (resumeEvaluationId is not null) throw new ArgumentException("Use --resume-evaluation only once.");
                    resumeEvaluationId = ReadValue(args, ref i, arg);
                    break;
                case "--show-evaluation":
                    if (showEvaluationId is not null) throw new ArgumentException("Use --show-evaluation only once.");
                    showEvaluationId = ReadValue(args, ref i, arg);
                    break;
                case "--reconcile-evaluation":
                    if (reconcileEvaluationId is not null) throw new ArgumentException("Use --reconcile-evaluation only once.");
                    reconcileEvaluationId = ReadValue(args, ref i, arg);
                    break;
                case "--reconcile-evaluation-command":
                    if (reconcileEvaluationCommandId is not null) throw new ArgumentException("Use --reconcile-evaluation-command only once.");
                    reconcileEvaluationCommandId = ReadValue(args, ref i, arg);
                    break;
                case "--reconcile-evaluation-task":
                    if (reconcileEvaluationTaskId is not null) throw new ArgumentException("Use --reconcile-evaluation-task only once.");
                    reconcileEvaluationTaskId = ReadValue(args, ref i, arg);
                    break;
                case "--reconcile-evaluation-tool":
                    if (reconcileEvaluationToolId is not null) throw new ArgumentException("Use --reconcile-evaluation-tool only once.");
                    reconcileEvaluationToolId = ReadValue(args, ref i, arg);
                    break;
                case "--evaluation-graph":
                    if (evaluationGraphId is not null) throw new ArgumentException("Use --evaluation-graph only once.");
                    evaluationGraphId = ReadValue(args, ref i, arg);
                    break;
                case "--evaluation-output":
                    if (evaluationOutputDirectory is not null) throw new ArgumentException("Use --evaluation-output only once.");
                    evaluationOutputDirectory = ReadValue(args, ref i, arg);
                    break;
                case "--request":
                    if (evaluationRequestId is not null) throw new ArgumentException("Use --request only once.");
                    evaluationRequestId = ReadValue(args, ref i, arg);
                    break;

                case "--start-graph":
                    startGraphFile = ReadValue(args, ref i, arg);
                    break;
                case "--list-graphs":
                    listGraphs = true;
                    break;
                case "--show-graph":
                    showGraphId = ReadValue(args, ref i, arg);
                    break;
                case "--resume-graph":
                    resumeGraphId = ReadValue(args, ref i, arg);
                    break;
                case "--pause-graph":
                    pauseGraphId = ReadValue(args, ref i, arg);
                    break;
                case "--cancel-graph":
                    cancelGraphId = ReadValue(args, ref i, arg);
                    break;
                case "--recover-graph":
                    recoverGraphId = ReadValue(args, ref i, arg);
                    break;
                case "--reconcile-graph-task":
                    reconcileTaskGraphId = ReadValue(args, ref i, arg);
                    break;
                case "--reconcile-graph-command":
                    reconcileCommandGraphId = ReadValue(args, ref i, arg);
                    break;
                case "--reconcile-graph-tool":
                    reconcileToolGraphId = ReadValue(args, ref i, arg);
                    break;
                case "--execution":
                case "--tool-execution":
                    graphToolExecutionId = ReadValue(args, ref i, arg);
                    break;
                case "--task":
                    graphTaskId = ReadValue(args, ref i, arg);
                    break;
                case "--invocation":
                    verificationInvocationId = ReadValue(args, ref i, arg);
                    break;
                case "--result-file":
                    graphResultFile = ReadValue(args, ref i, arg);
                    break;
                case "--allow-local-verification":
                    allowLocalVerification = true;
                    break;
                case "--resume":
                    resumeRunId = ReadValue(args, ref i, arg);
                    break;

                case "--list-workflows":
                    listWorkflows = true;
                    break;

                case "--pause":
                    pauseRunId = ReadValue(args, ref i, arg);
                    break;

                case "--cancel":
                    cancelRunId = ReadValue(args, ref i, arg);
                    break;

                case "--answer-request":
                    answerRequestId = ReadValue(args, ref i, arg);
                    break;

                case "--answer":
                    answer = ReadValue(args, ref i, arg);
                    break;

                case "--approve-request":
                    approveRequestId = ReadValue(args, ref i, arg);
                    break;

                case "--deny-request":
                    denyRequestId = ReadValue(args, ref i, arg);
                    break;

                case "--require-approval":
                    approvalRequiredTools = ReadValue(args, ref i, arg).Split(',').Select(name => name.Trim()).ToArray();
                    break;

                case "--reconcile-tool":
                    reconcileToolExecutionId = ReadValue(args, ref i, arg);
                    break;

                case "--reconcile-result":
                    reconcileResultFile = ReadValue(args, ref i, arg);
                    break;

                case "--reconcile-note":
                    reconcileNote = ReadValue(args, ref i, arg);
                    break;

                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option '{arg}'.");
                    }

                    prompt = string.IsNullOrWhiteSpace(prompt)
                        ? arg
                        : $"{prompt} {arg}";
                    break;
            }
        }

        return new CliOptions(
            provider,
            model,
            session,
            prompt,
            promptFile,
            profile,
            messageTtlDays,
            autoCreateSkills,
            listSkills,
            noEvents,
            showHelp,
            llmContextWindowTokens,
            llmSafetyMarginTokens,
            llmMinOutputTokensPerRequest,
            llmMaxAutoContinuationPasses,
            resumeRunId,
            listWorkflows,
            reconcileToolExecutionId,
            reconcileResultFile,
            reconcileNote,
            pauseRunId,
            cancelRunId,
            answerRequestId,
            answer,
            approveRequestId,
            denyRequestId,
            approvalRequiredTools,
            maxTotalTokens,
            maxExecutionSeconds,
            startGraphFile, listGraphs, showGraphId, resumeGraphId, pauseGraphId, cancelGraphId,
            recoverGraphId, reconcileTaskGraphId, reconcileCommandGraphId, reconcileToolGraphId, graphTaskId,
            verificationInvocationId, graphToolExecutionId, graphResultFile, allowLocalVerification, graphBindingOverrides,
            validateEvaluationConfigFile, evaluationConfigurationOverrides,
            runEvaluationFile, resumeEvaluationId, showEvaluationId, reconcileEvaluationId,
            evaluationOutputDirectory, evaluationRequestId, evaluationRuntimeOverrides,
            reconcileEvaluationCommandId, reconcileEvaluationTaskId, reconcileEvaluationToolId, evaluationGraphId,
            configFile, validateConfigFile, configurationOverrides);
    }

    public bool TryValidate(out string? error)
    {
        if (ValidateConfigFile is not null)
        {
            if (string.IsNullOrWhiteSpace(ValidateConfigFile) || ConfigFile is not null || ConfigurationOverrides?.Count > 0)
            {
                error = "--validate-config requires a nonempty settings file path and cannot be combined with other commands or overrides.";
                return false;
            }
            error = null;
            return true;
        }
        if (ConfigFile is not null && (string.IsNullOrWhiteSpace(ConfigFile) || ResumeRunId is not null || ListWorkflows || PauseRunId is not null || CancelRunId is not null))
        {
            error = "--config requires a nonempty path and applies only to new runs; saved workflows retain their settings.";
            return false;
        }
        if (EvaluationCommandCount > 0 || EvaluationOutputDirectory is not null || EvaluationRequestId is not null || EvaluationGraphId is not null)
            return TryValidateEvaluationRuntime(out error);
        if (ValidateEvaluationConfigFile is not null)
            return TryValidateEvaluationConfiguration(out error);
        if (GraphCommandCount > 0 || GraphTaskId is not null || VerificationInvocationId is not null || GraphToolExecutionId is not null ||
            GraphResultFile is not null || AllowLocalVerification)
            return TryValidateGraph(out error);
        if ((ResumeRunId is not null && string.IsNullOrWhiteSpace(ResumeRunId)) ||
            (PauseRunId is not null && string.IsNullOrWhiteSpace(PauseRunId)) ||
            (CancelRunId is not null && string.IsNullOrWhiteSpace(CancelRunId)))
        {
            error = "--resume, --pause, and --cancel require a workflow run ID.";
            return false;
        }

        var workflowCommandCount = (ListWorkflows ? 1 : 0) + (ResumeRunId is not null ? 1 : 0) +
            (PauseRunId is not null ? 1 : 0) + (CancelRunId is not null ? 1 : 0);
        if (workflowCommandCount > 1)
        {
            error = "Use only one of --list-workflows, --resume, --pause, or --cancel.";
            return false;
        }

        if (workflowCommandCount > 0 && (MaxTotalTokens is not null || MaxExecutionSeconds is not null))
        {
            error = "--max-total-tokens and --max-execution-seconds apply only to new workflows. Saved limits cannot change during recovery.";
            return false;
        }

        if (MaxTotalTokens is <= 0 || MaxExecutionSeconds is <= 0)
        {
            error = "--max-total-tokens and --max-execution-seconds must be positive integers.";
            return false;
        }

        if (workflowCommandCount > 0 &&
            (!string.IsNullOrWhiteSpace(Prompt) || !string.IsNullOrWhiteSpace(PromptFile) || ListSkills ||
             ApprovalRequiredTools is not null || AutoCreateSkills))
        {
            error = "Workflow commands cannot be combined with a prompt, --list-skills, --auto-create-skills, or --require-approval. Saved permissions remain in effect.";
            return false;
        }

        var hasReconciliation = ReconcileToolExecutionId is not null || ReconcileResultFile is not null || ReconcileNote is not null;
        var hasAnswer = AnswerRequestId is not null || Answer is not null;
        if (hasAnswer && (ResumeRunId is null || string.IsNullOrWhiteSpace(AnswerRequestId) || string.IsNullOrWhiteSpace(Answer)))
        {
            error = "Answering requires --resume, --answer-request, and a nonempty --answer.";
            return false;
        }

        if ((ApproveRequestId is not null || DenyRequestId is not null) &&
            (ResumeRunId is null || (ApproveRequestId is not null && string.IsNullOrWhiteSpace(ApproveRequestId)) ||
             (DenyRequestId is not null && string.IsNullOrWhiteSpace(DenyRequestId))))
        {
            error = "Approval decisions require --resume and a nonempty --approve-request or --deny-request ID.";
            return false;
        }

        var responseCount = (hasAnswer ? 1 : 0) + (ApproveRequestId is not null ? 1 : 0) +
            (DenyRequestId is not null ? 1 : 0) + (hasReconciliation ? 1 : 0);
        if (responseCount > 1)
        {
            error = "Supply only one answer, approval decision, or tool reconciliation per resume command.";
            return false;
        }

        if (hasReconciliation && (ResumeRunId is null || string.IsNullOrWhiteSpace(ReconcileToolExecutionId) ||
            string.IsNullOrWhiteSpace(ReconcileResultFile) || string.IsNullOrWhiteSpace(ReconcileNote)))
        {
            error = "Reconciliation requires --resume, --reconcile-tool, --reconcile-result, and --reconcile-note.";
            return false;
        }

        if (ApprovalRequiredTools is not null && (ApprovalRequiredTools.Count == 0 ||
            ApprovalRequiredTools.Any(string.IsNullOrWhiteSpace) ||
            ApprovalRequiredTools.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ApprovalRequiredTools.Count))
        {
            error = "--require-approval requires distinct, nonempty tool names separated by commas.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Prompt) && !string.IsNullOrWhiteSpace(PromptFile))
        {
            error = "Use either --prompt or --prompt-file, not both.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Provider))
        {
            error = "--provider cannot be empty.";
            return false;
        }

        if (AutoCreateSkills && Profile != ToolProfile.Full)
        {
            error = "--auto-create-skills requires --profile Full.";
            return false;
        }

        if (MessageTtlDays <= 0)
        {
            error = "--message-ttl-days must be a positive integer.";
            return false;
        }

        if (LlmContextWindowTokens.HasValue && LlmContextWindowTokens.Value <= 0)
        {
            error = "--llm-context-window-tokens must be a positive integer.";
            return false;
        }

        if (LlmSafetyMarginTokens.HasValue && LlmSafetyMarginTokens.Value < 0)
        {
            error = "--llm-safety-margin-tokens must be zero or a positive integer.";
            return false;
        }

        if (LlmMinOutputTokensPerRequest.HasValue && LlmMinOutputTokensPerRequest.Value <= 0)
        {
            error = "--llm-min-output-tokens-per-request must be a positive integer.";
            return false;
        }

        if (LlmMaxAutoContinuationPasses.HasValue && LlmMaxAutoContinuationPasses.Value < 0)
        {
            error = "--llm-max-auto-continuation-passes must be zero or a positive integer.";
            return false;
        }

        error = null;
        return true;
    }

    internal CliOptions WithSettings(IronPawSettings settings)
    {
        settings = settings.RequireValid();
        var providerExplicit = GraphBindingOverrides?.Any(flag => flag is "--provider" or "-p") == true;
        var llm = new RuntimeLlmSettings {
            ContextWindowTokens = LlmContextWindowTokens ?? settings.Llm.ContextWindowTokens,
            SafetyMarginTokens = LlmSafetyMarginTokens ?? settings.Llm.SafetyMarginTokens,
            MinimumOutputTokensPerRequest = LlmMinOutputTokensPerRequest ?? settings.Llm.MinimumOutputTokensPerRequest,
            MaxAutoContinuationPasses = LlmMaxAutoContinuationPasses ?? settings.Llm.MaxAutoContinuationPasses };
        (settings with { Llm = llm }).RequireValid();
        return this with {
            Provider = providerExplicit ? Provider : settings.Provider,
            Model = Model ?? (providerExplicit && !Provider.Equals(settings.Provider, StringComparison.OrdinalIgnoreCase) ? null : settings.Model),
            MaxTotalTokens = MaxTotalTokens ?? settings.MaxTotalTokens,
            MaxExecutionSeconds = MaxExecutionSeconds ?? settings.MaxExecutionSeconds,
            LlmContextWindowTokens = llm.ContextWindowTokens,
            LlmSafetyMarginTokens = llm.SafetyMarginTokens,
            LlmMinOutputTokensPerRequest = llm.MinimumOutputTokensPerRequest,
            LlmMaxAutoContinuationPasses = llm.MaxAutoContinuationPasses
        };
    }

    private int EvaluationCommandCount => (RunEvaluationFile is not null ? 1 : 0) +
        (ResumeEvaluationId is not null ? 1 : 0) + (ShowEvaluationId is not null ? 1 : 0) +
        (ReconcileEvaluationId is not null ? 1 : 0) + (ReconcileEvaluationCommandId is not null ? 1 : 0) +
        (ReconcileEvaluationTaskId is not null ? 1 : 0) + (ReconcileEvaluationToolId is not null ? 1 : 0);

    private bool TryValidateEvaluationRuntime(out string? error)
    {
        if (EvaluationCommandCount != 1 || EvaluationRuntimeOverrides?.Count > 0 ||
            ValidateEvaluationConfigFile is not null || GraphCommandCount > 0 ||
            WorkflowCommands.IsWorkflowCommand(this) || Prompt is not null || PromptFile is not null ||
            Provider != "local" || Model is not null || Session != "default" || Profile != ToolProfile.Coding ||
            MessageTtlDays != 30 || ListSkills || AutoCreateSkills || NoEvents ||
            MaxTotalTokens is not null || MaxExecutionSeconds is not null ||
            LlmContextWindowTokens is not null || LlmSafetyMarginTokens is not null ||
            LlmMinOutputTokensPerRequest is not null || LlmMaxAutoContinuationPasses is not null ||
            AnswerRequestId is not null || Answer is not null || ApproveRequestId is not null || DenyRequestId is not null ||
            ReconcileToolExecutionId is not null || ReconcileResultFile is not null || ApprovalRequiredTools is not null)
        {
            error = "Use exactly one evaluation command without prompts, workflow or graph commands, or runtime overrides; saved settings and prices remain in effect.";
            return false;
        }
        if (new[] { RunEvaluationFile, ResumeEvaluationId, ShowEvaluationId, ReconcileEvaluationId,
            ReconcileEvaluationCommandId, ReconcileEvaluationTaskId, ReconcileEvaluationToolId }
            .Any(value => value is not null && string.IsNullOrWhiteSpace(value)))
        {
            error = "Evaluation commands require a nonempty configuration path or campaign ID.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(EvaluationOutputDirectory) || !Path.IsPathFullyQualified(EvaluationOutputDirectory))
        {
            error = "Evaluation commands require --evaluation-output with an absolute campaign directory.";
            return false;
        }
        var executes = RunEvaluationFile is not null || ResumeEvaluationId is not null;
        if (executes != AllowLocalVerification)
        {
            error = "--run-evaluation and --resume-evaluation require --allow-local-verification; read-only and reconciliation commands do not grant process authority.";
            return false;
        }
        var graphReconcile = ReconcileEvaluationCommandId is not null || ReconcileEvaluationTaskId is not null || ReconcileEvaluationToolId is not null;
        var reconciles = ReconcileEvaluationId is not null || graphReconcile;
        if ((ReconcileEvaluationId is not null && string.IsNullOrWhiteSpace(EvaluationRequestId)) ||
            (ReconcileEvaluationId is null && EvaluationRequestId is not null) ||
            (reconciles && (string.IsNullOrWhiteSpace(GraphResultFile) || string.IsNullOrWhiteSpace(ReconcileNote))) ||
            (!reconciles && (GraphResultFile is not null || ReconcileNote is not null)))
        {
            error = "Evaluation reconciliation requires --result-file and a nonempty --reconcile-note; --request applies only to billing reconciliation.";
            return false;
        }
        if ((graphReconcile && (string.IsNullOrWhiteSpace(EvaluationGraphId) || string.IsNullOrWhiteSpace(GraphTaskId))) ||
            (!graphReconcile && (EvaluationGraphId is not null || GraphTaskId is not null)))
        {
            error = "Evaluation graph reconciliation requires --evaluation-graph and --task; those flags apply only to graph reconciliation.";
            return false;
        }
        if ((ReconcileEvaluationCommandId is not null && string.IsNullOrWhiteSpace(VerificationInvocationId)) ||
            (ReconcileEvaluationCommandId is null && VerificationInvocationId is not null) ||
            (ReconcileEvaluationToolId is not null && string.IsNullOrWhiteSpace(GraphToolExecutionId)) ||
            (ReconcileEvaluationToolId is null && GraphToolExecutionId is not null))
        {
            error = "--invocation applies only to evaluation command reconciliation; --tool-execution applies only to evaluation tool reconciliation.";
            return false;
        }
        error = null;
        return true;
    }

    private bool TryValidateEvaluationConfiguration(out string? error)
    {
        if (string.IsNullOrWhiteSpace(ValidateEvaluationConfigFile))
        {
            error = "--validate-evaluation-config requires a nonempty configuration file path.";
            return false;
        }
        if (EvaluationConfigurationOverrides?.Count > 0 || GraphCommandCount > 0 ||
            Prompt is not null || PromptFile is not null || ListSkills || AutoCreateSkills ||
            Provider != "local" || Model is not null || Session != "default" || Profile != ToolProfile.Coding ||
            MessageTtlDays != 30 || NoEvents ||
            GraphBindingOverrides?.Count > 0 || MaxTotalTokens is not null || MaxExecutionSeconds is not null ||
            LlmContextWindowTokens is not null || LlmSafetyMarginTokens is not null ||
            LlmMinOutputTokensPerRequest is not null || LlmMaxAutoContinuationPasses is not null ||
            ResumeRunId is not null || ListWorkflows || PauseRunId is not null || CancelRunId is not null ||
            AnswerRequestId is not null || Answer is not null || ApproveRequestId is not null || DenyRequestId is not null ||
            ReconcileToolExecutionId is not null || ReconcileResultFile is not null || ReconcileNote is not null ||
            ApprovalRequiredTools is not null || GraphTaskId is not null || VerificationInvocationId is not null ||
            GraphToolExecutionId is not null || GraphResultFile is not null || AllowLocalVerification)
        {
            error = "--validate-evaluation-config is a standalone validation command; it cannot be combined with prompts, workflow or graph commands, or runtime overrides.";
            return false;
        }
        error = null;
        return true;
    }

    private int GraphCommandCount => (StartGraphFile is not null ? 1 : 0) + (ListGraphs ? 1 : 0) +
        (ShowGraphId is not null ? 1 : 0) + (ResumeGraphId is not null ? 1 : 0) +
        (PauseGraphId is not null ? 1 : 0) + (CancelGraphId is not null ? 1 : 0) +
        (RecoverGraphId is not null ? 1 : 0) + (ReconcileTaskGraphId is not null ? 1 : 0) +
        (ReconcileCommandGraphId is not null ? 1 : 0) + (ReconcileToolGraphId is not null ? 1 : 0);

    private bool TryValidateGraph(out string? error)
    {
        if (GraphCommandCount != 1 || WorkflowCommands.IsWorkflowCommand(this) || ListSkills || AutoCreateSkills ||
            Prompt is not null || PromptFile is not null || ApprovalRequiredTools is not null ||
            AnswerRequestId is not null || Answer is not null || ApproveRequestId is not null || DenyRequestId is not null ||
            ReconcileToolExecutionId is not null || ReconcileResultFile is not null)
        {
            error = "Use exactly one graph command without a prompt, workflow command, skill command, or workflow response.";
            return false;
        }
        if (new[] { StartGraphFile, ShowGraphId, ResumeGraphId, PauseGraphId, CancelGraphId, RecoverGraphId,
            ReconcileTaskGraphId, ReconcileCommandGraphId, ReconcileToolGraphId }.Any(value => value is not null && string.IsNullOrWhiteSpace(value)))
        {
            error = "Graph commands require a nonempty graph ID or launch document path.";
            return false;
        }
        if (GraphBindingOverrides?.Count > 0 || Provider != "local" || Model is not null || Session != "default" ||
            Profile != ToolProfile.Coding || MaxTotalTokens is not null || MaxExecutionSeconds is not null ||
            LlmContextWindowTokens is not null || LlmSafetyMarginTokens is not null ||
            LlmMinOutputTokensPerRequest is not null || LlmMaxAutoContinuationPasses is not null)
        {
            error = "Graph provider, model, session, permissions, and budgets come from the launch document or saved binding; graph commands do not accept overrides.";
            return false;
        }
        var reconcile = ReconcileTaskGraphId is not null || ReconcileCommandGraphId is not null || ReconcileToolGraphId is not null;
        var taskRequired = RecoverGraphId is not null || reconcile;
        if ((taskRequired && string.IsNullOrWhiteSpace(GraphTaskId)) || (!taskRequired && GraphTaskId is not null))
        {
            error = "--task is required only for --recover-graph and graph reconciliation commands.";
            return false;
        }
        if ((ReconcileCommandGraphId is not null && string.IsNullOrWhiteSpace(VerificationInvocationId)) ||
            (ReconcileCommandGraphId is null && VerificationInvocationId is not null))
        {
            error = "--invocation is required only for --reconcile-graph-command.";
            return false;
        }
        if ((ReconcileToolGraphId is not null && string.IsNullOrWhiteSpace(GraphToolExecutionId)) ||
            (ReconcileToolGraphId is null && GraphToolExecutionId is not null))
        {
            error = "--execution is required only for --reconcile-graph-tool.";
            return false;
        }
        if ((reconcile && (string.IsNullOrWhiteSpace(GraphResultFile) || string.IsNullOrWhiteSpace(ReconcileNote))) ||
            (!reconcile && (GraphResultFile is not null || ReconcileNote is not null)))
        {
            error = "Graph reconciliation requires --result-file and a nonempty --reconcile-note.";
            return false;
        }
        if (AllowLocalVerification && StartGraphFile is null && ResumeGraphId is null && RecoverGraphId is null)
        {
            error = "--allow-local-verification applies only to --start-graph, --resume-graph, or --recover-graph.";
            return false;
        }
        if (MessageTtlDays <= 0)
        {
            error = "--message-ttl-days must be a positive integer.";
            return false;
        }
        error = null;
        return true;
    }

    public async Task<string> ResolvePromptAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(Prompt))
        {
            return Prompt.Trim();
        }

        if (!string.IsNullOrWhiteSpace(PromptFile))
        {
            return await File.ReadAllTextAsync(PromptFile, ct);
        }

        if (Console.IsInputRedirected)
        {
            using var reader = Console.In;
            return await reader.ReadToEndAsync(ct);
        }

        return AnsiConsole.Prompt(new TextPrompt<string>("[green]Prompt[/]>").AllowEmpty());
    }

    public WorkflowBudget? ResolveWorkflowBudget() => MaxTotalTokens is null && MaxExecutionSeconds is null
        ? null
        : new WorkflowBudget(MaxTotalTokens: MaxTotalTokens,
            MaxExecutionTime: MaxExecutionSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);

    public static void PrintHelp()
    {
        var table = new Table()
            .RoundedBorder()
            .AddColumn("[bold]Option[/]")
            .AddColumn("[bold]Description[/]");

        table.AddRow("-h, --help", "Show help");
        table.AddRow("--config <path>", "Use a reviewed settings file for a new run (otherwise IRONPAW_CONFIG or shared user settings)");
        table.AddRow("--validate-config <path>", "Validate application settings without opening application state or contacting a provider");
        table.AddRow("-p, --provider <name>", "Provider: local | openai | ollama | lmstudio | anthropic (default: local)");
        table.AddRow("-m, --model <id>", "Model id (provider-specific default if omitted)");
        table.AddRow("-s, --session <key>", "Session key (default: default)");
        table.AddRow("--prompt <text>", "Prompt text");
        table.AddRow("--prompt-file <path>", "Read prompt from file");
        table.AddRow("--profile <name>", "Tool profile: Minimal | Coding | Messaging | Web | Full");
        table.AddRow("--auto-create-skills", "Automatically create runtime tools when needed (requires Full profile)");
        table.AddRow("--list-skills", "List discovered add-in skills and exit");
        table.AddRow("--validate-evaluation-config <path>", "Validate live-comparison settings without initializing providers or running evaluations");
        table.AddRow("--run-evaluation <path>", "Run configured paired trials; requires --evaluation-output and --allow-local-verification");
        table.AddRow("--resume-evaluation <campaign-id>", "Continue a saved campaign without replaying unknown billable requests");
        table.AddRow("--show-evaluation <campaign-id>", "Inspect a saved campaign without provider credentials");
        table.AddRow("--reconcile-evaluation <campaign-id>", "Record externally verified billing; requires --request, --result-file, and --reconcile-note");
        table.AddRow("--reconcile-evaluation-command <campaign-id>", "Reconcile an uncertain build/test; requires --evaluation-graph, --task, --invocation, --result-file, --reconcile-note");
        table.AddRow("--reconcile-evaluation-task <campaign-id>", "Reconcile an uncertain task; requires --evaluation-graph, --task, --result-file, --reconcile-note");
        table.AddRow("--reconcile-evaluation-tool <campaign-id>", "Reconcile an uncertain tool; also requires --tool-execution");
        table.AddRow("--evaluation-output <absolute-path>", "Isolated campaign directory for its CSharpDB ledger, workspaces, and report");
        table.AddRow("--start-graph <path>", "Accept and execute a JSON TaskGraphLaunchDocument");
        table.AddRow("--list-graphs", "List durable task graphs in this workspace without provider credentials");
        table.AddRow("--show-graph <graph-id>", "Show tasks, blockers, receipts, artifacts, budget, and journal");
        table.AddRow("--resume-graph <graph-id>", "Continue a graph with its saved provider, policy, and budgets");
        table.AddRow("--pause-graph <graph-id>", "Persist a resumable graph pause");
        table.AddRow("--cancel-graph <graph-id>", "Permanently cancel a graph");
        table.AddRow("--recover-graph <graph-id> --task <task-id>", "Explicitly recover an interrupted task without unknown effects");
        table.AddRow("--reconcile-graph-task <graph-id>", "Record externally verified task outcome; requires --task, --result-file, --reconcile-note");
        table.AddRow("--reconcile-graph-command <graph-id>", "Record externally verified command outcome; also requires --invocation");
        table.AddRow("--reconcile-graph-tool <graph-id>", "Record an externally verified child tool outcome; also requires --execution");
        table.AddRow("--allow-local-verification", "For graph start/resume/recover, grant trusted command profiles host OS authority");
        table.AddRow("--list-workflows", "List saved workflows and unresolved tool execution IDs for this workspace");
        table.AddRow("--resume <run-id>", "Resume a saved workflow using its original provider, model, session, and permissions");
        table.AddRow("--pause <run-id>", "Persist a resumable pause request without provider credentials");
        table.AddRow("--cancel <run-id>", "Permanently cancel a workflow without provider credentials");
        table.AddRow("--answer-request <request-id>", "With --resume, answer a saved human input request");
        table.AddRow("--answer <text>", "Text for --answer-request; recorded before resuming");
        table.AddRow("--approve-request <request-id>", "With --resume, approve the exact saved tool operation");
        table.AddRow("--deny-request <request-id>", "With --resume, deny the saved tool operation");
        table.AddRow("--require-approval <tool-names>", "For new runs, require approval for comma-separated tool names (default: none)");
        table.AddRow("--max-total-tokens <n>", "Persistent token limit shared by a new workflow and its descendants (default: unlimited)");
        table.AddRow("--max-execution-seconds <n>", "Persistent execution time limit for a new workflow (default: unlimited; paused/input waiting excluded)");
        table.AddRow("--reconcile-tool <execution-id>", "With --resume, record an externally verified tool outcome before resuming");
        table.AddRow("--reconcile-result <path>", "JSON ToolResult file containing the verified outcome (never executes the tool)");
        table.AddRow("--reconcile-note <text>", "Evidence explaining how the uncertain tool outcome was established");
        table.AddRow("--message-ttl-days <days>", "CSharpDB message retention window in days (default: 30)");
        table.AddRow("--llm-context-window-tokens <n>", "Override context window tokens for OpenAI-compatible providers");
        table.AddRow("--llm-safety-margin-tokens <n>", "Override reserved safety margin tokens for OpenAI-compatible providers");
        table.AddRow("--llm-min-output-tokens-per-request <n>", "Override minimum output tokens budgeted per provider request");
        table.AddRow("--llm-max-auto-continuation-passes <n>", "Override max auto-continue passes when truncated by length");
        table.AddRow("--no-events", "Hide event stream logs");

        AnsiConsole.Write(new Rule("[bold deepskyblue1]IronPaw CLI[/]").LeftJustified());
        AnsiConsole.MarkupLine("[bold]Usage[/]");
        AnsiConsole.Write(new Text("  dotnet run --project src/IronPaw.Cli -- [options]"));
        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Environment Variables[/]");
        AnsiConsole.MarkupLine("  [white]OPENAI_API_KEY[/]");
        AnsiConsole.MarkupLine("  [white]ANTHROPIC_API_KEY[/]");
        AnsiConsole.MarkupLine("  [white]OLLAMA_BASE_URL[/] (default: http://localhost:11434)");
        AnsiConsole.MarkupLine("  [white]OLLAMA_API_KEY[/] (optional, default: ollama)");
        AnsiConsole.MarkupLine("  [white]LMSTUDIO_BASE_URL[/] (default: http://localhost:1234)");
        AnsiConsole.MarkupLine("  [white]LMSTUDIO_API_KEY[/] (optional, default: lm-studio)");
        AnsiConsole.MarkupLine("  [white]IRONPAW_LLM_CONTEXT_WINDOW_TOKENS[/] (optional; OpenAI-compatible override)");
        AnsiConsole.MarkupLine("  [white]IRONPAW_LLM_SAFETY_MARGIN_TOKENS[/] (optional; OpenAI-compatible override)");
        AnsiConsole.MarkupLine("  [white]IRONPAW_LLM_MIN_OUTPUT_TOKENS_PER_REQUEST[/] (optional; OpenAI-compatible override)");
        AnsiConsole.MarkupLine("  [white]IRONPAW_LLM_MAX_AUTO_CONTINUATION_PASSES[/] (optional; OpenAI-compatible override)");
        AnsiConsole.MarkupLine("  [white]IRONPAW_CODEGEN_PROVIDER[/] (optional; specialist override)");
        AnsiConsole.MarkupLine("  [white]IRONPAW_CODEGEN_MODEL[/] (optional; specialist override)");
        AnsiConsole.MarkupLine("  [white]IRONPAW_REVIEWER_PROVIDER[/] (optional; specialist override)");
        AnsiConsole.MarkupLine("  [white]IRONPAW_REVIEWER_MODEL[/] (optional; specialist override)");
        AnsiConsole.MarkupLine("  [white]IRONPAW_WEB_BROWSER_PROVIDER[/] (optional; specialist override)");
        AnsiConsole.MarkupLine("  [white]IRONPAW_WEB_BROWSER_MODEL[/] (optional; specialist override)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Interactive Mode[/]");
        AnsiConsole.MarkupLine("  Launch without --prompt/--prompt-file to keep running until you type [white]quit[/].");
        AnsiConsole.MarkupLine("  Commands: [white]/help[/], [white]/settings[/], [white]/provider[/], [white]/model[/], [white]/session[/],");
        AnsiConsole.MarkupLine("           [white]/remember[/], [white]/forget[/], [white]/memories[/], [white]/scope-runs[/], [white]/agent-messages[/],");
        AnsiConsole.MarkupLine("           [white]/web-sessions[/],");
        AnsiConsole.MarkupLine("           [white]/tasks[/], [white]/review[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Add-in Skill Directories[/]");
        AnsiConsole.MarkupLine("  [white].\\addins\\skills[/]");
        AnsiConsole.MarkupLine("  [white]%LOCALAPPDATA%\\IronPaw\\skills[/]");
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }

    private static int ReadIntOption(string[] args, ref int index, string option, int minInclusive)
    {
        var value = ReadValue(args, ref index, option);
        if (!int.TryParse(value, out var parsedValue) || parsedValue < minInclusive)
        {
            throw new ArgumentException($"Invalid {option} value '{value}'. Use an integer >= {minInclusive}.");
        }

        return parsedValue;
    }

    private static long ReadPositiveLongOption(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        if (!long.TryParse(value, out var parsedValue) || parsedValue <= 0)
        {
            throw new ArgumentException($"Invalid {option} value '{value}'. Use a positive integer.");
        }

        return parsedValue;
    }
}
