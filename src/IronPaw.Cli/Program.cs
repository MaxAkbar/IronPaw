using System.IO;
using System.Net.Http;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Providers;
using IronPaw.AgentCore.Tools;
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
if (!TryResolveProvider(options.Provider, interactiveInputAvailable, out var provider, out var providerQuitRequested))
{
    return providerQuitRequested ? 0 : 2;
}

var model = ResolveModel(options.Model, provider);
var sessionKey = new SessionKey(string.IsNullOrWhiteSpace(options.Session) ? "default" : options.Session.Trim());
IAgentEventSink eventSink = options.NoEvents
    ? NullAgentEventSink.Instance
    : new AnsiConsoleEventSink();
var bootstrap = BuildOrchestrator();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    ConsoleUi.RenderBanner();
    ConsoleUi.RenderRunSettings(provider, model, sessionKey.Value, options.Profile, options.NoEvents, bootstrap.SkillLoadResult.SkillsLoaded);
    ConsoleUi.RenderSkillDiagnostics(bootstrap.SkillLoadResult);

    if (options.ListSkills)
    {
        ConsoleUi.RenderSkillCatalog(bootstrap.SkillLoadResult);
        return 0;
    }

    if (HasPromptSource(options))
    {
        if (!TryEnsureProviderConfiguration(provider, interactiveInputAvailable, out var configQuitRequested))
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
        if (!TryEnsureProviderConfiguration(provider, interactiveInputAvailable: true, out var configQuitRequested))
        {
            return configQuitRequested ? 0 : 2;
        }

        var promptOrCommand = PromptForInteractivePrompt(provider, model, sessionKey.Value);
        if (IsQuitCommand(promptOrCommand))
        {
            ConsoleUi.RenderWarning("Interactive session closed.");
            return 0;
        }

        if (TryHandleInteractiveCommand(
                promptOrCommand,
                ref provider,
                ref model,
                ref sessionKey,
                options.Profile,
                options.NoEvents,
                bootstrap.SkillLoadResult.SkillsLoaded,
                bootstrap.MemoryStore,
                bootstrap.RunHistoryStore))
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

static bool TryResolveProvider(
    string configuredProvider,
    bool interactiveInputAvailable,
    out string provider,
    out bool userQuitRequested)
{
    userQuitRequested = false;
    provider = configuredProvider.Trim().ToLowerInvariant();
    if (CliDefaults.ProviderDefaultModels.ContainsKey(provider))
    {
        return true;
    }

    if (!interactiveInputAvailable)
    {
        ConsoleUi.RenderError($"Unsupported provider '{configuredProvider}'.");
        return false;
    }

    ConsoleUi.RenderWarning($"Unsupported provider '{configuredProvider}'.");
    var selected = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select a provider or quit:")
            .AddChoices(CliDefaults.ProviderChoicesWithQuit));

    if (string.Equals(selected, CliDefaults.QuitOption, StringComparison.OrdinalIgnoreCase))
    {
        userQuitRequested = true;
        return false;
    }

    provider = selected;
    return true;
}

static bool TryEnsureProviderConfiguration(string provider, bool interactiveInputAvailable, out bool userQuitRequested)
{
    userQuitRequested = false;
    switch (provider)
    {
        case "openai":
            return TryEnsureEnvironmentVariable("OPENAI_API_KEY", interactiveInputAvailable, out userQuitRequested);

        case "anthropic":
            return TryEnsureEnvironmentVariable("ANTHROPIC_API_KEY", interactiveInputAvailable, out userQuitRequested);

        default:
            return true;
    }
}

static bool TryEnsureEnvironmentVariable(
    string variableName,
    bool interactiveInputAvailable,
    out bool userQuitRequested)
{
    userQuitRequested = false;
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName)))
    {
        return true;
    }

    if (!interactiveInputAvailable)
    {
        ConsoleUi.RenderError($"{variableName} is not configured.");
        return false;
    }

    while (true)
    {
        var value = AnsiConsole.Prompt(
            new TextPrompt<string>($"{variableName} is required. Enter value or type 'quit' to exit:")
                .Secret()
                .AllowEmpty());

        if (IsQuitCommand(value))
        {
            userQuitRequested = true;
            return false;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            ConsoleUi.RenderWarning($"{variableName} cannot be empty.");
            continue;
        }

        Environment.SetEnvironmentVariable(variableName, value.Trim());
        return true;
    }
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

