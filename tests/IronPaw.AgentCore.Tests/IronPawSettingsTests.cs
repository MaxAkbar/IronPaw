using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

public sealed class IronPawSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "IronPaw-Settings-" + Guid.NewGuid().ToString("n"));
    public IronPawSettingsTests() => Directory.CreateDirectory(root);

    [Fact]
    public void AbsentUserFileUsesLocalDefaultsWithoutReadingCredentials()
    {
        var names = new List<string>();
        var settings = IronPawSettingsLoader.Load(environment: name => { names.Add(name); return null; }, defaultPath: Path.Combine(root, "absent.json"));
        Assert.Equal("local", settings.Provider);
        Assert.Null(settings.Model);
        Assert.Null(settings.MaxTotalTokens);
        Assert.Null(settings.Agents.Reviewer.Provider);
        Assert.DoesNotContain(names, name => name.Contains("API_KEY"));
        Assert.False(File.Exists(Path.Combine(root, "absent.json")));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"provider\":\"unknown\"}")]
    [InlineData("{\"provider\":null}")]
    [InlineData("{\"provider\":\" local \"}")]
    [InlineData("{\"model\":\" padded\"}")]
    [InlineData("{\"maxTotalTokens\":0}")]
    [InlineData("{\"maxExecutionSeconds\":-1}")]
    [InlineData("{\"llm\":null}")]
    [InlineData("{\"agents\":null}")]
    [InlineData("{\"agents\":{\"reviewer\":null}}")]
    [InlineData("{\"llm\":{\"contextWindowTokens\":10,\"safetyMarginTokens\":10}}")]
    [InlineData("{\"llm\":{\"contextWindowTokens\":10,\"safetyMarginTokens\":8,\"minimumOutputTokensPerRequest\":3}}")]
    [InlineData("{\"agents\":{\"reviewer\":{\"provider\":\"unknown\"}}}")]
    public void InvalidSettingsRejectBeforeStateOrProviderInitialization(string json)
    {
        var file = Write(json);
        Assert.ThrowsAny<ArgumentException>(() => IronPawSettingsLoader.Load(file, _ => null));
        Assert.Single(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData("{\"apiKey\":\"not-allowed\"}")]
    [InlineData("{\"Provider\":\"local\",\"provider\":\"openai\"}")]
    [InlineData("{\"maxTotalTokens\":\"5000\"}")]
    [InlineData("{\"schemaVersion\":\"1\"}")]
    [InlineData("{\"agents\":{\"reviewer\":{\"toolPolicy\":\"Full\"}}}")]
    [InlineData("{\"liveEvaluation\":{\"enabled\":true}}")]
    [InlineData("{\"workspaceRoot\":\"/other\"}")]
    public void StrictFileCannotSmuggleSecretsAuthorityOrEvaluationOverrides(string json) =>
        Assert.ThrowsAny<JsonException>(() => IronPawSettingsLoader.Load(Write(json), _ => null));

    [Fact]
    public void EnvironmentOverridesFileAndChangingProviderClearsIncompatibleModel()
    {
        var file = Write("""
            { "provider":"openai", "model":"file-model", "maxTotalTokens":40000,
              "llm":{"contextWindowTokens":50000,"safetyMarginTokens":100},
              "agents":{"reviewer":{"provider":"openai","model":"review-file"}} }
            """);
        var env = new Dictionary<string, string> { ["IRONPAW_PROVIDER"] = "anthropic", ["IRONPAW_REVIEWER_PROVIDER"] = "lmstudio",
            ["IRONPAW_LLM_CONTEXT_WINDOW_TOKENS"] = "60000", ["IRONPAW_CODEGEN_MODEL"] = "code-env" };
        var settings = IronPawSettingsLoader.Load(file, name => env.GetValueOrDefault(name));
        Assert.Equal("anthropic", settings.Provider);
        Assert.Null(settings.Model);
        Assert.Equal(40000, settings.MaxTotalTokens);
        Assert.Equal(60000, settings.Llm.ContextWindowTokens);
        Assert.Equal(100, settings.Llm.SafetyMarginTokens);
        Assert.Equal("lmstudio", settings.Agents.Reviewer.Provider);
        Assert.Null(settings.Agents.Reviewer.Model);
        Assert.Equal("code-env", settings.Agents.Codegen.Model);
    }

    [Fact]
    public void ExplicitFileWinsEnvironmentPathAndNoRepositoryFileIsDiscovered()
    {
        var user = Write("{\"provider\":\"openai\",\"model\":\"file-model\"}");
        var other = Path.Combine(root, "other.json"); File.WriteAllText(other, "{\"provider\":\"anthropic\"}");
        Assert.Equal("openai", IronPawSettingsLoader.Load(user, name => name == "IRONPAW_CONFIG" ? other : null).Provider);
        Assert.Equal("anthropic", IronPawSettingsLoader.Load(environment: name => name == "IRONPAW_CONFIG" ? other : null).Provider);
        Assert.Equal("local", IronPawSettingsLoader.Load(environment: _ => null, defaultPath: Path.Combine(root, "missing-user.json")).Provider);
        Assert.Throws<FileNotFoundException>(() => IronPawSettingsLoader.Load(Path.Combine(root, "missing.json"), _ => null));
    }

    [Fact]
    public void SharedCatalogChangesRoutingAndKeepsReviewerReadOnly()
    {
        var settings = new IronPawSettings { Agents = new() { Reviewer = new("OPENAI", "review-model"), Codegen = new("anthropic", "code-model") } };
        var registry = new InMemoryAgentRegistry();
        DefaultAgentCatalog.Register(registry, settings);
        Assert.Equal("openai", registry.Get("reviewer")!.Provider);
        Assert.Equal("code-model", registry.Get("codegen")!.Model);
        var tools = new InMemoryToolRegistry(BuiltInToolFactory.CreateDefault(root)).ResolveTools(registry.Get("reviewer")!.ToolPolicy with { SenderIsOwner = true });
        Assert.Contains(tools, tool => tool.Descriptor.Name == "read_file");
        Assert.DoesNotContain(tools, tool => tool.Descriptor.Name == "write_file");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("not-a-number")]
    [InlineData("2147483648")]
    public void MalformedEnvironmentDoesNotSilentlyFallback(string value) => Assert.Throws<ArgumentException>(() =>
        IronPawSettingsLoader.Load(environment: name => name == "IRONPAW_LLM_CONTEXT_WINDOW_TOKENS" ? value : null, defaultPath: Path.Combine(root, "missing.json")));

    private string Write(string json) { var path = Path.Combine(root, "settings.json"); File.WriteAllText(path, json); return path; }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
