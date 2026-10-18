using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.Cli;
using Spectre.Console;

namespace IronPaw.Cli.Tests;

public sealed class LiveEvaluationCommandsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"enabled\": true }")]
    [InlineData("{ \"provider\": \"local\" }")]
    [InlineData("not JSON")]
    [InlineData("{ \"enabled\": false, \"Enabled\": true }")]
    public async Task DisabledOrInvalidConfigRejectsBeforeClientJournalOrOutputCreation(string json)
    {
        using var scope = new Scope();
        var file = await scope.WriteAsync(json);
        var result = await scope.ExecuteAsync(["--run-evaluation", file, "--evaluation-output", scope.Output, "--allow-local-verification"]);
        Assert.Equal(2, result.Code);
        Assert.Equal(0, scope.ClientFactories);
        Assert.False(Directory.Exists(scope.Output));
        Assert.Contains("Evaluation command rejected", result.Text);
    }

    [Theory]
    [InlineData("--show-evaluation")]
    [InlineData("--resume-evaluation")]
    [InlineData("--reconcile-evaluation")]
    public async Task MissingJournalDoesNotCreateDirectoriesOrResolveCredentials(string command)
    {
        using var scope = new Scope();
        var arguments = new List<string> { command, "missing", "--evaluation-output", scope.Output };
        if (command == "--resume-evaluation") arguments.Add("--allow-local-verification");
        if (command == "--reconcile-evaluation") arguments.AddRange(["--request", "missing", "--result-file", "missing.json", "--reconcile-note", "verified"]);
        var result = await scope.ExecuteAsync(arguments.ToArray());
        Assert.Equal(2, result.Code);
        Assert.Contains("no evaluation.cdb", result.Text);
        Assert.False(Directory.Exists(scope.Output));
        Assert.Equal(0, scope.ClientFactories);
    }

    [Theory]
    [InlineData("--show-evaluation")]
    [InlineData("--resume-evaluation")]
    [InlineData("--reconcile-evaluation")]
    public async Task MissingCampaignRejectsBeforeClientOrReceiptFileAccess(string command)
    {
        using var scope = new Scope();
        await using (var _ = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(scope.Output, "evaluation.cdb"))) { }
        var arguments = new List<string> { command, "missing", "--evaluation-output", scope.Output };
        if (command == "--resume-evaluation") arguments.Add("--allow-local-verification");
        if (command == "--reconcile-evaluation") arguments.AddRange(["--request", "missing", "--result-file", "missing.json", "--reconcile-note", "verified"]);
        var result = await scope.ExecuteAsync(arguments.ToArray());
        Assert.Equal(2, result.Code);
        Assert.Contains("No saved evaluation campaign", result.Text);
        Assert.DoesNotContain("missing.json", result.Text);
        Assert.Equal(0, scope.ClientFactories);
    }

    [Fact]
    public async Task FreshRunRejectsNonemptyOutputBeforeProviderOrJournal()
    {
        using var scope = new Scope();
        Directory.CreateDirectory(scope.Output);
        var marker = Path.Combine(scope.Output, "preserve.txt");
        await File.WriteAllTextAsync(marker, "preserved");
        var config = await scope.WriteAsync(TaskGraphDocumentJson.Serialize(EvaluationProviderClientTests.Settings()));
        var result = await scope.ExecuteAsync(["--run-evaluation", config, "--evaluation-output", scope.Output, "--allow-local-verification"]);
        Assert.Equal(2, result.Code);
        Assert.Contains("new or empty", result.Text);
        Assert.Equal("preserved", await File.ReadAllTextAsync(marker));
        Assert.False(File.Exists(Path.Combine(scope.Output, "evaluation.cdb")));
        Assert.Equal(0, scope.ClientFactories);
    }

    [Fact]
    public async Task ForeignCampaignRejectsBeforeReadingReceiptOrCreatingProvider()
    {
        using var scope = new Scope();
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(scope.Output, "evaluation.cdb")))
            await ((ILiveEvaluationStore)store).CreateEvaluationAsync(new("foreign", "openai", "exact-model", "USD", 1, "{}"),
                new CallerIdentity("different-owner", "Different owner", true), CancellationToken.None);
        var result = await scope.ExecuteAsync(["--reconcile-evaluation", "foreign", "--evaluation-output", scope.Output,
            "--request", "request", "--result-file", "must-not-read-this-file.json", "--reconcile-note", "verified"]);
        Assert.Equal(2, result.Code);
        Assert.DoesNotContain("must-not-read-this-file.json", result.Text);
        Assert.Equal(0, scope.ClientFactories);
    }

    [Fact]
    public async Task FreshRunRejectsOutputInsideCurrentWorkspaceWithoutCreatingIt()
    {
        using var scope = new Scope();
        var config = await scope.WriteAsync(TaskGraphDocumentJson.Serialize(EvaluationProviderClientTests.Settings()));
        var output = Path.Combine(Directory.GetCurrentDirectory(), ".evaluation-rejection-" + Guid.NewGuid().ToString("n"));
        var result = await scope.ExecuteAsync(["--run-evaluation", config, "--evaluation-output", output, "--allow-local-verification"]);
        Assert.Equal(2, result.Code);
        Assert.Contains("outside the current source workspace", result.Text);
        Assert.False(Directory.Exists(output));
        Assert.Equal(0, scope.ClientFactories);
    }

    [Fact]
    public async Task ProgramDisabledRunDoesNotBootstrapApplicationStateOrReadProviderConfiguration()
    {
        using var scope = new Scope();
        var config = await scope.WriteAsync("{}");
        var workspace = Path.Combine(scope.Root, "workspace");
        Directory.CreateDirectory(workspace);
        var journal = AppBootstrapper.ResolveWorkflowDatabasePath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IronPaw", "state"), workspace);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { typeof(CliOptions).Assembly.Location, "--run-evaluation", config,
            "--evaluation-output", scope.Output, "--allow-local-verification" }) start.ArgumentList.Add(argument);
        start.Environment.Remove("OPENAI_API_KEY");
        start.Environment.Remove("ANTHROPIC_API_KEY");
        start.Environment["IRONPAW_LLM_CONTEXT_WINDOW_TOKENS"] = "not-a-number";
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 2, await error);
        Assert.Contains("Live evaluation is disabled", await output);
        Assert.False(Directory.Exists(Path.GetDirectoryName(journal)));
        Assert.False(Directory.Exists(scope.Output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
    }

    [Fact]
    public async Task ControlledPairRunsThroughCliAndReopensForInspectAndCompletedResumeWithoutProviderCalls()
    {
        using var scope = new Scope();
        var settings = EvaluationProviderClientTests.Settings() with { PairedTrials = 1, MaxInputTokensPerRequest = 1_000_000, MaxTotalSpend = 10 };
        var config = await scope.WriteAsync(TaskGraphDocumentJson.Serialize(settings));
        var provider = new CorpusWriter();
        var started = await scope.ExecuteAsync(["--run-evaluation", config, "--evaluation-output", scope.Output, "--allow-local-verification"], _ => provider);
        Assert.True(started.Code == 0, started.Text);
        Assert.Contains("Live baseline qualification: NOT_ESTABLISHED", started.Text);
        var reportPath = Path.Combine(scope.Output, "report.json");
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        var id = report.RootElement.GetProperty("campaignId").GetString()!;
        Assert.Equal("Completed", report.RootElement.GetProperty("status").GetString());
        var trials = report.RootElement.GetProperty("trials").EnumerateArray().ToArray();
        Assert.Equal(2, trials.Length);
        Assert.All(trials, trial => Assert.True(trial.GetProperty("verifiedCompletion").GetBoolean()));
        Assert.True(provider.Calls > 0);
        var calls = provider.Calls;
        var shown = await scope.ExecuteAsync(["--show-evaluation", id, "--evaluation-output", scope.Output]);
        Assert.Equal(0, shown.Code);
        Assert.Contains("Completed", shown.Text);
        var resumed = await scope.ExecuteAsync(["--resume-evaluation", id, "--evaluation-output", scope.Output, "--allow-local-verification"], _ => provider);
        Assert.True(resumed.Code == 0, resumed.Text);
        Assert.Equal(calls, provider.Calls);
        foreach (var command in new[] { "--reconcile-evaluation-command", "--reconcile-evaluation-task", "--reconcile-evaluation-tool" })
        {
            var arguments = new List<string> { command, id, "--evaluation-output", scope.Output, "--evaluation-graph", "foreign-graph", "--task", "task",
                "--result-file", "must-not-read-this-file.json", "--reconcile-note", "verified" };
            if (command.EndsWith("-command", StringComparison.Ordinal)) arguments.AddRange(["--invocation", "invocation"]);
            if (command.EndsWith("-tool", StringComparison.Ordinal)) arguments.AddRange(["--tool-execution", "execution"]);
            var rejected = await scope.ExecuteAsync(arguments.ToArray());
            Assert.Equal(2, rejected.Code);
            Assert.Contains("does not belong", rejected.Text);
            Assert.DoesNotContain("must-not-read-this-file.json", rejected.Text);
            Assert.Equal(calls, provider.Calls);
        }
    }

    [Fact]
    public async Task UnknownPaidRequestCanBeInspectedAndReconciledWithoutAutomaticProviderRetry()
    {
        using var scope = new Scope();
        var settings = EvaluationProviderClientTests.Settings() with { PairedTrials = 1, MaxInputTokensPerRequest = 1_000_000, MaxTotalSpend = 10 };
        var config = await scope.WriteAsync(TaskGraphDocumentJson.Serialize(settings));
        var provider = new InterruptedClient();
        var started = await scope.ExecuteAsync(["--run-evaluation", config, "--evaluation-output", scope.Output, "--allow-local-verification"], _ => provider);
        Assert.Equal(2, started.Code);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(scope.Output, "report.json")));
        var id = report.RootElement.GetProperty("campaignId").GetString()!;
        Assert.Equal("NeedsReconciliation", report.RootElement.GetProperty("status").GetString());
        LiveEvaluationCheckpoint checkpoint;
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(scope.Output, "evaluation.cdb")))
            checkpoint = (await ((ILiveEvaluationStore)store).GetEvaluationAsync(id, CallerIdentity.LocalOwner(), CancellationToken.None))!;
        var request = Assert.Single(checkpoint.Requests);
        Assert.Equal(LiveEvaluationRequestState.NeedsReconciliation, request.State);
        var calls = provider.Calls;
        var resumed = await scope.ExecuteAsync(["--resume-evaluation", id, "--evaluation-output", scope.Output, "--allow-local-verification"], _ => provider);
        Assert.Equal(2, resumed.Code);
        Assert.Equal(calls, provider.Calls);
        var shown = await scope.ExecuteAsync(["--show-evaluation", id, "--evaluation-output", scope.Output]);
        Assert.Equal(0, shown.Code);
        var receiptPath = await scope.WriteAsync(TaskGraphDocumentJson.Serialize(new LiveEvaluationReceipt(true, request.Receipt!.ActualSpend, 20, 0,
            "{\"source\":\"controlled external usage fixture\"}")));
        var reconciled = await scope.ExecuteAsync(["--reconcile-evaluation", id, "--evaluation-output", scope.Output,
            "--request", request.Request.RequestId, "--result-file", receiptPath, "--reconcile-note", "Verified controlled provider usage"]);
        Assert.True(reconciled.Code == 0, reconciled.Text);
        Assert.Equal(calls, provider.Calls);
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(scope.Output, "evaluation.cdb"));
        var saved = (await ((ILiveEvaluationStore)reopened).GetEvaluationAsync(id, CallerIdentity.LocalOwner(), CancellationToken.None))!;
        Assert.False(saved.NeedsReconciliation);
        Assert.Equal("Verified controlled provider usage", Assert.Single(saved.Requests).ReconciliationNote);
    }

    private sealed class InterruptedClient : ILlmClient
    {
        public int Calls;
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Interlocked.Increment(ref Calls);
            yield return new UsageDelta(20, null, 0, 0);
        }
    }

    private sealed class CorpusWriter : ILlmClient
    {
        public int Calls;
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Calls);
            var input = string.Join("\n", request.Messages.Select(message => message.Content));
            var hasResults = request.Messages.Any(message => message.Role == ChatRole.Tool);
            if (!hasResults && !input.Contains("Role: join", StringComparison.Ordinal))
            {
                if (input.Contains("Role: code", StringComparison.Ordinal) || input.Contains("Role: direct", StringComparison.Ordinal))
                    yield return new ToolCallDelta("code", "write_file", JsonSerializer.SerializeToElement(new { path = "Calc.cs", content = "public static class Calc { public static int Add(int left, int right) => checked(left + right); }" }));
                if (input.Contains("Role: docs", StringComparison.Ordinal) || input.Contains("Role: direct", StringComparison.Ordinal))
                    yield return new ToolCallDelta("docs", "write_file", JsonSerializer.SerializeToElement(new { path = "README.md", content = "Calc.Add uses checked integer addition." }));
            }
            yield return new AssistantTextDelta("Controlled fixture changes completed.");
            yield return new UsageDelta(20, 10, 0, 0);
            yield return new CompletedDelta();
        }
    }

    private sealed class Scope : IDisposable
    {
        private static readonly string TestBase = Path.Combine(Path.GetTempPath(), "ironpaw-live-evaluation-cli-tests");
        public string Root { get; } = Path.Combine(TestBase, Guid.NewGuid().ToString("n"));
        public string Output => Path.Combine(Root, "campaign");
        public int ClientFactories { get; private set; }
        public Scope() => Directory.CreateDirectory(Root);
        public async Task<string> WriteAsync(string json)
        {
            var path = Path.Combine(Root, "config.json");
            await File.WriteAllTextAsync(path, json);
            return path;
        }
        public async Task<(int Code, string Text)> ExecuteAsync(string[] arguments, Func<LiveEvaluationSettings, ILlmClient>? create = null)
        {
            using var text = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(text), Ansi = AnsiSupport.No });
            console.Profile.Width = 240;
            var code = await LiveEvaluationCommands.ExecuteAsync(CliOptions.Parse(arguments), CancellationToken.None, console,
                settings => { ClientFactories++; return create?.Invoke(settings) ?? throw new InvalidOperationException("Provider factory must remain deferred."); });
            return (code, text.ToString());
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(TestBase) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("CLI campaign cleanup escaped its isolated root.");
            if (Directory.Exists(resolved))
            {
                foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
