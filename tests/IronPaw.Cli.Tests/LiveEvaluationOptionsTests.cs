using IronPaw.Cli;

namespace IronPaw.Cli.Tests;

[TestClass]
public sealed class LiveEvaluationOptionsTests
{
    private static readonly string Output = Path.Combine(Path.GetTempPath(), "ironpaw-evaluation-options");

    [TestMethod]
    [DataRow("--run-evaluation")]
    [DataRow("--resume-evaluation")]
    public void ExecutionRequiresExplicitHostProcessPermission(string command)
    {
        var options = CliOptions.Parse([command, "campaign", "--evaluation-output", Output]);
        Assert.IsFalse(options.TryValidate(out var error));
        Assert.IsNotNull(error);
        Assert.Contains("--allow-local-verification", error);
        Assert.IsTrue((options with { AllowLocalVerification = true }).TryValidate(out _));
    }

    [TestMethod]
    [DataRow("--show-evaluation")]
    [DataRow("--reconcile-evaluation")]
    public void NonExecutingCommandsRejectProcessPermission(string command)
    {
        var arguments = new List<string> { command, "campaign", "--evaluation-output", Output };
        if (command == "--reconcile-evaluation") arguments.AddRange(["--request", "request", "--result-file", "receipt.json", "--reconcile-note", "verified invoice"]);
        var options = CliOptions.Parse(arguments.ToArray());
        Assert.IsTrue(options.TryValidate(out _));
        Assert.IsFalse((options with { AllowLocalVerification = true }).TryValidate(out _));
    }

    [TestMethod]
    [DataRow("--provider", "local")]
    [DataRow("--model", "other")]
    [DataRow("--session", "default")]
    [DataRow("--profile", "Coding")]
    [DataRow("--message-ttl-days", "30")]
    [DataRow("--llm-context-window-tokens", "2048")]
    [DataRow("--prompt", "hello")]
    [DataRow("--max-total-tokens", "100")]
    [DataRow("--resume", "workflow")]
    [DataRow("--show-graph", "graph")]
    [DataRow("--validate-evaluation-config", "config.json")]
    public void EvaluationRejectsUnrelatedOverridesEvenWhenEqualToDefaults(string flag, string value)
    {
        var options = CliOptions.Parse(["--run-evaluation", "config.json", "--evaluation-output", Output,
            "--allow-local-verification", flag, value]);
        Assert.IsFalse(options.TryValidate(out var error));
        Assert.IsNotNull(error);
        Assert.Contains("exactly one evaluation command", error);
    }

    [TestMethod]
    public void EvaluationRequiresAbsoluteOutputAndExactlyOneOperation()
    {
        Assert.IsFalse(CliOptions.Parse(["--show-evaluation", "campaign"]).TryValidate(out _));
        Assert.IsFalse(CliOptions.Parse(["--show-evaluation", "campaign", "--evaluation-output", "relative"]).TryValidate(out _));
        Assert.IsFalse(CliOptions.Parse(["--show-evaluation", "campaign", "--resume-evaluation", "campaign",
            "--evaluation-output", Output, "--allow-local-verification"]).TryValidate(out _));
        Assert.IsFalse(CliOptions.Parse(["--evaluation-output", Output]).TryValidate(out _));
    }

    [TestMethod]
    [DataRow("--request", "request")]
    [DataRow("--result-file", "receipt.json")]
    [DataRow("--reconcile-note", "invoice")]
    public void ReconciliationDetailsCannotAppearInShowOrExecution(string flag, string value)
    {
        Assert.IsFalse(CliOptions.Parse(["--show-evaluation", "campaign", "--evaluation-output", Output, flag, value]).TryValidate(out _));
    }

