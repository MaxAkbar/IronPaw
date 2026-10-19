using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Providers;
using IronPaw.Cli;
using IronPaw.AgentCore.Mcp;
using IronPaw.AgentCore.Browser;
using Spectre.Console;

if (args.FirstOrDefault() == "--smoke-test")
{
    try { CliOptions.RejectRemovedOptions(args); }
    catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
    if (args.Length != 2) { Console.Error.WriteLine("--smoke-test requires only a new evidence directory."); return 2; }
    return await ReleaseSmokeCommand.ExecuteAsync(args[1]);
}

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    ConsoleUi.RenderError(ex.Message);
    Console.WriteLine();
    CliOptions.PrintHelp();
    return 2;
}

if (options.ShowHelp)
{
    CliOptions.PrintHelp();
    return 0;
}

if (!options.TryValidate(out var validationError))
{
    ConsoleUi.RenderError(validationError ?? "Invalid options.");
    Console.WriteLine();
    CliOptions.PrintHelp();
    return 2;
}

if (options.ValidateEvaluationConfigFile is not null)
    return await EvaluationConfigurationCommands.ExecuteAsync(options, CancellationToken.None);

if (options.InspectMcpConfigFile is not null)
{
    try { using var review = McpHost.Load(options.InspectMcpConfigFile); Console.WriteLine(review.Review); return 0; }
    catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException or UnauthorizedAccessException)
    { ConsoleUi.RenderError("Invalid connector configuration: " + error.Message); return 2; }
}

if (options.InspectBrowserConfigFile is not null)
{
    try { Console.WriteLine(BrowserConfiguration.Load(options.InspectBrowserConfigFile).Review); return 0; }
    catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException or UnauthorizedAccessException)
    { ConsoleUi.RenderError("Invalid browser configuration: " + error.Message); return 2; }
}

if (options.ValidateConfigFile is not null)
{
    try
    {
        // Validate the file itself, independent of current environment defaults or provider credentials.
        (await TaskGraphDocumentJson.ReadAsync<IronPawSettings>(options.ValidateConfigFile, CancellationToken.None)).RequireValid();
        Console.WriteLine("Application settings valid. No application state was opened and no provider was called.");
        return 0;
    }
    catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException)
    {
        ConsoleUi.RenderError("Invalid application settings: " + error.Message);
        return 2;
    }
}

if (LiveEvaluationCommands.IsEvaluationCommand(options))
{
    using var evaluationCancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; evaluationCancellation.Cancel(); };
    return await LiveEvaluationCommands.ExecuteAsync(options, evaluationCancellation.Token);
}

var interactiveInputAvailable = !Console.IsInputRedirected;
IronPawSettings settings;
var savedCommand = TaskGraphCommands.IsGraphCommand(options) || WorkflowCommands.IsWorkflowCommand(options);
try
{
    // Inspection and saved recovery remain available even if current user settings are broken.
    settings = savedCommand ? new IronPawSettings() : IronPawSettingsLoader.Load(options.ConfigFile);
    if (!savedCommand) options = options.WithSettings(settings);
}
catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException)
{
    ConsoleUi.RenderError("Invalid application settings: " + error.Message);
    return 2;
}
IAgentEventSink eventSink = options.NoEvents
    ? NullAgentEventSink.Instance
    : new AnsiConsoleEventSink();
