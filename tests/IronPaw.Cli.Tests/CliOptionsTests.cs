using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Cli.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void Parse_RecognizesStandaloneEvaluationConfigurationValidation()
    {
        var options = CliOptions.Parse(["--validate-evaluation-config", "evaluation.json"]);
        Assert.Equal("evaluation.json", options.ValidateEvaluationConfigFile);
        Assert.True(options.TryValidate(out var error), error);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("--prompt", "hello")]
    [InlineData("--prompt-file", "prompt.txt")]
    [InlineData("--list-skills")]
    [InlineData("--list-workflows")]
    [InlineData("--resume", "run")]
    [InlineData("--list-graphs")]
    [InlineData("--start-graph", "graph.json")]
    [InlineData("--provider", "local")]
    [InlineData("--model", "model")]
    [InlineData("--session", "default")]
    [InlineData("--profile", "Coding")]
    [InlineData("--max-total-tokens", "100")]
    [InlineData("--max-execution-seconds", "10")]
    [InlineData("--llm-context-window-tokens", "1000")]
    [InlineData("--message-ttl-days", "30")]
    [InlineData("--no-events")]
    [InlineData("--allow-local-verification")]
    [InlineData("--task", "task")]
    [InlineData("--require-approval", "write_file")]
    [InlineData("--reconcile-note", "note")]
    public void TryValidate_RejectsEvaluationConfigWithOtherCommandsOrOverrides(params string[] extra)
    {
        var options = CliOptions.Parse(["--validate-evaluation-config", "evaluation.json", .. extra]);
        Assert.False(options.TryValidate(out var error));
        Assert.Contains("standalone", error);
    }

    [Fact]
    public void Parse_RejectsRepeatedEvaluationConfigurationFile()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--validate-evaluation-config", "one.json", "--validate-evaluation-config", "two.json"]));
    }

    [Fact]
    public void TryValidate_RejectsBlankEvaluationConfigurationFile()
    {
        Assert.False(CliOptions.Parse(["--validate-evaluation-config", " "]).TryValidate(out var error));
        Assert.Contains("nonempty", error);
    }

    [Theory]
    [InlineData("--list-graphs")]
    [InlineData("--start-graph", "graph.json")]
    [InlineData("--show-graph", "graph-1")]
    [InlineData("--resume-graph", "graph-1", "--allow-local-verification")]
    [InlineData("--pause-graph", "graph-1")]
    [InlineData("--cancel-graph", "graph-1")]
    [InlineData("--recover-graph", "graph-1", "--task", "task-1")]
    [InlineData("--reconcile-graph-tool", "graph-1", "--task", "task-1", "--execution", "execution-1", "--result-file", "result.json", "--reconcile-note", "Verified tool effects")]
    [InlineData("--reconcile-graph-task", "graph-1", "--task", "task-1", "--result-file", "result.json", "--reconcile-note", "Verified workspace state")]
    [InlineData("--reconcile-graph-command", "graph-1", "--task", "task-1", "--invocation", "command-1", "--result-file", "result.json", "--reconcile-note", "Verified command outcome")]
    public void Parse_RecognizesGraphCommands(params string[] args)
    {
        var options = CliOptions.Parse(args);
        Assert.True(options.TryValidate(out var error), error);
    }

    [Theory]
    [InlineData("--list-graphs", "--prompt", "hi")]
    [InlineData("--list-graphs", "--list-workflows")]
    [InlineData("--list-graphs", "--show-graph", "graph")]
    [InlineData("--list-graphs", "--provider", "local")]
    [InlineData("--resume-graph", "graph", "--model", "different")]
    [InlineData("--start-graph", "graph.json", "--profile", "Coding")]
    [InlineData("--resume-graph", "graph", "--max-total-tokens", "100")]
    [InlineData("--resume-graph", "graph", "--max-execution-seconds", "10")]
    [InlineData("--resume-graph", "graph", "--llm-context-window-tokens", "1000")]
    [InlineData("--show-graph", " ")]
    [InlineData("--start-graph", " ")]
    [InlineData("--list-graphs", "--allow-local-verification")]
    [InlineData("--pause-graph", "graph", "--allow-local-verification")]
    [InlineData("--recover-graph", "graph")]
    [InlineData("--show-graph", "graph", "--task", "task")]
    [InlineData("--reconcile-graph-command", "graph", "--task", "task", "--result-file", "result.json", "--reconcile-note", "Verified")]
    [InlineData("--reconcile-graph-task", "graph", "--task", "task", "--invocation", "invocation", "--result-file", "result.json", "--reconcile-note", "Verified")]
    [InlineData("--reconcile-graph-task", "graph", "--task", "task", "--result-file", "result.json")]
    [InlineData("--resume-graph", "graph", "--reconcile-note", "note")]
    [InlineData("--reconcile-graph-tool", "graph", "--task", "task", "--result-file", "result.json", "--reconcile-note", "Verified")]
    [InlineData("--show-graph", "graph", "--execution", "execution")]
    [InlineData("--execution", "execution")]
    [InlineData("--task", "task")]
    [InlineData("--allow-local-verification", "--prompt", "hi")]
    [InlineData("--result-file", "result.json")]
    public void TryValidate_RejectsAmbiguousOrOverriddenGraphCommands(params string[] args)
    {
        Assert.False(CliOptions.Parse(args).TryValidate(out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

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
            MessageTtlDays: 30,
            AutoCreateSkills: false,
            ListSkills: false,
            NoEvents: false,
            ShowHelp: false);

        var valid = options.TryValidate(out var error);

        Assert.False(valid);
        Assert.Contains("either --prompt or --prompt-file", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    [InlineData("csharpdb")]
    public void Parse_RejectsObsoleteStoreFlag(string backend)
    {
        var error = Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--store", backend, "--prompt", "hi"]));
        Assert.Contains("CSharpDB", error.Message);
        Assert.Contains("not imported", error.Message);
        Assert.Throws<ArgumentException>(() => CliOptions.Parse([$"--store={backend}"]));
    }

    [Fact]
    public void Parse_ParsesWorkflowCommands()
    {
        var list = CliOptions.Parse(["--list-workflows"]);
        Assert.True(list.ListWorkflows);
        Assert.True(list.TryValidate(out _));
        var resume = CliOptions.Parse(["--resume", "run-123", "--reconcile-tool", "execution-456",
            "--reconcile-result", "result.json", "--reconcile-note", "Verified destination state"]);
        Assert.Equal("run-123", resume.ResumeRunId);
        Assert.Equal("execution-456", resume.ReconcileToolExecutionId);
        Assert.Equal("result.json", resume.ReconcileResultFile);
        Assert.Equal("Verified destination state", resume.ReconcileNote);
        Assert.True(resume.TryValidate(out _));
    }

    [Theory]
    [InlineData("--resume", "run", "--list-workflows")]
    [InlineData("--resume", "run", "--prompt", "hello")]
    [InlineData("--list-workflows", "--prompt-file", "prompt.txt")]
    [InlineData("--list-workflows", "--list-skills")]
    [InlineData("--resume", " ")]
    [InlineData("--reconcile-tool", "execution")]
    [InlineData("--resume", "run", "--reconcile-tool", "execution")]
    public void TryValidate_RejectsAmbiguousWorkflowCommands(params string[] args)
    {
        var options = CliOptions.Parse(args);
        Assert.False(options.TryValidate(out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("--pause", "run")]
    [InlineData("--cancel", "run")]
    [InlineData("--resume", "run", "--answer-request", "question", "--answer", "Use CSharpDB")]
    [InlineData("--resume", "run", "--approve-request", "approval")]
    [InlineData("--resume", "run", "--deny-request", "approval")]
    public void Parse_RecognizesDurableHumanControls(params string[] args)
    {
        var options = CliOptions.Parse(args);
        Assert.True(options.TryValidate(out var error), error);
        Assert.True(WorkflowCommands.IsWorkflowCommand(options));
    }

    [Theory]
    [InlineData("--pause", "run", "--resume", "run")]
    [InlineData("--cancel", "run", "--pause", "run")]
    [InlineData("--pause", " ")]
    [InlineData("--cancel", "run", "--prompt", "hello")]
    [InlineData("--list-workflows", "--cancel", "run")]
    [InlineData("--answer-request", "request", "--answer", "text")]
    [InlineData("--resume", "run", "--answer", "text")]
    [InlineData("--resume", "run", "--answer-request", "request")]
    [InlineData("--resume", "run", "--answer-request", "request", "--answer", " ")]
    [InlineData("--approve-request", "request")]
    [InlineData("--resume", "run", "--deny-request", " ")]
    [InlineData("--resume", "run", "--approve-request", "request", "--deny-request", "request")]
    [InlineData("--resume", "run", "--answer-request", "request", "--answer", "text", "--approve-request", "approval")]
    [InlineData("--resume", "run", "--require-approval", "write_file")]
    [InlineData("--require-approval", "write_file,")]
    [InlineData("--require-approval", "write_file,WRITE_FILE")]
    public void TryValidate_RejectsIncompleteOrConflictingHumanControls(params string[] args)
    {
        var options = CliOptions.Parse(args);
        Assert.False(options.TryValidate(out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Parse_ApprovalPolicyDefaultsToNoneAndTrimsToolNames()
    {
        Assert.Null(CliOptions.Parse(["--prompt", "hi"]).ApprovalRequiredTools);
        var options = CliOptions.Parse(["--prompt", "hi", "--require-approval", "write_file, execute_command"]);
        Assert.Equal(new[] { "write_file", "execute_command" }, options.ApprovalRequiredTools);
        Assert.True(options.TryValidate(out _));
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
    public void Parse_ParsesLlmBudgetFlags()
    {
        var options = CliOptions.Parse(
        [
            "--llm-context-window-tokens", "32768",
            "--llm-safety-margin-tokens", "1024",
            "--llm-min-output-tokens-per-request", "512",
            "--llm-max-auto-continuation-passes", "3",
            "--prompt", "hi"
        ]);

        Assert.Equal(32768, options.LlmContextWindowTokens);
        Assert.Equal(1024, options.LlmSafetyMarginTokens);
        Assert.Equal(512, options.LlmMinOutputTokensPerRequest);
        Assert.Equal(3, options.LlmMaxAutoContinuationPasses);
    }

    [Fact]
    public void Parse_ResolvesPersistentWorkflowBudgetsWithoutChangingDefaultCallLimits()
    {
        var options = CliOptions.Parse(["--max-total-tokens", "3000000000", "--max-execution-seconds", "95", "--prompt", "hello"]);

        Assert.True(options.TryValidate(out _));
        Assert.Equal(3_000_000_000L, options.MaxTotalTokens);
        Assert.Equal(95, options.MaxExecutionSeconds);
        var budget = options.ResolveWorkflowBudget();
        Assert.NotNull(budget);
        Assert.Equal(3_000_000_000L, budget.MaxTotalTokens);
        Assert.Equal(TimeSpan.FromSeconds(95), budget.MaxExecutionTime);
        Assert.Equal(32, budget.MaxModelCalls);
        Assert.Equal(64, budget.MaxToolCalls);
        Assert.Null(CliOptions.Parse(["--prompt", "hello"]).ResolveWorkflowBudget());
    }

    [Theory]
    [InlineData("--max-total-tokens", "0")]
    [InlineData("--max-total-tokens", "-1")]
    [InlineData("--max-total-tokens", "9223372036854775808")]
    [InlineData("--max-total-tokens", "invalid")]
    [InlineData("--max-execution-seconds", "0")]
    [InlineData("--max-execution-seconds", "-1")]
    [InlineData("--max-execution-seconds", "2147483648")]
    [InlineData("--max-execution-seconds", "invalid")]
    public void Parse_RejectsInvalidPersistentBudget(string option, string value)
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse([option, value]));
    }

    [Theory]
    [InlineData("--resume", "workflow", "--max-total-tokens", "1000")]
    [InlineData("--pause", "workflow", "--max-execution-seconds", "10")]
    [InlineData("--cancel", "workflow", "--max-total-tokens", "1000")]
    [InlineData("--list-workflows", "--max-execution-seconds", "10")]
    public void TryValidate_RejectsBudgetChangesDuringRecovery(params string[] args)
    {
        Assert.False(CliOptions.Parse(args).TryValidate(out var error));
        Assert.Contains("Saved limits cannot change", error);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    public void TryValidate_RejectsInvalidBudgetInDirectOptions(long tokens, int seconds)
    {
        var options = CliOptions.Parse(["--prompt", "hello"]) with { MaxTotalTokens = tokens, MaxExecutionSeconds = seconds };
        Assert.False(options.TryValidate(out var error));
        Assert.Contains("positive integers", error);
    }

    [Fact]
    public void Parse_InvalidLlmContextWindow_Throws()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--llm-context-window-tokens", "0", "--prompt", "hi"]));
    }

    [Fact]
    public void Parse_InvalidLlmAutoContinuationPasses_Throws()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--llm-max-auto-continuation-passes", "-1", "--prompt", "hi"]));
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
    public void TryValidate_FailsWhenLlmContextWindowNotPositive()
    {
        var options = new CliOptions(
            Provider: "local",
            Model: null,
            Session: "default",
            Prompt: "inline",
            PromptFile: null,
            Profile: ToolProfile.Coding,
            MessageTtlDays: 30,
            AutoCreateSkills: false,
            ListSkills: false,
            NoEvents: false,
            ShowHelp: false,
            LlmContextWindowTokens: 0);

        var valid = options.TryValidate(out var error);

        Assert.False(valid);
        Assert.Contains("llm-context-window-tokens", error, StringComparison.OrdinalIgnoreCase);
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
            MessageTtlDays: 30,
            AutoCreateSkills: false,
            ListSkills: false,
            NoEvents: false,
            ShowHelp: false);

        var prompt = await options.ResolvePromptAsync(CancellationToken.None);
        Assert.Equal("hello world", prompt);
    }
}