static bool TryHandleInteractiveCommand(
    string promptOrCommand,
    ref string provider,
    ref string model,
    ref SessionKey sessionKey,
    ToolProfile profile,
    bool noEvents,
    int skillsLoaded,
    IMemoryStore memoryStore,
    IRunHistoryStore runHistoryStore)
{
    var command = promptOrCommand.Trim();
    if (!command.StartsWith("/", StringComparison.Ordinal))
    {
        return false;
    }

    switch (command.ToLowerInvariant())
    {
        case "/help":
            ConsoleUi.RenderInteractiveHelp();
            return true;

        case "/settings":
            ConsoleUi.RenderRunSettings(provider, model, sessionKey.Value, profile, noEvents, skillsLoaded);
            return true;

        case "/provider":
        {
            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select provider:")
                    .AddChoices(CliDefaults.ProviderNames));

            var oldDefaultModel = CliDefaults.ProviderDefaultModels[provider];
            provider = selected;
            if (string.Equals(model, oldDefaultModel, StringComparison.OrdinalIgnoreCase))
            {
                model = CliDefaults.ProviderDefaultModels[provider];
            }

            ConsoleUi.RenderRunSettings(provider, model, sessionKey.Value, profile, noEvents, skillsLoaded);
            return true;
        }

        case "/model":
        {
            var selectedModel = AnsiConsole.Prompt(
                new TextPrompt<string>($"Model for provider '{provider}':")
                    .DefaultValue(model)
                    .ShowDefaultValue());

            if (string.IsNullOrWhiteSpace(selectedModel))
            {
                ConsoleUi.RenderWarning("Model cannot be empty.");
                return true;
            }

            model = selectedModel.Trim();
            ConsoleUi.RenderRunSettings(provider, model, sessionKey.Value, profile, noEvents, skillsLoaded);
            return true;
        }

        case "/session":
        {
            var selectedSession = AnsiConsole.Prompt(
                new TextPrompt<string>("Session key:")
                    .DefaultValue(sessionKey.Value)
                    .ShowDefaultValue());

            if (string.IsNullOrWhiteSpace(selectedSession))
            {
                ConsoleUi.RenderWarning("Session key cannot be empty.");
                return true;
            }

            sessionKey = new SessionKey(selectedSession.Trim());
            ConsoleUi.RenderRunSettings(provider, model, sessionKey.Value, profile, noEvents, skillsLoaded);
            return true;
        }

        case "/remember":
        {
            var content = AnsiConsole.Prompt(
                new TextPrompt<string>("What should I remember?")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(content))
            {
                ConsoleUi.RenderWarning("Memory content cannot be empty.");
                return true;
            }

            var entry = new MemoryEntry(
                Id: Guid.NewGuid().ToString("n"),
                Content: content.Trim(),
                Scope: MemoryScope.Global,
                CreatedAt: DateTimeOffset.UtcNow,
                Source: "user");

            memoryStore.AddAsync(entry, CancellationToken.None).GetAwaiter().GetResult();
            ConsoleUi.RenderSuccess($"Remembered: {content.Trim()}");
            return true;
        }

        case "/forget":
        {
            var globalMemories = memoryStore.LoadGlobalAsync(CancellationToken.None).GetAwaiter().GetResult();
            var sessionMemories = memoryStore.LoadSessionAsync(sessionKey, CancellationToken.None).GetAwaiter().GetResult();
            var allMemories = globalMemories.Concat(sessionMemories).ToList();

            if (allMemories.Count == 0)
            {
                ConsoleUi.RenderWarning("No memories to forget.");
                return true;
            }

            ConsoleUi.RenderMemoryList(globalMemories, sessionMemories, sessionKey.Value);

            var idToForget = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter memory ID to forget (or 'cancel'):")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(idToForget) ||
                idToForget.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var removed = memoryStore.RemoveAsync(idToForget.Trim(), CancellationToken.None).GetAwaiter().GetResult();
            if (removed)
            {
                ConsoleUi.RenderSuccess("Memory forgotten.");
            }
            else
            {
                ConsoleUi.RenderWarning("Memory not found.");
            }

            return true;
        }

        case "/memories":
        {
            var globalMemories = memoryStore.LoadGlobalAsync(CancellationToken.None).GetAwaiter().GetResult();
            var sessionMemories = memoryStore.LoadSessionAsync(sessionKey, CancellationToken.None).GetAwaiter().GetResult();
            ConsoleUi.RenderMemoryList(globalMemories, sessionMemories, sessionKey.Value);
            return true;
        }

        case "/tasks":
        {
            var runs = runHistoryStore.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
            var recent = runs
                .OrderByDescending(r => r.StartedAt)
                .Take(20)
                .ToList();

            ConsoleUi.RenderRunHistory(recent);
            return true;
        }

        case "/review":
        {
            var runId = AnsiConsole.Prompt(
                new TextPrompt<string>("Run ID to review:")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(runId))
            {
                ConsoleUi.RenderWarning("Run ID cannot be empty.");
                return true;
            }

            var detail = runHistoryStore.GetAsync(runId.Trim(), CancellationToken.None).GetAwaiter().GetResult();
            if (detail is null)
            {
                ConsoleUi.RenderWarning("Run not found.");
                return true;
            }

            ConsoleUi.RenderRunDetail(detail);
            return true;
        }

        default:
            ConsoleUi.RenderWarning($"Unknown command '{command}'. Type /help for commands.");
            return true;
    }
}