var llmOptionOverrides = new OpenAiClientOptionOverrides(
    ContextWindowTokens: options.LlmContextWindowTokens,
    SafetyMarginTokens: options.LlmSafetyMarginTokens,
    MinimumOutputTokensPerRequest: options.LlmMinOutputTokensPerRequest,
    MaxAutoContinuationPasses: options.LlmMaxAutoContinuationPasses);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    using var bootstrap = AppBootstrapper.Build(options.MessageTtlDays, llmOptionOverrides, settings: settings, legacyEnvironment: savedCommand, mcpConfigFile: options.McpConfigFile, browserConfigFile: options.BrowserConfigFile);
    if (bootstrap.Connectors is { } connectors) Console.WriteLine(connectors.Review);
    ConsoleUi.RenderBanner();
    if (TaskGraphCommands.IsGraphCommand(options))
    {
        return await TaskGraphCommands.ExecuteAsync(options, bootstrap.GraphController,
            configuredProvider =>
            {
                if (!ProviderResolver.TryResolve(configuredProvider, interactiveInputAvailable: false,
                        out var savedProvider, out var resolveQuitRequested))
                    return (false, resolveQuitRequested);
                var configured = ProviderResolver.TryEnsureConfiguration(savedProvider, interactiveInputAvailable,
                    out var configurationQuitRequested);
                return (configured, configurationQuitRequested);
            }, cancellation.Token);
    }
    if (WorkflowCommands.IsWorkflowCommand(options))
    {
        return await WorkflowCommands.ExecuteAsync(options, bootstrap.WorkflowStore, bootstrap.Orchestrator,
            eventSink, configuredProvider =>
            {
                if (!ProviderResolver.TryResolve(configuredProvider, interactiveInputAvailable: false,
                        out var savedProvider, out var resolveQuitRequested))
                {
                    return (false, resolveQuitRequested);
                }

                var configured = ProviderResolver.TryEnsureConfiguration(savedProvider, interactiveInputAvailable,
                    out var configurationQuitRequested);
                return (configured, configurationQuitRequested);
            }, cancellation.Token);
    }

    if (!ProviderResolver.TryResolve(options.Provider, interactiveInputAvailable, out var provider, out var providerQuitRequested))
    {
        return providerQuitRequested ? 0 : 2;
    }

    var model = ResolveModel(options.Model, provider);
    var sessionKey = new SessionKey(string.IsNullOrWhiteSpace(options.Session) ? "default" : options.Session.Trim());
    ConsoleUi.RenderRunSettings(
        provider,
        model,
        sessionKey.Value,
        options.Profile,
        options.NoEvents,
        bootstrap.SkillLoadResult.SkillsLoaded,
        ResolveLlmBudgetOptions(provider, llmOptionOverrides));
    ConsoleUi.RenderSkillDiagnostics(bootstrap.SkillLoadResult);

    if (options.ListSkills)
    {
        ConsoleUi.RenderSkillCatalog(bootstrap.SkillLoadResult);
        return 0;
    }

    if (HasPromptSource(options))
    {
        if (!ProviderResolver.TryEnsureConfiguration(provider, interactiveInputAvailable, out var configQuitRequested))
        {
            return configQuitRequested ? 0 : 2;
        }

        string prompt;
        try
        {
            prompt = await options.ResolvePromptAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ConsoleUi.RenderError($"Failed to resolve prompt: {ex.Message}");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            ConsoleUi.RenderError("Prompt is empty.");
            return 2;
        }

        var runExitCode = await RunSingleAsync(
            bootstrap.Orchestrator,
            eventSink,
            sessionKey,
            provider,
            model,
            options.Profile,
            options.AutoCreateSkills,
            prompt,
            options.ApprovalRequiredTools,
            options.ResolveWorkflowBudget(),
            bootstrap.Connectors,
            bootstrap.Browser,
            cancellation.Token);

        return runExitCode;
    }

    if (!interactiveInputAvailable)
    {
        ConsoleUi.RenderError("No prompt source was provided. Use --prompt/--prompt-file or run in interactive terminal mode.");
        return 2;
    }

    ConsoleUi.RenderInteractiveHelp();
    while (!cancellation.IsCancellationRequested)
    {
        if (!ProviderResolver.TryEnsureConfiguration(provider, interactiveInputAvailable: true, out var configQuitRequested))
        {
            return configQuitRequested ? 0 : 2;
        }

        var promptOrCommand = PromptForInteractivePrompt(provider, model, sessionKey.Value);
        if (IsQuitCommand(promptOrCommand))
        {
            ConsoleUi.RenderWarning("Interactive session closed.");
            return 0;
        }

        if (InteractiveCommandHandler.TryHandle(
                promptOrCommand,
                ref provider,
                ref model,
                ref sessionKey,
                options.Profile,
                options.NoEvents,
                bootstrap.SkillLoadResult.SkillsLoaded,
                llmOptionOverrides,
                bootstrap.MemoryStore,
                bootstrap.RunHistoryStore,
                bootstrap.SessionStore,
                bootstrap.TranscriptStore,
                bootstrap.AgentRegistry,
                bootstrap.MessageBus,
                bootstrap.WebBrowserService))
        {
            continue;
        }

        if (string.IsNullOrWhiteSpace(promptOrCommand))
        {
            ConsoleUi.RenderWarning("Prompt is empty. Enter a prompt or type 'quit' to exit.");
            continue;
        }

        var runExitCode = await RunSingleAsync(
            bootstrap.Orchestrator,
            eventSink,
            sessionKey,
            provider,
            model,
            options.Profile,
            options.AutoCreateSkills,
            promptOrCommand,
            options.ApprovalRequiredTools,
            options.ResolveWorkflowBudget(),
            bootstrap.Connectors,
            bootstrap.Browser,
            cancellation.Token);

        if (runExitCode is 130)
        {
            return runExitCode;
        }
    }

    return 0;
}
catch (OperationCanceledException)
{
    ConsoleUi.RenderWarning("Run canceled.");
    return 130;
}
catch (WorkflowReconciliationRequiredException ex)
{
    ConsoleUi.RenderError(ex.Message);
    ConsoleUi.RenderWarning("Use --list-workflows to inspect unresolved tool execution IDs. After verifying the external outcome, supply --resume with --reconcile-tool, --reconcile-result, and --reconcile-note.");
    return 2;
}
catch (Exception ex)
{
    ConsoleUi.RenderError($"Run failed: {ex.Message}");
    return 1;
}

