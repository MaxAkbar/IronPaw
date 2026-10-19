using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class IronPawSettingsTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "IronPaw-Settings-" + Guid.NewGuid().ToString("n"));
    public IronPawSettingsTests() => Directory.CreateDirectory(root);

    [TestMethod]
    public void AbsentUserFileUsesLocalDefaultsWithoutReadingCredentials()
    {
        var names = new List<string>();
        var settings = IronPawSettingsLoader.Load(environment: name => { names.Add(name); return null; }, defaultPath: Path.Combine(root, "absent.json"));
        Assert.AreEqual("local", settings.Provider);
        Assert.IsNull(settings.Model);
        Assert.IsNull(settings.MaxTotalTokens);
        Assert.IsNull(settings.Agents.Reviewer.Provider);
        Assert.DoesNotContain(name => name.Contains("API_KEY"), names);
        Assert.IsFalse(File.Exists(Path.Combine(root, "absent.json")));
    }

    [TestMethod]
    [DataRow("{\"schemaVersion\":2}")]
    [DataRow("{\"provider\":\"unknown\"}")]
    [DataRow("{\"provider\":null}")]
    [DataRow("{\"provider\":\" local \"}")]
    [DataRow("{\"model\":\" padded\"}")]
    [DataRow("{\"maxTotalTokens\":0}")]
    [DataRow("{\"maxExecutionSeconds\":-1}")]
    [DataRow("{\"llm\":null}")]
    [DataRow("{\"agents\":null}")]
    [DataRow("{\"agents\":{\"reviewer\":null}}")]
    [DataRow("{\"llm\":{\"contextWindowTokens\":10,\"safetyMarginTokens\":10}}")]
    [DataRow("{\"llm\":{\"contextWindowTokens\":10,\"safetyMarginTokens\":8,\"minimumOutputTokensPerRequest\":3}}")]
    [DataRow("{\"agents\":{\"reviewer\":{\"provider\":\"unknown\"}}}")]
    public void InvalidSettingsRejectBeforeStateOrProviderInitialization(string json)
    {
        var file = Write(json);
        Assert.Throws<ArgumentException>(() => IronPawSettingsLoader.Load(file, _ => null));
        Assert.ContainsSingle(Directory.GetFiles(root));
    }

    [TestMethod]
    [DataRow("{\"apiKey\":\"not-allowed\"}")]
    [DataRow("{\"Provider\":\"local\",\"provider\":\"openai\"}")]
    [DataRow("{\"maxTotalTokens\":\"5000\"}")]
    [DataRow("{\"schemaVersion\":\"1\"}")]
    [DataRow("{\"agents\":{\"reviewer\":{\"toolPolicy\":\"Full\"}}}")]
    [DataRow("{\"liveEvaluation\":{\"enabled\":true}}")]
    [DataRow("{\"workspaceRoot\":\"/other\"}")]
    public void StrictFileCannotSmuggleSecretsAuthorityOrEvaluationOverrides(string json) =>
        Assert.Throws<JsonException>(() => IronPawSettingsLoader.Load(Write(json), _ => null));

    [TestMethod]
    public void EnvironmentOverridesFileAndChangingProviderClearsIncompatibleModel()
    {
        var file = Write("""
            { "provider":"openai", "model":"file-model", "maxTotalTokens":40000,
              "llm":{"contextWindowTokens":50000,"safetyMarginTokens":100},
              "agents":{"reviewer":{"provider":"openai","model":"review-file"}} }
            """);
        var env = new Dictionary<string, string>
        {
            ["IRONPAW_PROVIDER"] = "anthropic",
            ["IRONPAW_REVIEWER_PROVIDER"] = "lmstudio",
            ["IRONPAW_LLM_CONTEXT_WINDOW_TOKENS"] = "60000",
            ["IRONPAW_CODEGEN_MODEL"] = "code-env"
        };
        var settings = IronPawSettingsLoader.Load(file, name => env.GetValueOrDefault(name));
        Assert.AreEqual("anthropic", settings.Provider);
        Assert.IsNull(settings.Model);
        Assert.AreEqual(40000, settings.MaxTotalTokens);
        Assert.AreEqual(60000, settings.Llm.ContextWindowTokens);
        Assert.AreEqual(100, settings.Llm.SafetyMarginTokens);
        Assert.AreEqual("lmstudio", settings.Agents.Reviewer.Provider);
        Assert.IsNull(settings.Agents.Reviewer.Model);
        Assert.AreEqual("code-env", settings.Agents.Codegen.Model);
    }

    [TestMethod]
    public void ExplicitFileWinsEnvironmentPathAndNoRepositoryFileIsDiscovered()
    {
        var user = Write("{\"provider\":\"openai\",\"model\":\"file-model\"}");
        var other = Path.Combine(root, "other.json"); File.WriteAllText(other, "{\"provider\":\"anthropic\"}");
        Assert.AreEqual("openai", IronPawSettingsLoader.Load(user, name => name == "IRONPAW_CONFIG" ? other : null).Provider);
        Assert.AreEqual("anthropic", IronPawSettingsLoader.Load(environment: name => name == "IRONPAW_CONFIG" ? other : null).Provider);
        Assert.AreEqual("local", IronPawSettingsLoader.Load(environment: _ => null, defaultPath: Path.Combine(root, "missing-user.json")).Provider);
        Assert.ThrowsExactly<FileNotFoundException>(() => IronPawSettingsLoader.Load(Path.Combine(root, "missing.json"), _ => null));
    }

    [TestMethod]
    public void SharedCatalogChangesRoutingAndKeepsReviewerReadOnly()
    {
        var settings = new IronPawSettings { Agents = new() { Reviewer = new("OPENAI", "review-model"), Codegen = new("anthropic", "code-model") } };
        var registry = new InMemoryAgentRegistry();
        DefaultAgentCatalog.Register(registry, settings);
        Assert.AreEqual("openai", registry.Get("reviewer")!.Provider);
        Assert.AreEqual("code-model", registry.Get("codegen")!.Model);
        var tools = new InMemoryToolRegistry(BuiltInToolFactory.CreateDefault(root)).ResolveTools(registry.Get("reviewer")!.ToolPolicy with { SenderIsOwner = true });
        Assert.Contains(tool => tool.Descriptor.Name == "read_file", tools);
        Assert.DoesNotContain(tool => tool.Descriptor.Name == "write_file", tools);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("not-a-number")]
    [DataRow("2147483648")]
    public void MalformedEnvironmentDoesNotSilentlyFallback(string value) => Assert.ThrowsExactly<ArgumentException>(() =>
        IronPawSettingsLoader.Load(environment: name => name == "IRONPAW_LLM_CONTEXT_WINDOW_TOKENS" ? value : null, defaultPath: Path.Combine(root, "missing.json")));

    private string Write(string json) { var path = Path.Combine(root, "settings.json"); File.WriteAllText(path, json); return path; }
    [TestCleanup]
    public void Cleanup() => Directory.Delete(root, recursive: true);
}