    [TestMethod]
    public void ReconciliationRequiresAllDetails()
    {
        var options = CliOptions.Parse(["--reconcile-evaluation", "campaign", "--evaluation-output", Output,
            "--request", "request", "--result-file", "receipt.json", "--reconcile-note", "verified"]);
        Assert.AreEqual("campaign", options.ReconcileEvaluationId);
        Assert.AreEqual("request", options.EvaluationRequestId);
        Assert.IsTrue(options.TryValidate(out _));
        Assert.IsFalse((options with { EvaluationRequestId = null }).TryValidate(out _));
        Assert.IsFalse((options with { GraphResultFile = null }).TryValidate(out _));
        Assert.IsFalse((options with { ReconcileNote = " " }).TryValidate(out _));
    }

    [TestMethod]
    [DataRow("--run-evaluation")]
    [DataRow("--resume-evaluation")]
    [DataRow("--show-evaluation")]
    [DataRow("--reconcile-evaluation")]
    [DataRow("--evaluation-output")]
    [DataRow("--request")]
    public void EvaluationRejectsDuplicateOptions(string flag)
    {
        Assert.ThrowsExactly<ArgumentException>(() => CliOptions.Parse([flag, "one", flag, "two"]));
    }

    [TestMethod]
    [DataRow("--reconcile-evaluation-command")]
    [DataRow("--reconcile-evaluation-task")]
    [DataRow("--reconcile-evaluation-tool")]
    public void EvaluationGraphReconciliationRequiresExactExclusiveIdentifiers(string command)
    {
        var arguments = new List<string> { command, "campaign", "--evaluation-output", Output,
            "--evaluation-graph", "graph", "--task", "task", "--result-file", "receipt.json", "--reconcile-note", "verified" };
        if (command == "--reconcile-evaluation-command") arguments.AddRange(["--invocation", "invocation"]);
        if (command == "--reconcile-evaluation-tool") arguments.AddRange(["--tool-execution", "execution"]);
        var options = CliOptions.Parse(arguments.ToArray());
        Assert.IsTrue(options.TryValidate(out var error), error);
        Assert.IsFalse((options with { EvaluationGraphId = null }).TryValidate(out _));
        Assert.IsFalse((options with { GraphTaskId = null }).TryValidate(out _));
        Assert.IsFalse((options with { GraphResultFile = null }).TryValidate(out _));
        Assert.IsFalse((options with { ReconcileNote = null }).TryValidate(out _));
        Assert.IsFalse((options with { EvaluationRequestId = "money-request" }).TryValidate(out _));
        Assert.IsFalse((options with { AllowLocalVerification = true }).TryValidate(out _));
        Assert.IsFalse((options with { ShowEvaluationId = "campaign" }).TryValidate(out _));
        if (command == "--reconcile-evaluation-command")
            Assert.IsFalse((options with { VerificationInvocationId = null }).TryValidate(out _));
        else Assert.IsFalse((options with { VerificationInvocationId = "invocation" }).TryValidate(out _));
        if (command == "--reconcile-evaluation-tool")
            Assert.IsFalse((options with { GraphToolExecutionId = null }).TryValidate(out _));
        else Assert.IsFalse((options with { GraphToolExecutionId = "execution" }).TryValidate(out _));
    }

    [TestMethod]
    [DataRow("--show-evaluation")]
    [DataRow("--run-evaluation")]
    [DataRow("--resume-evaluation")]
    public void GraphReconciliationIdentifiersCannotChangeOtherEvaluationCommands(string command)
    {
        var arguments = new List<string> { command, "campaign", "--evaluation-output", Output };
        if (command != "--show-evaluation") arguments.Add("--allow-local-verification");
        var options = CliOptions.Parse(arguments.ToArray());
        Assert.IsFalse((options with { EvaluationGraphId = "graph" }).TryValidate(out _));
        Assert.IsFalse((options with { GraphTaskId = "task" }).TryValidate(out _));
        Assert.IsFalse((options with { VerificationInvocationId = "invocation" }).TryValidate(out _));
        Assert.IsFalse((options with { GraphToolExecutionId = "execution" }).TryValidate(out _));
    }
}
