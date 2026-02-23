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
        AnsiConsole.MarkupLine("  [white]/sessions[/]   List all sessions");
        AnsiConsole.MarkupLine("  [white]/session-detail[/] Inspect a session's files and stats");
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

    public static void RenderSessionList(IReadOnlyDictionary<SessionKey, SessionEntry> sessions)
    {
        AnsiConsole.Write(new Rule("[grey]Sessions[/]").LeftJustified());

        if (sessions.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No sessions found.[/]");
            return;
        }

        var table = new Table()
            .RoundedBorder()
            .AddColumn("[bold]Session ID[/]")
            .AddColumn("[bold]Last Updated[/]")
            .AddColumn("[bold]Total Tokens[/]")
            .AddColumn("[bold]Compactions[/]")
            .AddColumn("[bold]Transcript Path[/]");

        foreach (var (key, entry) in sessions.OrderByDescending(s => s.Value.UpdatedAt))
        {
            table.AddRow(
                $"[deepskyblue1]{Markup.Escape(key.Value)}[/]",
                $"[grey70]{Markup.Escape(entry.UpdatedAt.ToLocalTime().ToString("g"))}[/]",
                $"[white]{entry.TotalTokens}[/]",
                $"[white]{entry.CompactionCount}[/]",
                $"[grey70]{Markup.Escape(entry.TranscriptPath ?? "-")}[/]");
        }

        AnsiConsole.Write(table);
    }

    public static void RenderSessionDetail(
        SessionEntry session,
        IReadOnlyList<ChatMessage> transcript,
        IReadOnlyList<MemoryEntry> sessionMemories)
    {
        AnsiConsole.Write(new Rule("[grey]Session Detail[/]").LeftJustified());

        var infoTable = new Table()
            .RoundedBorder()
            .AddColumn("[bold]Field[/]")
            .AddColumn("[bold]Value[/]");

        infoTable.AddRow("Session ID", $"[deepskyblue1]{Markup.Escape(session.SessionId)}[/]");
        infoTable.AddRow("Last Updated", $"[white]{Markup.Escape(session.UpdatedAt.ToLocalTime().ToString("O"))}[/]");
        infoTable.AddRow("Provider", $"[white]{Markup.Escape(session.ProviderOverride ?? "-")}[/]");
        infoTable.AddRow("Model", $"[white]{Markup.Escape(session.ModelOverride ?? "-")}[/]");
        infoTable.AddRow("Input Tokens", $"[white]{session.InputTokens}[/]");
        infoTable.AddRow("Output Tokens", $"[white]{session.OutputTokens}[/]");
        infoTable.AddRow("Total Tokens", $"[white]{session.TotalTokens}[/]");
        infoTable.AddRow("Compactions", $"[white]{session.CompactionCount}[/]");
        infoTable.AddRow("Transcript Path", $"[grey70]{Markup.Escape(session.TranscriptPath ?? "-")}[/]");

        AnsiConsole.Write(infoTable);

        // Transcript stats
        var userCount = transcript.Count(m => m.Role is ChatRole.User);
        var assistantCount = transcript.Count(m => m.Role is ChatRole.Assistant);
        var toolCount = transcript.Count(m => m.Role is ChatRole.Tool);

        var statsTable = new Table()
            .RoundedBorder()
            .AddColumn("[bold]Transcript Stats[/]")
            .AddColumn("[bold]Count[/]");

        statsTable.AddRow("Total Messages", $"[white]{transcript.Count}[/]");
        statsTable.AddRow("User Messages", $"[white]{userCount}[/]");
        statsTable.AddRow("Assistant Messages", $"[white]{assistantCount}[/]");
        statsTable.AddRow("Tool Messages", $"[white]{toolCount}[/]");
        statsTable.AddRow("Session Memories", $"[white]{sessionMemories.Count}[/]");

        AnsiConsole.Write(statsTable);
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
