using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Providers;
using IronPaw.Cli;
using Spectre.Console;

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

var interactiveInputAvailable = !Console.IsInputRedirected;
if (!ProviderResolver.TryResolve(options.Provider, interactiveInputAvailable, out var provider, out var providerQuitRequested))
{
    return providerQuitRequested ? 0 : 2;
}

var model = ResolveModel(options.Model, provider);
var sessionKey = new SessionKey(string.IsNullOrWhiteSpace(options.Session) ? "default" : options.Session.Trim());
IAgentEventSink eventSink = options.NoEvents
    ? NullAgentEventSink.Instance
    : new AnsiConsoleEventSink();
var llmOptionOverrides = new OpenAiClientOptionOverrides(
    ContextWindowTokens: options.LlmContextWindowTokens,
    SafetyMarginTokens: options.LlmSafetyMarginTokens,
    MinimumOutputTokensPerRequest: options.LlmMinOutputTokensPerRequest,
    MaxAutoContinuationPasses: options.LlmMaxAutoContinuationPasses);
using var bootstrap = AppBootstrapper.Build(options.Store, options.MessageTtlDays, llmOptionOverrides);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    ConsoleUi.RenderBanner();
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
    return LlmClientFactory.ResolveOpenAiCompatibleOptions(provider, llmOptionOverrides);
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
    CancellationToken ct)
{
    try
    {
        AnsiConsole.Write(new Rule("[grey]Event Stream[/]").LeftJustified());

        var request = new AgentRunRequest(
            SessionKey: sessionKey,
            UserInput: prompt,
            Provider: provider,
            Model: model,
            ToolPolicy: new ToolSelectionPolicy(Profile: profile),
            Caller: CallerIdentity.LocalOwner(),
            AutoCreateSkills: autoCreateSkills);

        var result = await orchestrator.RunAsync(request, eventSink, ct);
        ConsoleUi.RenderFinal(result);
        return 0;
    }
    catch (OperationCanceledException)
    {
        ConsoleUi.RenderWarning("Run canceled.");
        return 130;
    }
    catch (Exception ex)
    {
        ConsoleUi.RenderError($"Run failed: {ex.Message}");
        return 1;
    }
}
