using IronPaw.AgentCore.Abstractions;
using IronPaw.Cli;

namespace IronPaw.Cli.Tests;

public sealed class AppBootstrapperTests
{
    [Fact]
    public void Build_RegistersWebBrowserSpecialistAgent()
    {
        using var bootstrap = AppBootstrapper.Build(StoreBackend.Json);

        var webBrowser = bootstrap.AgentRegistry.Get("web_browser");

        Assert.NotNull(webBrowser);
        Assert.Equal(ToolProfile.Web, webBrowser!.ToolPolicy.Profile);
        Assert.Contains("web pages", webBrowser.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("web_navigate", webBrowser.ToolPolicy.AllowList ?? []);
        Assert.Contains("web_extract_structured", webBrowser.ToolPolicy.AllowList ?? []);
    }

    [Fact]
    public void Build_RestrictsCodegenAndReviewerFromDirectWebTools()
    {
        using var bootstrap = AppBootstrapper.Build(StoreBackend.Json);

        var codegen = bootstrap.AgentRegistry.Get("codegen");
        var reviewer = bootstrap.AgentRegistry.Get("reviewer");

        Assert.NotNull(codegen);
        Assert.NotNull(reviewer);
        Assert.Contains("web_navigate", codegen!.ToolPolicy.DenyList ?? []);
        Assert.Contains("web_extract_structured", codegen.ToolPolicy.DenyList ?? []);
        Assert.Contains("web_navigate", reviewer!.ToolPolicy.DenyList ?? []);
        Assert.Contains("web_extract_structured", reviewer.ToolPolicy.DenyList ?? []);
        Assert.Contains("web_browser", codegen.AllowedMessageSenders ?? []);
        Assert.Contains("web_browser", reviewer.AllowedMessageSenders ?? []);
    }
}
