using Spectre.Console;

namespace IronPaw.Cli;

internal static class ProviderResolver
{
    public static bool TryResolve(
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

    public static bool TryEnsureConfiguration(string provider, bool interactiveInputAvailable, out bool userQuitRequested)
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

    private static bool TryEnsureEnvironmentVariable(
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

    private static bool IsQuitCommand(string input)
    {
        var command = input.Trim();
        return command.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
               command.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
               command.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
               command.Equals(":q", StringComparison.OrdinalIgnoreCase);
    }
}
