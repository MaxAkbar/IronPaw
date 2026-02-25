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
        var store = StoreBackend.Json;
        var messageTtlDays = 30;
        var autoCreateSkills = false;
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

                case "--auto-create-skills":
                    autoCreateSkills = true;
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

        return new CliOptions(provider, model, session, prompt, promptFile, profile, store, messageTtlDays, autoCreateSkills, listSkills, noEvents, showHelp);
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
        table.AddRow("--profile <name>", "Tool profile: Minimal | Coding | Messaging | Web | Full");
        table.AddRow("--auto-create-skills", "Automatically create runtime tools when needed (requires Full profile)");
        table.AddRow("--list-skills", "List discovered add-in skills and exit");
        table.AddRow("--store <backend>", "Storage backend: json | sqlite (default: json)");
        table.AddRow("--message-ttl-days <days>", "SQLite message retention window in days (default: 30)");
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
}