static async Task<int> RunSingleAsync(
    IAgentOrchestrator orchestrator,
    IAgentEventSink eventSink,
    SessionKey sessionKey,
    string provider,
    string model,
    ToolProfile profile,
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
            ToolPolicy: new ToolSelectionPolicy(
                Profile: profile,
                SenderIsOwner: true));

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

static (IAgentOrchestrator Orchestrator, SkillAddinLoadResult SkillLoadResult, IMemoryStore MemoryStore, IRunHistoryStore RunHistoryStore) BuildOrchestrator()
{
    var workspaceRoot = Directory.GetCurrentDirectory();
    var appData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IronPaw");
    var stateRoot = Path.Combine(appData, "state");
    var sessionsDirectory = Path.Combine(stateRoot, "sessions");
    var sessionStorePath = Path.Combine(stateRoot, "session-store.json");

    Directory.CreateDirectory(stateRoot);
    Directory.CreateDirectory(sessionsDirectory);

    var memoryStore = new FileMemoryStore(new FileMemoryStoreOptions(
        GlobalMemoryPath: Path.Combine(stateRoot, "memories.json"),
        SessionMemoriesDirectory: sessionsDirectory));
    var runHistoryStore = new JsonRunHistoryStore(Path.Combine(stateRoot, "run-history.jsonl"));

    var skillLoadResult = SkillAddinLoader.LoadFromDirectories(
    [
        Path.Combine(workspaceRoot, "addins", "skills"),
        Path.Combine(appData, "skills")
    ]);
    var tools = BuiltInToolFactory.CreateDefault(workspaceRoot, skillLoadResult.SkillTools, memoryStore, runHistoryStore);
    var toolRegistry = new InMemoryToolRegistry(tools);
    toolRegistry.RegisterTools([new CreateSkillTool(toolRegistry)]);
    var sessionStore = new FileSessionStore(new FileSessionStoreOptions(
        StoreFilePath: sessionStorePath,
        CacheTtl: TimeSpan.FromSeconds(5)));
    var transcriptStore = new JsonTranscriptStore(sessionsDirectory);
    var laneScheduler = new SessionLaneScheduler();
    var toolExecutor = new ToolExecutor();
    var schemaNormalizer = new ToolSchemaNormalizer();
    var llmClient = BuildLlmRouter();
    var sanitizer = new TranscriptSanitizer();
    var truncator = new ToolResultTruncator();
    var promptBuilder = new DefaultSystemPromptBuilder();

    var orchestrator = new AgentOrchestrator(
        laneScheduler,
        sessionStore,
        transcriptStore,
        toolRegistry,
        toolExecutor,
        schemaNormalizer,
        llmClient,
        sanitizer,
        truncator,
        promptBuilder,
        memoryStore,
        runHistoryStore);

    return (orchestrator, skillLoadResult, memoryStore, runHistoryStore);
}