static bool HasPromptSource(CliOptions options)
{
    return !string.IsNullOrWhiteSpace(options.Prompt) ||
           !string.IsNullOrWhiteSpace(options.PromptFile) ||
           Console.IsInputRedirected;
}

static string ResolveModel(string? configuredModel, string provider)
{
    if (!string.IsNullOrWhiteSpace(configuredModel))
    {
        return configuredModel.Trim();
    }

    return CliDefaults.ProviderDefaultModels[provider];
}

static OpenAiClientOptions? ResolveLlmBudgetOptions(string provider, OpenAiClientOptionOverrides llmOptionOverrides)
{
    return LlmClientFactory.ResolveOpenAiCompatibleOptions(provider, llmOptionOverrides, readEnvironmentOverrides: false);
}

static string PromptForInteractivePrompt(string provider, string model, string sessionKey)
{
    return AnsiConsole.Prompt(
        new TextPrompt<string>(
                $"[green]Prompt[/] [grey70](provider={provider}, model={model}, session={sessionKey}; type 'quit' or /help)[/]\n>")
            .AllowEmpty());
}

static bool IsQuitCommand(string input)
{
    var command = input.Trim();
    return command.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
           command.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
           command.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
           command.Equals(":q", StringComparison.OrdinalIgnoreCase);
}

static async Task<int> RunSingleAsync(
    IAgentOrchestrator orchestrator,
    IAgentEventSink eventSink,
    SessionKey sessionKey,
    string provider,
    string model,
    ToolProfile profile,
    bool autoCreateSkills,
    string prompt,
    IReadOnlyCollection<string>? approvalRequiredTools,
    WorkflowBudget? workflowBudget,
    McpHost? connectors,
    BrowserRuntime browser,
    CancellationToken ct)
{
    var requestedRunId = RunId.New();
    var retainBrowser = false;
    try
    {
        AnsiConsole.Write(new Rule("[grey]Event Stream[/]").LeftJustified());

        var request = new AgentRunRequest(
            SessionKey: sessionKey,
            UserInput: prompt,
            Provider: provider,
            Model: model,
            ToolPolicy: browser.Grant(connectors?.Grant(new(Profile: profile)) ?? new ToolSelectionPolicy(Profile: profile)),
            Caller: CallerIdentity.LocalOwner(),
            RequestedRunId: requestedRunId,
            AutoCreateSkills: autoCreateSkills,
            ApprovalRequiredTools: browser.RequireApprovals(connectors?.RequireApprovals(approvalRequiredTools) ?? approvalRequiredTools),
            Budget: workflowBudget);

        var result = await orchestrator.RunAsync(request, eventSink, ct);
        retainBrowser = result.Status is AgentRunStatus.AwaitingInput or AgentRunStatus.Paused;
        ConsoleUi.RenderFinal(result);
        return result.Status is AgentRunStatus.Succeeded or AgentRunStatus.AwaitingInput or AgentRunStatus.Paused ? 0 : 2;
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        ConsoleUi.RenderWarning("Run canceled.");
        return 130;
    }
    catch (Exception ex)
    {
        ConsoleUi.RenderError($"Run failed: {ex.Message}");
        return 1;
    }
    finally { if (!retainBrowser) await browser.ReleaseRunAsync(requestedRunId); }
}
