using System.Diagnostics;
using System.Text.RegularExpressions;
using IronPaw.Cli;
using Spectre.Console;

namespace IronPaw.Cli.Tests;

public sealed class EvaluationConfigurationCommandsTests
{
    [Fact]
    public async Task DisabledTemplate_IsValidWithoutProviderModelOrSpend()
    {
        using var scope = new Scope();
        var result = await scope.ExecuteAsync("""
            { "schemaVersion": 1, "enabled": false, "provider": null, "model": null, "maxTotalSpend": null, "currency": "USD" }
            """);
        Assert.Equal(0, result.Code);
        Assert.Contains("Configuration valid.", result.Output);
        Assert.Contains("Live comparison disabled", result.Output);
        AssertBoundary(result.Output);
    }

    [Fact]
    public async Task DisabledPartialConfiguration_IsValidatedWithoutFillingDefaults()
    {
        using var scope = new Scope();
        var result = await scope.ExecuteAsync("""{ "provider": "openai" }""");
        Assert.Equal(0, result.Code);
        Assert.Contains("Live comparison disabled", result.Output);
        Assert.DoesNotContain("model=", result.Output);
        AssertBoundary(result.Output);
    }

    [Fact]
    public async Task EnabledCompleteConfiguration_PrintsReviewedValuesAndValidationBoundary()
    {
        using var scope = new Scope();
        var result = await scope.ExecuteAsync("""
            { "enabled": true, "provider": "openai", "model": "[red]literal[/]", "maxTotalSpend": 2.50, "currency": "USD",
              "maxInputTokensPerRequest": 10000, "maxOutputTokensPerRequest": 1000, "inputFramingTokenAllowance": 1024,
              "pricing": { "inputPerMillion": 1, "outputPerMillion": 2, "cacheReadPerMillion": 0.1, "cacheWritePerMillion": 1.25,
                "source": "controlled fixture supplied prices", "asOfUtc": "2026-10-18T00:00:00Z" } }
            """);
        Assert.Equal(0, result.Code);
        Assert.Contains("Live comparison enabled in configuration", result.Output);
        Assert.Contains("provider=openai", result.Output);
        Assert.Contains("model=[red]literal[/]", result.Output);
        Assert.Contains("USD 2.50", result.Output);
        AssertBoundary(result.Output);
    }

    [Theory]
    [InlineData("{ \"enabled\": true }")]
    [InlineData("{ \"provider\": \"unsupported\" }")]
    [InlineData("{ \"provider\": \"local\" }")]
    [InlineData("{ \"currency\": \"usd\" }")]
    [InlineData("{ \"maxTotalSpend\": 0 }")]
    [InlineData("{ \"maxTotalSpend\": -1 }")]
    [InlineData("{ \"schemaVersion\": 2 }")]
    [InlineData("{ \"enabled\": true, \"provider\": \"openai\", \"model\": \"exact-model\" }")]
    [InlineData("{ \"enabled\": true, \"provider\": \"openai\", \"maxTotalSpend\": 5 }")]
    [InlineData("{ \"enabled\": true, \"model\": \"exact-model\", \"maxTotalSpend\": 5 }")]
    public async Task InvalidConfiguration_ReturnsTwoWithoutRunning(string json)
    {
        using var scope = new Scope();
        var result = await scope.ExecuteAsync(json);
        Assert.Equal(2, result.Code);
        Assert.Contains("Invalid evaluation configuration.", result.Output);
        AssertBoundary(result.Output);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ \"unexpected\": true }")]
    [InlineData("{ \"enabled\": false, \"Enabled\": true }")]
    [InlineData("{ \"maxTotalSpend\": \"5\" }")]
    public async Task InvalidJson_ReturnsTwoWithoutRunning(string json)
    {
        using var scope = new Scope();
        var result = await scope.ExecuteAsync(json);
        Assert.Equal(2, result.Code);
        Assert.Contains("Invalid evaluation configuration.", result.Output);
        AssertBoundary(result.Output);
    }

    [Fact]
    public async Task MissingFile_ReturnsTwoWithValidationBoundary()
    {
        using var scope = new Scope();
        var result = await scope.ExecuteOptionsAsync(CliOptions.Parse(["--validate-evaluation-config", Path.Combine(scope.Root, "missing.json")]));
        Assert.Equal(2, result.Code);
        AssertBoundary(result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Program_ValidatesBeforeCreatingWorkspaceJournalOrResolvingProvider(bool enabled)
    {
        using var scope = new Scope();
        var path = await scope.WriteAsync(enabled
            ? """
              { "enabled": true, "provider": "anthropic", "model": "explicit-model-id", "maxTotalSpend": 1,
                "maxInputTokensPerRequest": 10000, "maxOutputTokensPerRequest": 1000, "inputFramingTokenAllowance": 1024,
                "pricing": { "inputPerMillion": 1, "outputPerMillion": 2, "cacheReadPerMillion": 0.1, "cacheWritePerMillion": 1.25,
                  "source": "controlled fixture supplied prices", "asOfUtc": "2026-10-18T00:00:00Z" } }
              """
            : "{}");
        var workspace = Path.Combine(scope.Root, "workspace");
        Directory.CreateDirectory(workspace);
        var journal = AppBootstrapper.ResolveWorkflowDatabasePath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IronPaw", "state"), workspace);
        Assert.False(Directory.Exists(Path.GetDirectoryName(journal)));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliOptions).Assembly.Location);
        start.ArgumentList.Add("--validate-evaluation-config");
        start.ArgumentList.Add(path);
        start.Environment.Remove("OPENAI_API_KEY");
        start.Environment.Remove("ANTHROPIC_API_KEY");
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.True(process.ExitCode == 0, await error);
        AssertBoundary(await output);
        Assert.False(Directory.Exists(Path.GetDirectoryName(journal)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
    }

    private static void AssertBoundary(string output)
    {
        output = Regex.Replace(output, @"\s+", " ");
        Assert.Contains("Validation only; no live comparison was run.", output);
        Assert.Contains("Execution requires --run-evaluation", output);
    }

    private sealed class Scope : IDisposable
    {
        private static readonly string TestBase = Path.Combine(Path.GetTempPath(), "ironpaw-evaluation-config-tests");
        public string Root { get; } = Path.Combine(TestBase, Guid.NewGuid().ToString("n"));

        public Scope() => Directory.CreateDirectory(Root);

        public async Task<string> WriteAsync(string json)
        {
            var path = Path.Combine(Root, "evaluation.json");
            await File.WriteAllTextAsync(path, json);
            return path;
        }

        public async Task<(int Code, string Output)> ExecuteAsync(string json) =>
            await ExecuteOptionsAsync(CliOptions.Parse(["--validate-evaluation-config", await WriteAsync(json)]));

        public async Task<(int Code, string Output)> ExecuteOptionsAsync(CliOptions options)
        {
            using var writer = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No });
            console.Profile.Width = 240;
            var code = await EvaluationConfigurationCommands.ExecuteAsync(options, CancellationToken.None, console);
            return (code, writer.ToString());
        }

        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(TestBase) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Evaluation config cleanup escaped its isolated root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