static ILlmClient BuildLlmRouter()
{
    var sharedHttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    var local = new RuleBasedLlmClient();
    var openAi = new OpenAiLlmClient(
        sharedHttpClient,
        () => Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
    var ollama = new OpenAiLlmClient(
        sharedHttpClient,
        () => ResolveApiKey("OLLAMA_API_KEY", "ollama"),
        ResolveOpenAiCompatibleBaseUrl(
            Environment.GetEnvironmentVariable("OLLAMA_BASE_URL"),
            "http://localhost:11434"));
    var lmStudio = new OpenAiLlmClient(
        sharedHttpClient,
        () => ResolveApiKey("LMSTUDIO_API_KEY", "lm-studio"),
        ResolveOpenAiCompatibleBaseUrl(
            Environment.GetEnvironmentVariable("LMSTUDIO_BASE_URL"),
            "http://localhost:1234"));
    var anthropic = new AnthropicLlmClient(
        sharedHttpClient,
        () => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

    var providers = new Dictionary<string, ILlmClient>(StringComparer.OrdinalIgnoreCase)
    {
        ["local"] = local,
        ["openai"] = openAi,
        ["ollama"] = ollama,
        ["lmstudio"] = lmStudio,
        ["anthropic"] = anthropic
    };

    return new RoutedLlmClient(providers, local);
}

static string ResolveApiKey(string environmentVariableName, string fallback)
{
    var configured = Environment.GetEnvironmentVariable(environmentVariableName);
    return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
}

static string ResolveOpenAiCompatibleBaseUrl(string? configured, string fallbackBaseUrl)
{
    var baseUrl = string.IsNullOrWhiteSpace(configured)
        ? fallbackBaseUrl
        : configured.Trim();

    baseUrl = baseUrl.TrimEnd('/');
    if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
    {
        baseUrl = baseUrl[..^3];
    }

    return baseUrl;
}

internal static class CliDefaults
{
    public const string QuitOption = "quit";

    public static readonly IReadOnlyList<string> ProviderNames =
    [
        "local",
        "openai",
        "ollama",
        "lmstudio",
        "anthropic"
    ];

    public static readonly IReadOnlyList<string> ProviderChoicesWithQuit =
    [
        "local",
        "openai",
        "ollama",
        "lmstudio",
        "anthropic",
        QuitOption
    ];

    public static readonly IReadOnlyDictionary<string, string> ProviderDefaultModels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["local"] = "rule-based-v1",
            ["openai"] = "gpt-4o-mini",
            ["ollama"] = "llama3.2",
            ["lmstudio"] = "local-model",
            ["anthropic"] = "claude-3-5-haiku-latest"
        };
}

internal sealed class AnsiConsoleEventSink : IAgentEventSink
{
    public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct)
    {
        var severityStyle = evt.Severity switch
        {
            AgentEventSeverity.Warning => "yellow",
            AgentEventSeverity.Error => "red",
            _ => "grey70"
        };

        var streamStyle = evt.Stream switch
        {
            AgentEventStream.Lifecycle => "deepskyblue1",
            AgentEventStream.Assistant => "green3_1",
            AgentEventStream.Tool => "darkorange3",
            AgentEventStream.Diagnostics => "mediumpurple",
            AgentEventStream.Compaction => "khaki1",
            _ => "white"
        };

        var time = evt.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        var data = string.Join(", ", evt.Data.Select(pair => $"{pair.Key}={pair.Value}"));
        var dataPart = string.IsNullOrWhiteSpace(data)
            ? string.Empty
            : $" [silver]{Markup.Escape(data)}[/]";

        AnsiConsole.MarkupLine(
            $"[{severityStyle}]{Markup.Escape(time)}[/] " +
            $"[{streamStyle}]{Markup.Escape(evt.Stream.ToString())}[/]/" +
            $"[{streamStyle}]{Markup.Escape(evt.Type)}[/]{dataPart}");

        return ValueTask.CompletedTask;
    }
}

