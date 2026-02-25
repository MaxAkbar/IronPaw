using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;
using Spectre.Console;

namespace IronPaw.Cli;

internal static class InteractiveCommandHandler
{
    public static bool TryHandle(
        string promptOrCommand,
        ref string provider,
        ref string model,
        ref SessionKey sessionKey,
        ToolProfile profile,
        bool noEvents,
        int skillsLoaded,
        IMemoryStore memoryStore,
        IRunHistoryStore runHistoryStore,
        ISessionStore sessionStore,
        ITranscriptStore transcriptStore,
        IAgentRegistry agentRegistry,
        IAgentMessageBus messageBus,
        IWebBrowserService webBrowserService)
    {
        var command = promptOrCommand.Trim();
        if (!command.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        if (command.StartsWith("/agent-messages", StringComparison.OrdinalIgnoreCase))
        {
            return HandleAgentMessagesCommand(command, sessionKey, agentRegistry, messageBus);
        }

        if (command.StartsWith("/scope-runs", StringComparison.OrdinalIgnoreCase))
        {
            return HandleScopeRunsCommand(command, sessionKey, runHistoryStore);
        }

        if (command.StartsWith("/web-sessions", StringComparison.OrdinalIgnoreCase))
        {
            return HandleWebSessionsCommand(command, webBrowserService);
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

            case "/sessions":
            {
                var allSessions = sessionStore.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
                ConsoleUi.RenderSessionList(allSessions);
                return true;
            }

            case "/session-detail":
            {
                var allSessions = sessionStore.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (allSessions.Count == 0)
                {
                    ConsoleUi.RenderWarning("No sessions found.");
                    return true;
                }

                var selectedId = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select a session to inspect:")
                        .AddChoices(allSessions.Keys.Select(k => k.Value).OrderBy(k => k)));

                var key = new SessionKey(selectedId);
                var session = allSessions[key];
                var transcript = transcriptStore.LoadAsync(key, CancellationToken.None).GetAwaiter().GetResult();
                var sessionMemories = memoryStore.LoadSessionAsync(key, CancellationToken.None).GetAwaiter().GetResult();

                ConsoleUi.RenderSessionDetail(session, transcript, sessionMemories);
                return true;
            }

            default:
                ConsoleUi.RenderWarning($"Unknown command '{command}'. Type /help for commands.");
                return true;
        }
    }

    private static bool HandleAgentMessagesCommand(
        string command,
        SessionKey sessionKey,
        IAgentRegistry agentRegistry,
        IAgentMessageBus messageBus)
    {
        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            ConsoleUi.RenderWarning("Usage: /agent-messages <scope-run-id> [agent-id] [--max N] [--consume] [--sender agent-id] [--correlation id]");
            return true;
        }

        RunId scopeRunId;
        try
        {
            scopeRunId = new RunId(tokens[1]);
        }
        catch (ArgumentException ex)
        {
            ConsoleUi.RenderWarning(ex.Message);
            return true;
        }

        var index = 2;
        string? agentIdFilter = null;
        string? senderFilter = null;
        string? correlationFilter = null;
        if (index < tokens.Length && !tokens[index].StartsWith("--", StringComparison.Ordinal))
        {
            agentIdFilter = tokens[index].Trim();
            index++;
        }

        var maxMessages = 20;
        var markAsRead = false;
        while (index < tokens.Length)
        {
            var token = tokens[index].Trim();
            switch (token.ToLowerInvariant())
            {
                case "--consume":
                    markAsRead = true;
                    index++;
                    break;

                case "--peek":
                    markAsRead = false;
                    index++;
                    break;

                case "--max":
                    if (index + 1 >= tokens.Length ||
                        !int.TryParse(tokens[index + 1], out var parsedMax) ||
                        parsedMax <= 0)
                    {
                        ConsoleUi.RenderWarning("Invalid --max value. Use a positive integer.");
                        return true;
                    }

                    maxMessages = Math.Clamp(parsedMax, 1, 100);
                    index += 2;
                    break;

                case "--sender":
                    if (index + 1 >= tokens.Length || string.IsNullOrWhiteSpace(tokens[index + 1]))
                    {
                        ConsoleUi.RenderWarning("Invalid --sender value. Provide an agent ID.");
                        return true;
                    }

                    senderFilter = tokens[index + 1].Trim();
                    index += 2;
                    break;

                case "--correlation":
                    if (index + 1 >= tokens.Length || string.IsNullOrWhiteSpace(tokens[index + 1]))
                    {
                        ConsoleUi.RenderWarning("Invalid --correlation value. Provide a correlation ID.");
                        return true;
                    }

                    correlationFilter = tokens[index + 1].Trim();
                    index += 2;
                    break;

                default:
                    ConsoleUi.RenderWarning($"Unknown option '{token}'. Usage: /agent-messages <scope-run-id> [agent-id] [--max N] [--consume] [--sender agent-id] [--correlation id]");
                    return true;
            }
        }

