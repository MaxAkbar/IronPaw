using System.Diagnostics;
using IronPaw.AgentCore;
using IronPaw.Cli;

namespace IronPaw.Cli.Tests;

[TestClass]
public sealed class ApplicationSettingsTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "IronPaw-CliSettings-" + Guid.NewGuid().ToString("n"));
    public ApplicationSettingsTests() => Directory.CreateDirectory(root);

    [TestMethod]
    public void FileDefaultsApplyAndExplicitCliDefaultsStillWin()
    {
        var settings = new IronPawSettings
        {
            Provider = "openai",
            Model = "file-model",
            MaxTotalTokens = 4000,
            MaxExecutionSeconds = 60,
            Llm = new() { ContextWindowTokens = 10000 }
        };
        var defaults = CliOptions.Parse(["--prompt", "hello"]).WithSettings(settings);
        Assert.AreEqual("openai", defaults.Provider); Assert.AreEqual("file-model", defaults.Model);
        Assert.AreEqual(4000, defaults.MaxTotalTokens); Assert.AreEqual(60, defaults.MaxExecutionSeconds);
        var explicitOptions = CliOptions.Parse(["--provider", "local", "--max-total-tokens", "500", "--llm-context-window-tokens", "20000"]).WithSettings(settings);
        Assert.AreEqual("local", explicitOptions.Provider); Assert.IsNull(explicitOptions.Model);
        Assert.AreEqual(500, explicitOptions.MaxTotalTokens); Assert.AreEqual(20000, explicitOptions.LlmContextWindowTokens);
        Assert.AreEqual("cli-model", CliOptions.Parse(["--model", "cli-model"]).WithSettings(settings).Model);
    }

    [TestMethod]
    [DataRow("--validate-config", "file.json", "--prompt", "hello")]
    [DataRow("--config", "file.json", "--list-workflows")]
    [DataRow("--config", "file.json", "--show-graph", "graph")]
    [DataRow("--config", "file.json", "--run-evaluation", "eval.json", "--evaluation-output", "C:/Eval/campaign", "--allow-local-verification")]
    public void RuntimeFileCannotChangeSavedBindingsOrEvaluationSettings(params string[] arguments) =>
        Assert.IsFalse(CliOptions.Parse(arguments).TryValidate(out _));

    [TestMethod]
    public void BothHostsCanConsumeSameValidatedRoutingSnapshot()
    {
        var settings = new IronPawSettings { Agents = new() { Reviewer = new("openai", "same-reviewer"), WebBrowser = new("lmstudio", "browser-model") } };
        using var bootstrap = AppBootstrapper.Build(stateRoot: Path.Combine(root, "state"), workspaceRoot: root, settings: settings);
        Assert.AreEqual("same-reviewer", bootstrap.AgentRegistry.Get("reviewer")!.Model);
        Assert.AreEqual("browser-model", bootstrap.AgentRegistry.Get("web_browser")!.Model);
        Assert.AreEqual("local", bootstrap.Settings.Provider);
    }

    [TestMethod]
    public void CombinedFileAndCliLimitsMustLeaveUsableContext()
    {
        var settings = new IronPawSettings { Llm = new() { SafetyMarginTokens = 500 } };
        Assert.ThrowsExactly<ArgumentException>(() => CliOptions.Parse(["--llm-context-window-tokens", "100"]).WithSettings(settings));
    }

    [TestMethod]
    public void SavedWorkPreservesLegacySpecialistRoutingWithoutExpandingPermissions()
    {
        var registry = new IronPaw.AgentCore.Tools.InMemoryAgentRegistry();
        DefaultAgentCatalog.Register(registry, new());
        var original = registry.Get("reviewer")!;
        var values = new Dictionary<string, string> { ["IRONPAW_REVIEWER_PROVIDER"] = "openai", ["IRONPAW_REVIEWER_MODEL"] = "saved-review-model" };
        AppBootstrapper.ApplyLegacyAgentRouting(registry, name => values.GetValueOrDefault(name));
        var routed = registry.Get("reviewer")!;
        Assert.AreEqual("openai", routed.Provider); Assert.AreEqual("saved-review-model", routed.Model);
        Assert.AreEqual(original.ToolPolicy, routed.ToolPolicy); Assert.AreEqual(original.SystemPrompt, routed.SystemPrompt);
        Assert.IsNull(registry.Get("codegen")!.Provider);
    }

    [TestMethod]
    [DataRow("{\"schemaVersion\":1,\"provider\":\"local\"}", 0)]
    [DataRow("{\"apiKey\":\"reject-secret-fields\"}", 2)]
    public async Task ValidationIsIndependentOfProviderEnvironmentAndCreatesNoState(string json, int expected)
    {
        var path = Path.Combine(root, "settings.json"); await File.WriteAllTextAsync(path, json);
        var result = await Run(["--validate-config", path]);
        Assert.AreEqual(expected, result.Code);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "appdata", "IronPaw")));
        if (expected == 0) Assert.Contains("No application state was opened", result.Output);
    }

    [TestMethod]
    public async Task ObsoleteStoreFailsBeforeBrokenSettingsProviderOrStateInitialization()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "broken");
        var result = await Run(["--store", "sqlite", "--provider", "openai", "--prompt", "hello"]);
        Assert.AreEqual(2, result.Code);
        Assert.Contains("--store has been removed", result.Output);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "appdata", "IronPaw")));
    }

    [TestMethod]
    public async Task ExplicitCliProviderWinsSharedFileAndSavedInspectionIgnoresBrokenSettings()
    {
        var path = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(path, "{\"provider\":\"openai\",\"model\":\"file-model\"}");
        var result = await Run(["--config", path, "--provider", "local", "--list-skills"], invalidEnvironment: false);
        Assert.AreEqual(0, result.Code); Assert.DoesNotContain("file-model", result.Output);
        await File.WriteAllTextAsync(path, "invalid settings");
        var inspected = await Run(["--list-workflows"]);
        Assert.AreEqual(0, inspected.Code);
        var newRun = await Run(["--list-skills"]);
        Assert.AreEqual(2, newRun.Code);
        Assert.Contains("Invalid application settings", newRun.Output);
    }

    private async Task<(int Code, string Output)> Run(string[] arguments, bool invalidEnvironment = true)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        info.ArgumentList.Add(typeof(CliOptions).Assembly.Location);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["LOCALAPPDATA"] = Path.Combine(root, "appdata");
        info.Environment["IRONPAW_CONFIG"] = Path.Combine(root, "settings.json");
        info.Environment["IRONPAW_LLM_CONTEXT_WINDOW_TOKENS"] = invalidEnvironment ? "invalid" : "10000";
        info.Environment["OPENAI_API_KEY"] = ""; info.Environment["ANTHROPIC_API_KEY"] = "";
        using var process = Process.Start(info)!; process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output + await error);
    }
    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(root, recursive: true);
    }
}