internal static class ConsoleUi
{
    public static void RenderBanner()
    {
        AnsiConsole.Write(new FigletText("IronPaw").Color(Color.CadetBlue));
        AnsiConsole.MarkupLine("[grey70]Agent Command Center[/]");
        AnsiConsole.WriteLine();
    }

    public static void RenderInteractiveHelp()
    {
        AnsiConsole.MarkupLine("[bold]Interactive commands[/]");
        AnsiConsole.MarkupLine("  [white]/help[/]       Show this command list");
        AnsiConsole.MarkupLine("  [white]/settings[/]   Show current run settings");
        AnsiConsole.MarkupLine("  [white]/provider[/]   Change provider");
        AnsiConsole.MarkupLine("  [white]/model[/]      Change model");
        AnsiConsole.MarkupLine("  [white]/session[/]    Change session key");
        AnsiConsole.MarkupLine("  [white]/remember[/]   Save a memory");
        AnsiConsole.MarkupLine("  [white]/forget[/]     Remove a memory");
        AnsiConsole.MarkupLine("  [white]/memories[/]   List all memories");
        AnsiConsole.MarkupLine("  [white]/tasks[/]      List recent task runs");
        AnsiConsole.MarkupLine("  [white]/review[/]     Review a specific task run");
        AnsiConsole.MarkupLine("  [white]quit[/]        Exit the app");
        AnsiConsole.WriteLine();
    }

    public static void RenderRunSettings(
        string provider,
        string model,
        string session,
        ToolProfile profile,
        bool noEvents,
        int skillsLoaded)
    {
        var table = new Table()
            .RoundedBorder()
            .AddColumn("[bold]Setting[/]")
            .AddColumn("[bold]Value[/]");

        table.AddRow("Provider", $"[white]{Markup.Escape(provider)}[/]");
        table.AddRow("Model", $"[white]{Markup.Escape(model)}[/]");
        table.AddRow("Session", $"[white]{Markup.Escape(session)}[/]");
        table.AddRow("Tool Profile", $"[white]{Markup.Escape(profile.ToString())}[/]");
        table.AddRow("Loaded Skills", $"[white]{skillsLoaded}[/]");
        table.AddRow("Event Stream", noEvents ? "[white]disabled[/]" : "[white]enabled[/]");

        var panel = new Panel(table)
            .Header("[bold deepskyblue1]Run Configuration[/]")
            .RoundedBorder()
            .BorderColor(Color.SlateBlue1);

        AnsiConsole.Write(panel);
    }

    public static void RenderSkillDiagnostics(SkillAddinLoadResult result)
    {
        foreach (var message in result.Diagnostics)
        {
            AnsiConsole.MarkupLine($"[grey54]skills[/]: [grey70]{Markup.Escape(message)}[/]");
        }

        if (result.Diagnostics.Count > 0)
        {
            AnsiConsole.WriteLine();
        }
    }

