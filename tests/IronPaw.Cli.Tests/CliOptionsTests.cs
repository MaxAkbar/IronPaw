using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Cli.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void Parse_ParsesAllMajorFlags()
    {
        var options = CliOptions.Parse(
        [
            "--provider", "openai",
            "--model", "gpt-4o-mini",
            "--session", "s-1",
            "--prompt", "hello",
            "--profile", "Full",
            "--message-ttl-days", "14",
            "--auto-create-skills",
            "--list-skills",
            "--no-events"
        ]);

        Assert.Equal("openai", options.Provider);
        Assert.Equal("gpt-4o-mini", options.Model);
        Assert.Equal("s-1", options.Session);
        Assert.Equal("hello", options.Prompt);
        Assert.Equal(ToolProfile.Full, options.Profile);
        Assert.Equal(14, options.MessageTtlDays);
        Assert.True(options.AutoCreateSkills);
        Assert.True(options.ListSkills);
        Assert.True(options.NoEvents);
    }

    [Fact]
    public void Parse_UnknownOption_Throws()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--unknown"]));
    }

    [Fact]
    public void TryValidate_FailsWhenPromptAndPromptFileAreBothProvided()
    {
        var options = new CliOptions(
            Provider: "local",
            Model: null,
            Session: "default",
            Prompt: "inline",
            PromptFile: "prompt.txt",
            Profile: ToolProfile.Coding,
            Store: StoreBackend.Json,
            MessageTtlDays: 30,
            AutoCreateSkills: false,
            ListSkills: false,
            NoEvents: false,
            ShowHelp: false);

        var valid = options.TryValidate(out var error);

        Assert.False(valid);
        Assert.Contains("either --prompt or --prompt-file", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ParsesStoreFlag()
    {
        var options = CliOptions.Parse(["--store", "sqlite", "--prompt", "hi"]);
        Assert.Equal(StoreBackend.Sqlite, options.Store);
    }

    [Fact]
    public void Parse_DefaultStoreIsJson()
    {
        var options = CliOptions.Parse(["--prompt", "hi"]);
        Assert.Equal(StoreBackend.Json, options.Store);
    }

    [Fact]
    public void Parse_ParsesMessageTtlDays()
    {
        var options = CliOptions.Parse(["--message-ttl-days", "45", "--prompt", "hi"]);
        Assert.Equal(45, options.MessageTtlDays);
    }

    [Fact]
    public void Parse_InvalidMessageTtlDays_Throws()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--message-ttl-days", "0", "--prompt", "hi"]));
    }

    [Fact]
    public void Parse_WebProfile_IsSupported()
    {
        var options = CliOptions.Parse(["--profile", "web", "--prompt", "hi"]);

        Assert.Equal(ToolProfile.Web, options.Profile);
    }

    [Fact]
    public void TryValidate_FailsWhenMessageTtlDaysNotPositive()
    {
        var options = new CliOptions(
            Provider: "local",
            Model: null,
            Session: "default",
            Prompt: "inline",
            PromptFile: null,
            Profile: ToolProfile.Coding,
            Store: StoreBackend.Json,
            MessageTtlDays: 0,
            AutoCreateSkills: false,
            ListSkills: false,
            NoEvents: false,
            ShowHelp: false);

        var valid = options.TryValidate(out var error);

        Assert.False(valid);
        Assert.Contains("message-ttl-days", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolvePromptAsync_UsesInlinePromptAndTrimsWhitespace()
    {
        var options = new CliOptions(
            Provider: "local",
            Model: null,
            Session: "default",
            Prompt: "  hello world  ",
            PromptFile: null,
            Profile: ToolProfile.Coding,
            Store: StoreBackend.Json,
            MessageTtlDays: 30,
            AutoCreateSkills: false,
            ListSkills: false,
            NoEvents: false,
            ShowHelp: false);

        var prompt = await options.ResolvePromptAsync(CancellationToken.None);
        Assert.Equal("hello world", prompt);
    }
}