        var recipients = ResolveRecipients(agentRegistry, agentIdFilter);
        if (recipients.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(agentIdFilter))
            {
                ConsoleUi.RenderWarning($"Agent '{agentIdFilter}' is not registered.");
            }
            else
            {
                ConsoleUi.RenderWarning("No agents available for inbox inspection.");
            }

            return true;
        }

        var messages = new List<AgentMessage>();
        foreach (var recipient in recipients)
        {
            var inboxMessages = messageBus.ReadAsync(
                    new AgentMessageReadRequest(
                        SessionKey: sessionKey,
                        ScopeRunId: scopeRunId,
                        RecipientAgentId: recipient,
                        MaxMessages: maxMessages,
                        MarkAsRead: markAsRead,
                        SenderAgentId: senderFilter,
                        CorrelationId: correlationFilter),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            messages.AddRange(inboxMessages);
        }

        var ordered = messages
            .OrderBy(message => message.SentAt)
            .ToArray();
        ConsoleUi.RenderAgentMessages(
            sessionKey.Value,
            scopeRunId.Value,
            ordered,
            markAsRead,
            senderFilter,
            correlationFilter);
        return true;
    }

    private static bool HandleScopeRunsCommand(
        string command,
        SessionKey sessionKey,
        IRunHistoryStore runHistoryStore)
    {
        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var limit = 20;

        for (var i = 1; i < tokens.Length; i++)
        {
            var token = tokens[i].Trim();
            if (token.Equals("--limit", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= tokens.Length ||
                    !int.TryParse(tokens[i + 1], out var parsedLimit) ||
                    parsedLimit <= 0)
                {
                    ConsoleUi.RenderWarning("Invalid --limit value. Use a positive integer.");
                    return true;
                }

                limit = Math.Clamp(parsedLimit, 1, 100);
                i++;
                continue;
            }

            if (!int.TryParse(token, out var shorthandLimit) || shorthandLimit <= 0)
            {
                ConsoleUi.RenderWarning("Usage: /scope-runs [--limit N]");
                return true;
            }

            limit = Math.Clamp(shorthandLimit, 1, 100);
        }

        var runs = runHistoryStore.LoadBySessionAsync(sessionKey, CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .OrderByDescending(run => run.StartedAt)
            .Take(limit)
            .ToArray();

        ConsoleUi.RenderScopeRuns(sessionKey.Value, runs);
        return true;
    }

    private static IReadOnlyList<string> ResolveRecipients(IAgentRegistry agentRegistry, string? agentIdFilter)
    {
        if (!string.IsNullOrWhiteSpace(agentIdFilter))
        {
            var definition = agentRegistry.Get(agentIdFilter.Trim());
            return definition is null
                ? Array.Empty<string>()
                : [definition.AgentId];
        }

        return agentRegistry.GetAll().Select(definition => definition.AgentId).ToArray();
    }

    private static bool HandleWebSessionsCommand(string command, IWebBrowserService webBrowserService)
    {
        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1)
        {
            var snapshots = webBrowserService.GetSessionSnapshots();
            ConsoleUi.RenderWebSessionList(snapshots);
            return true;
        }

        var sessionId = tokens[1].Trim();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            ConsoleUi.RenderWarning("Usage: /web-sessions [session-id]");
            return true;
        }

        var snapshot = webBrowserService.GetSessionSnapshot(sessionId);
        if (snapshot is null)
        {
            ConsoleUi.RenderWarning($"No active web session found for '{sessionId}'.");
            return true;
        }

        ConsoleUi.RenderWebSessionDetail(snapshot);
        return true;
    }
}