    public static void RenderSkillCatalog(SkillAddinLoadResult result)
    {
        AnsiConsole.Write(new Rule("[grey]Loaded Skills[/]").LeftJustified());
        if (result.LoadedSkills.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No add-in skills were discovered.[/]");
            return;
        }

        var table = new Table()
            .RoundedBorder()
            .AddColumn("[bold]Add-in[/]")
            .AddColumn("[bold]Skill[/]")
            .AddColumn("[bold]Tool Name[/]")
            .AddColumn("[bold]Description[/]");

        foreach (var skill in result.LoadedSkills.OrderBy(skill => skill.AddinId).ThenBy(skill => skill.SkillName, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(
                $"[white]{Markup.Escape(skill.AddinId)}[/]",
                $"[white]{Markup.Escape(skill.SkillName)}[/]",
                $"[deepskyblue1]{Markup.Escape(skill.ToolName)}[/]",
                $"[grey70]{Markup.Escape(skill.Description)}[/]");
        }

        AnsiConsole.Write(table);
    }

    public static void RenderFinal(AgentRunResult result)
    {
        AnsiConsole.WriteLine();

        var panel = new Panel(new Text(result.FinalAssistantMessage))
            .Header("[bold green3_1]Final Assistant Message[/]")
            .RoundedBorder()
            .BorderColor(Color.Green3_1);
        AnsiConsole.Write(panel);

        AnsiConsole.MarkupLine(
            $"[grey70]run_id[/]: [deepskyblue1]{Markup.Escape(result.RunId.Value)}[/]");
    }

    public static void RenderMemoryList(
        IReadOnlyList<MemoryEntry> globalMemories,
        IReadOnlyList<MemoryEntry> sessionMemories,
        string sessionId)
    {
        AnsiConsole.Write(new Rule("[grey]Memories[/]").LeftJustified());

        if (globalMemories.Count == 0 && sessionMemories.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No memories saved yet.[/]");
            return;
        }

        if (globalMemories.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]Global Memories[/]");
            var table = new Table()
                .RoundedBorder()
                .AddColumn("[bold]ID[/]")
                .AddColumn("[bold]Content[/]")
                .AddColumn("[bold]Category[/]")
                .AddColumn("[bold]Source[/]")
                .AddColumn("[bold]Created[/]");

            foreach (var m in globalMemories)
            {
                table.AddRow(
                    $"[grey70]{Markup.Escape(m.Id[..Math.Min(8, m.Id.Length)])}[/]",
                    $"[white]{Markup.Escape(m.Content)}[/]",
                    $"[grey70]{Markup.Escape(m.Category ?? "-")}[/]",
                    $"[grey70]{Markup.Escape(m.Source ?? "-")}[/]",
                    $"[grey70]{Markup.Escape(m.CreatedAt.ToLocalTime().ToString("g"))}[/]");
            }

            AnsiConsole.Write(table);
        }

        if (sessionMemories.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold]Session Memories[/] [grey70](session={Markup.Escape(sessionId)})[/]");
            var table = new Table()
                .RoundedBorder()
                .AddColumn("[bold]ID[/]")
                .AddColumn("[bold]Content[/]")
                .AddColumn("[bold]Category[/]")
                .AddColumn("[bold]Source[/]")
                .AddColumn("[bold]Created[/]");

            foreach (var m in sessionMemories)
            {
                table.AddRow(
                    $"[grey70]{Markup.Escape(m.Id[..Math.Min(8, m.Id.Length)])}[/]",
                    $"[white]{Markup.Escape(m.Content)}[/]",
                    $"[grey70]{Markup.Escape(m.Category ?? "-")}[/]",
                    $"[grey70]{Markup.Escape(m.Source ?? "-")}[/]",
                    $"[grey70]{Markup.Escape(m.CreatedAt.ToLocalTime().ToString("g"))}[/]");
            }

            AnsiConsole.Write(table);
        }
    }

    public static void RenderRunHistory(IReadOnlyList<RunSummary> runs)
    {
        AnsiConsole.Write(new Rule("[grey]Task History[/]").LeftJustified());

        if (runs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No task runs recorded yet.[/]");
            return;
        }

        var table = new Table()
            .RoundedBorder()
            .AddColumn("[bold]Run ID[/]")
            .AddColumn("[bold]Session[/]")
            .AddColumn("[bold]When[/]")
            .AddColumn("[bold]Prompt[/]")
            .AddColumn("[bold]Rounds[/]")
            .AddColumn("[bold]Tokens[/]");

        foreach (var r in runs)
        {
            var promptTruncated = r.UserPrompt.Length > 60
                ? r.UserPrompt[..60] + "..."
                : r.UserPrompt;

            table.AddRow(
                $"[deepskyblue1]{Markup.Escape(r.RunId[..Math.Min(8, r.RunId.Length)])}[/]",
                $"[white]{Markup.Escape(r.SessionId)}[/]",
                $"[grey70]{Markup.Escape(r.StartedAt.ToLocalTime().ToString("g"))}[/]",
                $"[white]{Markup.Escape(promptTruncated)}[/]",
                $"[white]{r.Rounds}[/]",
                $"[white]{r.TotalTokens}[/]");
        }

        AnsiConsole.Write(table);
    }

    public static void RenderRunDetail(RunSummary summary)
    {
        AnsiConsole.Write(new Rule("[grey]Run Detail[/]").LeftJustified());

        var infoTable = new Table()
            .RoundedBorder()
            .AddColumn("[bold]Field[/]")
            .AddColumn("[bold]Value[/]");

        infoTable.AddRow("Run ID", $"[deepskyblue1]{Markup.Escape(summary.RunId)}[/]");
        infoTable.AddRow("Session", $"[white]{Markup.Escape(summary.SessionId)}[/]");
        infoTable.AddRow("Started", $"[white]{Markup.Escape(summary.StartedAt.ToLocalTime().ToString("O"))}[/]");
        infoTable.AddRow("Completed", $"[white]{Markup.Escape(summary.CompletedAt.ToLocalTime().ToString("O"))}[/]");
        infoTable.AddRow("Rounds", $"[white]{summary.Rounds}[/]");
        infoTable.AddRow("Total Tokens", $"[white]{summary.TotalTokens}[/]");
        infoTable.AddRow("Tools Used", $"[white]{Markup.Escape(string.Join(", ", summary.ToolsUsed))}[/]");

        AnsiConsole.Write(infoTable);

        var promptPanel = new Panel(new Text(summary.UserPrompt))
            .Header("[bold]User Prompt[/]")
            .RoundedBorder()
            .BorderColor(Color.Grey70);
        AnsiConsole.Write(promptPanel);

        var responsePanel = new Panel(new Text(summary.FinalAssistantMessage))
            .Header("[bold green3_1]Final Response[/]")
            .RoundedBorder()
            .BorderColor(Color.Green3_1);
        AnsiConsole.Write(responsePanel);
    }

    public static void RenderSuccess(string message)
    {
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]");
    }

    public static void RenderWarning(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");
    }

    public static void RenderError(string message)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
    }
}

