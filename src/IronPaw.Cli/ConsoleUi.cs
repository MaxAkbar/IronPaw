using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;
using Spectre.Console;

namespace IronPaw.Cli;

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
