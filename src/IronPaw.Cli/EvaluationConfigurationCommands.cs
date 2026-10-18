using System.Globalization;
using System.Text.Json;
using IronPaw.AgentCore;
using Spectre.Console;

namespace IronPaw.Cli;

internal static class EvaluationConfigurationCommands
{
    public static async Task<int> ExecuteAsync(CliOptions options, CancellationToken ct, IAnsiConsole? console = null)
    {
        console ??= AnsiConsole.Console;
        if (!options.TryValidate(out var optionError) || options.ValidateEvaluationConfigFile is null)
            return Invalid(console, optionError ?? "--validate-evaluation-config is required.");

        LiveEvaluationSettings settings;
        try
        {
            settings = await TaskGraphDocumentJson.ReadAsync<LiveEvaluationSettings>(options.ValidateEvaluationConfigFile, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Invalid(console, ex.Message);
        }

        var validation = settings.Validate();
        if (!validation.IsValid)
            return Invalid(console, string.Join(Environment.NewLine, validation.Errors));

        console.Write(new Text("Configuration valid.\n"));
        if (validation.IsReady)
            console.Write(new Text($"Live comparison enabled in configuration: provider={settings.Provider}; model={settings.Model}; " +
                $"maximum total spend={settings.Currency} {settings.MaxTotalSpend!.Value.ToString(CultureInfo.InvariantCulture)}.\n"));
        else
            console.Write(new Text("Live comparison disabled in configuration.\n"));
        RenderBoundary(console);
        return 0;
    }

    private static int Invalid(IAnsiConsole console, string error)
    {
        console.Write(new Text("Invalid evaluation configuration.\n" + error + "\n"));
        RenderBoundary(console);
        return 2;
    }

    private static void RenderBoundary(IAnsiConsole console) => console.Write(new Text(
        "Validation only; no live comparison was run. Execution requires --run-evaluation, an isolated campaign directory, and explicit local verification permission.\n"));
}
