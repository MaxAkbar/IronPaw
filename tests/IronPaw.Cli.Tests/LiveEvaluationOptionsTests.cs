using IronPaw.Cli;

namespace IronPaw.Cli.Tests;

public sealed class LiveEvaluationOptionsTests
{
    private static readonly string Output = Path.Combine(Path.GetTempPath(), "ironpaw-evaluation-options");

    [Theory]
    [InlineData("--run-evaluation")]
    [InlineData("--resume-evaluation")]
    public void ExecutionRequiresExplicitHostProcessPermission(string command)
    {
        var options = CliOptions.Parse([command, "campaign", "--evaluation-output", Output]);
        Assert.False(options.TryValidate(out var error));
        Assert.Contains("--allow-local-verification", error);
        Assert.True((options with { AllowLocalVerification = true }).TryValidate(out _));
    }

    [Theory]
    [InlineData("--show-evaluation")]
    [InlineData("--reconcile-evaluation")]
    public void NonExecutingCommandsRejectProcessPermission(string command)
    {
        var arguments = new List<string> { command, "campaign", "--evaluation-output", Output };
        if (command == "--reconcile-evaluation") arguments.AddRange(["--request", "request", "--result-file", "receipt.json", "--reconcile-note", "verified invoice"]);
        var options = CliOptions.Parse(arguments.ToArray());
        Assert.True(options.TryValidate(out _));
        Assert.False((options with { AllowLocalVerification = true }).TryValidate(out _));
    }

    [Theory]
    [InlineData("--provider", "local")]
    [InlineData("--model", "other")]
    [InlineData("--session", "default")]
    [InlineData("--profile", "Coding")]
    [InlineData("--message-ttl-days", "30")]
    [InlineData("--llm-context-window-tokens", "2048")]
    [InlineData("--prompt", "hello")]
    [InlineData("--max-total-tokens", "100")]
    [InlineData("--resume", "workflow")]
    [InlineData("--show-graph", "graph")]
    [InlineData("--validate-evaluation-config", "config.json")]
    public void EvaluationRejectsUnrelatedOverridesEvenWhenEqualToDefaults(string flag, string value)
    {
        var options = CliOptions.Parse(["--run-evaluation", "config.json", "--evaluation-output", Output,
            "--allow-local-verification", flag, value]);
        Assert.False(options.TryValidate(out var error));
        Assert.Contains("exactly one evaluation command", error);
    }

    [Fact]
    public void EvaluationRequiresAbsoluteOutputAndExactlyOneOperation()
    {
        Assert.False(CliOptions.Parse(["--show-evaluation", "campaign"]).TryValidate(out _));
        Assert.False(CliOptions.Parse(["--show-evaluation", "campaign", "--evaluation-output", "relative"]).TryValidate(out _));
        Assert.False(CliOptions.Parse(["--show-evaluation", "campaign", "--resume-evaluation", "campaign",
            "--evaluation-output", Output, "--allow-local-verification"]).TryValidate(out _));
        Assert.False(CliOptions.Parse(["--evaluation-output", Output]).TryValidate(out _));
    }

    [Theory]
    [InlineData("--request", "request")]
    [InlineData("--result-file", "receipt.json")]
    [InlineData("--reconcile-note", "invoice")]
    public void ReconciliationDetailsCannotAppearInShowOrExecution(string flag, string value)
    {
        Assert.False(CliOptions.Parse(["--show-evaluation", "campaign", "--evaluation-output", Output, flag, value]).TryValidate(out _));
    }

    [Fact]
    public void ReconciliationRequiresAllDetails()
    {
        var options = CliOptions.Parse(["--reconcile-evaluation", "campaign", "--evaluation-output", Output,
            "--request", "request", "--result-file", "receipt.json", "--reconcile-note", "verified"]);
        Assert.Equal("campaign", options.ReconcileEvaluationId);
        Assert.Equal("request", options.EvaluationRequestId);
        Assert.True(options.TryValidate(out _));
        Assert.False((options with { EvaluationRequestId = null }).TryValidate(out _));
        Assert.False((options with { GraphResultFile = null }).TryValidate(out _));
        Assert.False((options with { ReconcileNote = " " }).TryValidate(out _));
    }

    [Theory]
    [InlineData("--run-evaluation")]
    [InlineData("--resume-evaluation")]
    [InlineData("--show-evaluation")]
    [InlineData("--reconcile-evaluation")]
    [InlineData("--evaluation-output")]
    [InlineData("--request")]
    public void EvaluationRejectsDuplicateOptions(string flag)
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse([flag, "one", flag, "two"]));
    }

    [Theory]
    [InlineData("--reconcile-evaluation-command")]
    [InlineData("--reconcile-evaluation-task")]
    [InlineData("--reconcile-evaluation-tool")]
    public void EvaluationGraphReconciliationRequiresExactExclusiveIdentifiers(string command)
    {
        var arguments = new List<string> { command, "campaign", "--evaluation-output", Output,
            "--evaluation-graph", "graph", "--task", "task", "--result-file", "receipt.json", "--reconcile-note", "verified" };
        if (command == "--reconcile-evaluation-command") arguments.AddRange(["--invocation", "invocation"]);
        if (command == "--reconcile-evaluation-tool") arguments.AddRange(["--tool-execution", "execution"]);
        var options = CliOptions.Parse(arguments.ToArray());
        Assert.True(options.TryValidate(out var error), error);
        Assert.False((options with { EvaluationGraphId = null }).TryValidate(out _));
        Assert.False((options with { GraphTaskId = null }).TryValidate(out _));
        Assert.False((options with { GraphResultFile = null }).TryValidate(out _));
        Assert.False((options with { ReconcileNote = null }).TryValidate(out _));
        Assert.False((options with { EvaluationRequestId = "money-request" }).TryValidate(out _));
        Assert.False((options with { AllowLocalVerification = true }).TryValidate(out _));
        Assert.False((options with { ShowEvaluationId = "campaign" }).TryValidate(out _));
        if (command == "--reconcile-evaluation-command")
            Assert.False((options with { VerificationInvocationId = null }).TryValidate(out _));
        else Assert.False((options with { VerificationInvocationId = "invocation" }).TryValidate(out _));
        if (command == "--reconcile-evaluation-tool")
            Assert.False((options with { GraphToolExecutionId = null }).TryValidate(out _));
        else Assert.False((options with { GraphToolExecutionId = "execution" }).TryValidate(out _));
    }

    [Theory]
    [InlineData("--show-evaluation")]
    [InlineData("--run-evaluation")]
    [InlineData("--resume-evaluation")]
    public void GraphReconciliationIdentifiersCannotChangeOtherEvaluationCommands(string command)
    {
        var arguments = new List<string> { command, "campaign", "--evaluation-output", Output };
        if (command != "--show-evaluation") arguments.Add("--allow-local-verification");
        var options = CliOptions.Parse(arguments.ToArray());
        Assert.False((options with { EvaluationGraphId = "graph" }).TryValidate(out _));
        Assert.False((options with { GraphTaskId = "task" }).TryValidate(out _));
        Assert.False((options with { VerificationInvocationId = "invocation" }).TryValidate(out _));
        Assert.False((options with { GraphToolExecutionId = "execution" }).TryValidate(out _));
    }
}
