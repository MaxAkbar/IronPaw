namespace IronPaw.Cli.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void Parse_ParsesAllMajorFlags()
    {
        var options = global::CliOptions.Parse(
        [
            "--provider", "openai",
            "--model", "gpt-4o-mini",
            "--session", "s-1",
            "--prompt", "hello",
            "--profile", "Full",
            "--auto-create-skills",
            "--list-skills",
            "--no-events"
        ]);

        Assert.Equal("openai", options.Provider);
        Assert.Equal("gpt-4o-mini", options.Model);
        Assert.Equal("s-1", options.Session);
        Assert.Equal("hello", options.Prompt);
        Assert.Equal(global::IronPaw.AgentCore.Abstractions.ToolProfile.Full, options.Profile);
        Assert.True(options.AutoCreateSkills);
        Assert.True(options.ListSkills);
        Assert.True(options.NoEvents);
    }

    [Fact]
    public void Parse_UnknownOption_Throws()
    {
        Assert.Throws<ArgumentException>(() => global::CliOptions.Parse(["--unknown"]));
    }

    [Fact]
    public void TryValidate_FailsWhenPromptAndPromptFileAreBothProvided()
    {
        var options = new global::CliOptions(
            Provider: "local",
            Model: null,
            Session: "default",
            Prompt: "inline",
            PromptFile: "prompt.txt",
            Profile: global::IronPaw.AgentCore.Abstractions.ToolProfile.Coding,
            AutoCreateSkills: false,
            ListSkills: false,
            NoEvents: false,
            ShowHelp: false);

        var valid = options.TryValidate(out var error);

        Assert.False(valid);
        Assert.Contains("either --prompt or --prompt-file", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolvePromptAsync_UsesInlinePromptAndTrimsWhitespace()
    {
        var options = new global::CliOptions(
            Provider: "local",
            Model: null,
            Session: "default",
            Prompt: "  hello world  ",
            PromptFile: null,
            Profile: global::IronPaw.AgentCore.Abstractions.ToolProfile.Coding,
            AutoCreateSkills: false,
            ListSkills: false,
            NoEvents: false,
            ShowHelp: false);

        var prompt = await options.ResolvePromptAsync(CancellationToken.None);
        Assert.Equal("hello world", prompt);
    }
}
