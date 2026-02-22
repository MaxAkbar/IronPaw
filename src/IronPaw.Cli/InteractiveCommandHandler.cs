using IronPaw.AgentCore.Abstractions;
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
}
