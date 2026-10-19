using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Cli.Tests;

[TestClass]
public sealed class CliOptionsTests
{
    [TestMethod]
    public void Parse_RecognizesStandaloneEvaluationConfigurationValidation()
    {
        var options = CliOptions.Parse(["--validate-evaluation-config", "evaluation.json"]);
        Assert.AreEqual("evaluation.json", options.ValidateEvaluationConfigFile);
        Assert.IsTrue(options.TryValidate(out var error), error);
    }

    [TestMethod]
    [DataRow("hello")]
    [DataRow("--prompt", "hello")]
    [DataRow("--prompt-file", "prompt.txt")]
    [DataRow("--list-skills")]
    [DataRow("--list-workflows")]
    [DataRow("--resume", "run")]
    [DataRow("--list-graphs")]
    [DataRow("--start-graph", "graph.json")]
    [DataRow("--provider", "local")]
    [DataRow("--model", "model")]
    [DataRow("--session", "default")]
    [DataRow("--profile", "Coding")]
    [DataRow("--max-total-tokens", "100")]
    [DataRow("--max-execution-seconds", "10")]
    [DataRow("--llm-context-window-tokens", "1000")]
    [DataRow("--message-ttl-days", "30")]
    [DataRow("--no-events")]
    [DataRow("--allow-local-verification")]
    [DataRow("--task", "task")]
    [DataRow("--require-approval", "write_file")]
    [DataRow("--reconcile-note", "note")]
    public void TryValidate_RejectsEvaluationConfigWithOtherCommandsOrOverrides(params string[] extra)
    {
        var options = CliOptions.Parse(["--validate-evaluation-config", "evaluation.json", .. extra]);
        Assert.IsFalse(options.TryValidate(out var error));
        Assert.IsNotNull(error);
        Assert.Contains("standalone", error);
    }

