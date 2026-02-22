using IronPaw.AgentCore.Providers;

namespace IronPaw.Cli;

internal static class CliDefaults
{
    public const string QuitOption = "quit";

    public static IReadOnlyList<string> ProviderNames => ProviderDefaults.ProviderNames;

    public static IReadOnlyDictionary<string, string> ProviderDefaultModels => ProviderDefaults.DefaultModels;

    public static readonly IReadOnlyList<string> ProviderChoicesWithQuit =
    [
        .. ProviderDefaults.ProviderNames,
        QuitOption
    ];
}
