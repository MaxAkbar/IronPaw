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
    StoreBackend Store,
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
    int? MaxExecutionSeconds = null)
{
    public static CliOptions Parse(string[] args)
    {
        var provider = "local";
        string? model = null;
        var session = "default";
        string? prompt = null;
        string? promptFile = null;
        var profile = ToolProfile.Coding;
        var store = StoreBackend.Json;
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

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
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

                case "--store":
                    var storeValue = ReadValue(args, ref i, arg);
                    if (!Enum.TryParse<StoreBackend>(storeValue, ignoreCase: true, out store))
                    {
                        throw new ArgumentException($"Invalid --store value '{storeValue}'. Use 'json' or 'sqlite'.");
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
            store,
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
            maxExecutionSeconds);
    }

    public bool TryValidate(out string? error)
    {
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
        table.AddRow("-p, --provider <name>", "Provider: local | openai | ollama | lmstudio | anthropic (default: local)");
        table.AddRow("-m, --model <id>", "Model id (provider-specific default if omitted)");
        table.AddRow("-s, --session <key>", "Session key (default: default)");
        table.AddRow("--prompt <text>", "Prompt text");
        table.AddRow("--prompt-file <path>", "Read prompt from file");
        table.AddRow("--profile <name>", "Tool profile: Minimal | Coding | Messaging | Web | Full");
        table.AddRow("--auto-create-skills", "Automatically create runtime tools when needed (requires Full profile)");
        table.AddRow("--list-skills", "List discovered add-in skills and exit");
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
        table.AddRow("--store <backend>", "Storage backend: json | sqlite (default: json)");
        table.AddRow("--message-ttl-days <days>", "SQLite message retention window in days (default: 30)");
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