    [TestMethod]
    public void Parse_RejectsRepeatedEvaluationConfigurationFile()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CliOptions.Parse(["--validate-evaluation-config", "one.json", "--validate-evaluation-config", "two.json"]));
    }

    [TestMethod]
    public void TryValidate_RejectsBlankEvaluationConfigurationFile()
    {
        Assert.IsFalse(CliOptions.Parse(["--validate-evaluation-config", " "]).TryValidate(out var error));
        Assert.IsNotNull(error);
        Assert.Contains("nonempty", error);
    }

    [TestMethod]
    [DataRow("--list-graphs")]
    [DataRow("--start-graph", "graph.json")]
    [DataRow("--show-graph", "graph-1")]
    [DataRow("--resume-graph", "graph-1", "--allow-local-verification")]
    [DataRow("--pause-graph", "graph-1")]
    [DataRow("--cancel-graph", "graph-1")]
    [DataRow("--recover-graph", "graph-1", "--task", "task-1")]
    [DataRow("--reconcile-graph-tool", "graph-1", "--task", "task-1", "--execution", "execution-1", "--result-file", "result.json", "--reconcile-note", "Verified tool effects")]
    [DataRow("--reconcile-graph-task", "graph-1", "--task", "task-1", "--result-file", "result.json", "--reconcile-note", "Verified workspace state")]
    [DataRow("--reconcile-graph-command", "graph-1", "--task", "task-1", "--invocation", "command-1", "--result-file", "result.json", "--reconcile-note", "Verified command outcome")]
    public void Parse_RecognizesGraphCommands(params string[] args)
    {
        var options = CliOptions.Parse(args);
        Assert.IsTrue(options.TryValidate(out var error), error);
    }

    [TestMethod]
    [DataRow("--list-graphs", "--prompt", "hi")]
    [DataRow("--list-graphs", "--list-workflows")]
    [DataRow("--list-graphs", "--show-graph", "graph")]
    [DataRow("--list-graphs", "--provider", "local")]
    [DataRow("--resume-graph", "graph", "--model", "different")]
    [DataRow("--start-graph", "graph.json", "--profile", "Coding")]
    [DataRow("--resume-graph", "graph", "--max-total-tokens", "100")]
    [DataRow("--resume-graph", "graph", "--max-execution-seconds", "10")]
    [DataRow("--resume-graph", "graph", "--llm-context-window-tokens", "1000")]
    [DataRow("--show-graph", " ")]
    [DataRow("--start-graph", " ")]
    [DataRow("--list-graphs", "--allow-local-verification")]
    [DataRow("--pause-graph", "graph", "--allow-local-verification")]
    [DataRow("--recover-graph", "graph")]
    [DataRow("--show-graph", "graph", "--task", "task")]
    [DataRow("--reconcile-graph-command", "graph", "--task", "task", "--result-file", "result.json", "--reconcile-note", "Verified")]
    [DataRow("--reconcile-graph-task", "graph", "--task", "task", "--invocation", "invocation", "--result-file", "result.json", "--reconcile-note", "Verified")]
    [DataRow("--reconcile-graph-task", "graph", "--task", "task", "--result-file", "result.json")]
    [DataRow("--resume-graph", "graph", "--reconcile-note", "note")]
    [DataRow("--reconcile-graph-tool", "graph", "--task", "task", "--result-file", "result.json", "--reconcile-note", "Verified")]
    [DataRow("--show-graph", "graph", "--execution", "execution")]
    [DataRow("--execution", "execution")]
    [DataRow("--task", "task")]
    [DataRow("--allow-local-verification", "--prompt", "hi")]
    [DataRow("--result-file", "result.json")]
    public void TryValidate_RejectsAmbiguousOrOverriddenGraphCommands(params string[] args)
    {
        Assert.IsFalse(CliOptions.Parse(args).TryValidate(out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
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

        Assert.AreEqual("openai", options.Provider);
        Assert.AreEqual("gpt-4o-mini", options.Model);
        Assert.AreEqual("s-1", options.Session);
        Assert.AreEqual("hello", options.Prompt);
        Assert.AreEqual(ToolProfile.Full, options.Profile);
        Assert.AreEqual(14, options.MessageTtlDays);
        Assert.IsTrue(options.AutoCreateSkills);
        Assert.IsTrue(options.ListSkills);
        Assert.IsTrue(options.NoEvents);
    }

    [TestMethod]
    public void Parse_UnknownOption_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CliOptions.Parse(["--unknown"]));
    }

    [TestMethod]
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

        Assert.IsFalse(valid);
        Assert.IsNotNull(error);
        Assert.Contains("either --prompt or --prompt-file", error, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [DataRow("json")]
    [DataRow("sqlite")]
    [DataRow("csharpdb")]
    public void Parse_RejectsObsoleteStoreFlag(string backend)
    {
        var error = Assert.ThrowsExactly<ArgumentException>(() => CliOptions.Parse(["--store", backend, "--prompt", "hi"]));
        Assert.Contains("CSharpDB", error.Message);
        Assert.Contains("not imported", error.Message);
        Assert.ThrowsExactly<ArgumentException>(() => CliOptions.Parse([$"--store={backend}"]));
    }

    [TestMethod]
    public void Parse_ParsesWorkflowCommands()
    {
        var list = CliOptions.Parse(["--list-workflows"]);
        Assert.IsTrue(list.ListWorkflows);
        Assert.IsTrue(list.TryValidate(out _));
        var resume = CliOptions.Parse(["--resume", "run-123", "--reconcile-tool", "execution-456",
            "--reconcile-result", "result.json", "--reconcile-note", "Verified destination state"]);
        Assert.AreEqual("run-123", resume.ResumeRunId);
        Assert.AreEqual("execution-456", resume.ReconcileToolExecutionId);
        Assert.AreEqual("result.json", resume.ReconcileResultFile);
        Assert.AreEqual("Verified destination state", resume.ReconcileNote);
        Assert.IsTrue(resume.TryValidate(out _));
    }

    [TestMethod]
    [DataRow("--resume", "run", "--list-workflows")]
    [DataRow("--resume", "run", "--prompt", "hello")]
    [DataRow("--list-workflows", "--prompt-file", "prompt.txt")]
    [DataRow("--list-workflows", "--list-skills")]
    [DataRow("--resume", " ")]
    [DataRow("--reconcile-tool", "execution")]
    [DataRow("--resume", "run", "--reconcile-tool", "execution")]
    public void TryValidate_RejectsAmbiguousWorkflowCommands(params string[] args)
    {
        var options = CliOptions.Parse(args);
        Assert.IsFalse(options.TryValidate(out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    [DataRow("--pause", "run")]
    [DataRow("--cancel", "run")]
    [DataRow("--resume", "run", "--answer-request", "question", "--answer", "Use CSharpDB")]
    [DataRow("--resume", "run", "--approve-request", "approval")]
    [DataRow("--resume", "run", "--deny-request", "approval")]
    public void Parse_RecognizesDurableHumanControls(params string[] args)
    {
        var options = CliOptions.Parse(args);
        Assert.IsTrue(options.TryValidate(out var error), error);
        Assert.IsTrue(WorkflowCommands.IsWorkflowCommand(options));
    }

    [TestMethod]
    [DataRow("--pause", "run", "--resume", "run")]
    [DataRow("--cancel", "run", "--pause", "run")]
    [DataRow("--pause", " ")]
    [DataRow("--cancel", "run", "--prompt", "hello")]
    [DataRow("--list-workflows", "--cancel", "run")]
    [DataRow("--answer-request", "request", "--answer", "text")]
    [DataRow("--resume", "run", "--answer", "text")]
    [DataRow("--resume", "run", "--answer-request", "request")]
    [DataRow("--resume", "run", "--answer-request", "request", "--answer", " ")]
    [DataRow("--approve-request", "request")]
    [DataRow("--resume", "run", "--deny-request", " ")]
    [DataRow("--resume", "run", "--approve-request", "request", "--deny-request", "request")]
    [DataRow("--resume", "run", "--answer-request", "request", "--answer", "text", "--approve-request", "approval")]
    [DataRow("--resume", "run", "--require-approval", "write_file")]
    [DataRow("--require-approval", "write_file,")]
    [DataRow("--require-approval", "write_file,WRITE_FILE")]
    public void TryValidate_RejectsIncompleteOrConflictingHumanControls(params string[] args)
    {
        var options = CliOptions.Parse(args);
        Assert.IsFalse(options.TryValidate(out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void Parse_ApprovalPolicyDefaultsToNoneAndTrimsToolNames()
    {
        Assert.IsNull(CliOptions.Parse(["--prompt", "hi"]).ApprovalRequiredTools);
        var options = CliOptions.Parse(["--prompt", "hi", "--require-approval", "write_file, execute_command"]);
        Assert.AreSequenceEqual(new[] { "write_file", "execute_command" }, options.ApprovalRequiredTools);
        Assert.IsTrue(options.TryValidate(out _));
    }

    [TestMethod]
    public void Parse_ParsesMessageTtlDays()
    {
        var options = CliOptions.Parse(["--message-ttl-days", "45", "--prompt", "hi"]);
        Assert.AreEqual(45, options.MessageTtlDays);
    }

    [TestMethod]
    public void Parse_InvalidMessageTtlDays_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CliOptions.Parse(["--message-ttl-days", "0", "--prompt", "hi"]));
    }

    [TestMethod]
    public void Parse_WebProfile_IsSupported()
    {
        var options = CliOptions.Parse(["--profile", "web", "--prompt", "hi"]);

        Assert.AreEqual(ToolProfile.Web, options.Profile);
    }

    [TestMethod]
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

        Assert.AreEqual(32768, options.LlmContextWindowTokens);
        Assert.AreEqual(1024, options.LlmSafetyMarginTokens);
        Assert.AreEqual(512, options.LlmMinOutputTokensPerRequest);
        Assert.AreEqual(3, options.LlmMaxAutoContinuationPasses);
    }

    [TestMethod]
    public void Parse_ResolvesPersistentWorkflowBudgetsWithoutChangingDefaultCallLimits()
    {
        var options = CliOptions.Parse(["--max-total-tokens", "3000000000", "--max-execution-seconds", "95", "--prompt", "hello"]);

        Assert.IsTrue(options.TryValidate(out _));
        Assert.AreEqual(3_000_000_000L, options.MaxTotalTokens);
        Assert.AreEqual(95, options.MaxExecutionSeconds);
        var budget = options.ResolveWorkflowBudget();
        Assert.IsNotNull(budget);
        Assert.AreEqual(3_000_000_000L, budget.MaxTotalTokens);
        Assert.AreEqual(TimeSpan.FromSeconds(95), budget.MaxExecutionTime);
        Assert.AreEqual(32, budget.MaxModelCalls);
        Assert.AreEqual(64, budget.MaxToolCalls);
        Assert.IsNull(CliOptions.Parse(["--prompt", "hello"]).ResolveWorkflowBudget());
    }

    [TestMethod]
    [DataRow("--max-total-tokens", "0")]
    [DataRow("--max-total-tokens", "-1")]
    [DataRow("--max-total-tokens", "9223372036854775808")]
    [DataRow("--max-total-tokens", "invalid")]
    [DataRow("--max-execution-seconds", "0")]
    [DataRow("--max-execution-seconds", "-1")]
    [DataRow("--max-execution-seconds", "2147483648")]
    [DataRow("--max-execution-seconds", "invalid")]
    public void Parse_RejectsInvalidPersistentBudget(string option, string value)
    {
        Assert.ThrowsExactly<ArgumentException>(() => CliOptions.Parse([option, value]));
    }

    [TestMethod]
    [DataRow("--resume", "workflow", "--max-total-tokens", "1000")]
    [DataRow("--pause", "workflow", "--max-execution-seconds", "10")]
    [DataRow("--cancel", "workflow", "--max-total-tokens", "1000")]
    [DataRow("--list-workflows", "--max-execution-seconds", "10")]
    public void TryValidate_RejectsBudgetChangesDuringRecovery(params string[] args)
    {
        Assert.IsFalse(CliOptions.Parse(args).TryValidate(out var error));
        Assert.IsNotNull(error);
        Assert.Contains("Saved limits cannot change", error);
    }

    [TestMethod]
    [DataRow(0, 10)]
    [DataRow(1, 0)]
    public void TryValidate_RejectsInvalidBudgetInDirectOptions(long tokens, int seconds)
    {
        var options = CliOptions.Parse(["--prompt", "hello"]) with { MaxTotalTokens = tokens, MaxExecutionSeconds = seconds };
        Assert.IsFalse(options.TryValidate(out var error));
        Assert.IsNotNull(error);
        Assert.Contains("positive integers", error);
    }

    [TestMethod]
    public void Parse_InvalidLlmContextWindow_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CliOptions.Parse(["--llm-context-window-tokens", "0", "--prompt", "hi"]));
    }

    [TestMethod]
    public void Parse_InvalidLlmAutoContinuationPasses_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CliOptions.Parse(["--llm-max-auto-continuation-passes", "-1", "--prompt", "hi"]));
    }

    [TestMethod]
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

        Assert.IsFalse(valid);
        Assert.IsNotNull(error);
        Assert.Contains("message-ttl-days", error, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
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

        Assert.IsFalse(valid);
        Assert.IsNotNull(error);
        Assert.Contains("llm-context-window-tokens", error, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
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
        Assert.AreEqual("hello world", prompt);
    }
}