internal sealed record CliOptions(
    string Provider,
    string? Model,
    string Session,
    string? Prompt,
    string? PromptFile,
    ToolProfile Profile,
    bool ListSkills,
    bool NoEvents,
    bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        var provider = "local";
        string? model = null;
        var session = "default";
        string? prompt = null;
        string? promptFile = null;
        var profile = ToolProfile.Coding;
        var listSkills = false;
        var noEvents = false;
        var showHelp = false;

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

                case "--no-events":
                    noEvents = true;
                    break;

                case "--list-skills":
                    listSkills = true;
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

        return new CliOptions(provider, model, session, prompt, promptFile, profile, listSkills, noEvents, showHelp);
    }

    public bool TryValidate(out string? error)
    {
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
        table.AddRow("--profile <name>", "Tool profile: Minimal | Coding | Messaging | Full");
        table.AddRow("--list-skills", "List discovered add-in skills and exit");
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
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Interactive Mode[/]");
        AnsiConsole.MarkupLine("  Launch without --prompt/--prompt-file to keep running until you type [white]quit[/].");
        AnsiConsole.MarkupLine("  Commands: [white]/help[/], [white]/settings[/], [white]/provider[/], [white]/model[/], [white]/session[/],");
        AnsiConsole.MarkupLine("           [white]/remember[/], [white]/forget[/], [white]/memories[/], [white]/tasks[/], [white]/review[/]");
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
}
