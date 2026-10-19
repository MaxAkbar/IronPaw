using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

/// <summary>Versioned controlled replay, not a measurement of live model quality or routing superiority.</summary>
[TestClass]
public sealed class TaskGraphWorkflowEvaluationTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string Protocol = "ironpaw-controlled-replay/v1";
    private const string CorrectCode = "public static class Calc { public static int Add(int left, int right) => checked(left + right); }";
    private const string CorrectDocs = "Calc.Add uses checked integer addition.";
    private static readonly CallerIdentity Owner = new("evaluation-owner", "Controlled evaluation owner", true);
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly WorkflowBudget SharedBudget = new(24, 12, 100_000, TimeSpan.FromMinutes(5));

    [TestMethod]
    public async Task ParallelCodeAndDocs_AndDirectBaseline_PassTheSameRealChecks_WithoutClaimingModelAdvantage()
    {
        await using var scope = await Scope.OpenAsync();
        var parallelClient = new ReplayClient("parallel");
        var parallel = await EvaluateAsync(scope, "parallel-code-doc", Graph("parallel", parallel: true), parallelClient);
        var direct = await EvaluateAsync(scope, "direct-code-doc", Graph("direct"), new ReplayClient("direct"));

        Assert.IsTrue(parallel.VerifiedCompletion);
        Assert.IsTrue(direct.VerifiedCompletion);
        Assert.IsTrue(parallelClient.IndependentBranchesAdmitted);
        Assert.AreEqual(parallel.BaseCommit, direct.BaseCommit);
        Assert.AreSequenceEqual(parallel.VerificationProfiles, direct.VerificationProfiles);
        Assert.AreSequenceEqual(parallel.OutputDigests, direct.OutputDigests);
        Assert.AreEqual(parallel.Budget, direct.Budget);
        Assert.AreEqual(5, parallel.Usage.ModelCalls);
        Assert.AreEqual(2, direct.Usage.ModelCalls);
        Assert.AreEqual(2, parallel.Usage.ToolCalls);
        Assert.AreEqual(2, direct.Usage.ToolCalls);
        // Replay deliberately exposes coordination overhead. Synthetic usage cannot establish real cost advantage.
        Assert.IsTrue(parallel.ChargedTokens > direct.ChargedTokens);
        Assert.AreEqual("NOT_ESTABLISHED", parallel.LiveBaselineQualification);
    }

    [TestMethod]
    [DataRow("dependency-conflict")]
    [DataRow("missing-output")]
    [DataRow("false-output")]
    [DataRow("provider-failure")]
    [DataRow("command-failure")]
    public async Task FaultCorpus_NeverAcceptsAnInvalidOverallResult(string scenario)
    {
        await using var scope = await Scope.OpenAsync();
        var graph = scenario == "dependency-conflict" ? ConflictGraph() : Graph(scenario);
        var client = new ReplayClient(scenario);
        var result = await EvaluateAsync(scope, scenario, graph, client, expectedCompletion: false);

        Assert.IsFalse(result.VerifiedCompletion);
        Assert.IsFalse(result.FalseGraphAcceptance);
        Assert.AreEqual(scenario == "provider-failure" ? TaskGraphRunStatus.NeedsReconciliation : TaskGraphRunStatus.Failed, result.Status);
        if (scenario == "dependency-conflict")
        {
            Assert.DoesNotContain("join", client.AdmittedRoles);
            Assert.AreEqual(0, result.CommandInvocations);
        }
        if (scenario is "missing-output" or "provider-failure") Assert.AreEqual(0, result.CommandInvocations);
        if (scenario == "false-output") Assert.AreEqual(2, result.CommandInvocations);
        if (scenario == "command-failure") Assert.AreEqual(1, result.CommandInvocations);
    }

    [TestMethod]
    public async Task PauseAndReopen_ReuseCompletedCommandAndProviderReceipts()
    {
        await using var scope = await Scope.OpenAsync();
        var graph = Graph("pause-recovery");
        var options = scope.Options(graph);
        var client = new ReplayClient("direct");
        var inner = new TrustedLocalTaskVerificationWorker(options.VerificationProfiles, TaskVerificationAuthority.TrustedLocalProcess);
        var worker = new PausingWorker(inner);
        var runner = scope.Runner(client, worker);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var watch = Stopwatch.StartNew();
        var active = runner.StartAsync(graph, Owner, 1, options, timeout.Token);
        try
        {
            await worker.Completed.Task.WaitAsync(TimeSpan.FromMinutes(2), timeout.Token);
            await runner.PauseAsync(graph.GraphId, Owner, Ct);
            worker.Release.TrySetResult();
            var paused = await active.WaitAsync(TimeSpan.FromSeconds(15), timeout.Token);
            Assert.AreEqual(TaskGraphRunStatus.Paused, TaskGraphProgress.Inspect(paused).Status);
            foreach (var invocation in paused.Tasks.Single().Verifications)
            {
                Assert.AreEqual(TaskVerificationInvocationState.Completed, invocation.State);
            }
            var usageBefore = await ((IWorkflowStore)scope.Store).GetUsageAsync(AgentTaskIdentity.RootRunId(graph.GraphId), Ct);
            await scope.ReopenAsync();

            var noReplayClient = new ReplayClient("provider-failure");
            var noReplayWorker = new RejectReplayWorker();
            var resumed = await scope.Runner(noReplayClient, noReplayWorker).ResumeAsync(graph.GraphId, Owner, timeout.Token);
            watch.Stop();
            var report = await ReportAsync(scope, "pause-recovery", resumed, watch.Elapsed, expectedCompletion: true, recoveryReusedReceipts: true);
            Assert.IsTrue(report.VerifiedCompletion);
            Assert.AreEqual(usageBefore, report.Usage);
            Assert.AreEqual(0, noReplayClient.Calls);
            Assert.AreEqual(0, noReplayWorker.Calls);
            Assert.AreEqual(2, worker.Calls);
        }
        finally
        {
            worker.Release.TrySetResult();
            await timeout.CancelAsync();
            await active;
        }
    }

    private async Task<EvaluationReport> EvaluateAsync(Scope scope, string scenario, WorkflowTaskGraph graph,
        ReplayClient client, bool expectedCompletion = true)
    {
        var options = scope.Options(graph);
        var worker = new TrustedLocalTaskVerificationWorker(options.VerificationProfiles, TaskVerificationAuthority.TrustedLocalProcess);
        var watch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var checkpoint = await scope.Runner(client, worker).StartAsync(graph, Owner, graph.Tasks.Length == 1 ? 1 : 2, options, timeout.Token);
        watch.Stop();
        var report = await ReportAsync(scope, scenario, checkpoint, watch.Elapsed, expectedCompletion);
        Assert.AreEqual(scope.InitialCode, await File.ReadAllTextAsync(Path.Combine(scope.Source, "Calc.cs")));
        Assert.AreEqual("TODO", await File.ReadAllTextAsync(Path.Combine(scope.Source, "README.md")));
        Assert.IsFalse(Directory.Exists(Path.Combine(scope.Source, "bin")));
        Assert.IsFalse(Directory.Exists(Path.Combine(scope.Source, "obj")));
        Assert.IsFalse(File.Exists(Path.Combine(scope.Source, "results.trx")));
        return report;
    }

    private async Task<EvaluationReport> ReportAsync(Scope scope, string scenario, TaskGraphCheckpoint checkpoint,
        TimeSpan elapsed, bool expectedCompletion, bool recoveryReusedReceipts = false)
    {
        var final = checkpoint.Tasks.Single(state => state.TaskId is "direct" or "join");
        var status = TaskGraphProgress.Inspect(checkpoint).Status;
        var succeeded = status == TaskGraphRunStatus.Succeeded;
        var usage = await ((IWorkflowStore)scope.Store).GetUsageAsync(AgentTaskIdentity.RootRunId(checkpoint.Graph.GraphId), Ct);
        var budget = await scope.Store.GetBudgetAsync(AgentTaskIdentity.RootRunId(checkpoint.Graph.GraphId), Ct);
        var invocations = checkpoint.Tasks.SelectMany(state => state.Verifications).ToArray();
        var verified = succeeded && final.Verifications.Length == 2 && final.Verifications.All(invocation =>
            invocation.State == TaskVerificationInvocationState.Completed && invocation.Completion?.Outcome == TaskCommandOutcome.Passed)
            && final.Verifications.Single(invocation => invocation.ProfileId == "tests").Completion!.Tests == new TaskVerificationTestSummary(5, 5, 0, 0);
        var report = new EvaluationReport(Protocol, scenario, "controlled-replay", "NOT_ESTABLISHED",
            checkpoint.AgentBinding!.BaseCommit, Scope.InputDigest, SharedBudget, status, expectedCompletion, verified,
            succeeded && !expectedCompletion, usage, budget.ChargedTokens, elapsed.TotalMilliseconds,
            budget.ExecutionTime.TotalMilliseconds, invocations.Length,
            checkpoint.AgentBinding.VerificationProfiles.Select(TaskVerificationIdentity.ProfileFingerprint).ToArray(),
            (final.Result?.Artifacts ?? []).OrderBy(artifact => artifact.ArtifactId, StringComparer.Ordinal).Select(artifact => artifact.ContentDigest).ToArray(),
            recoveryReusedReceipts,
            checkpoint.Tasks.Select(state => new TaskObservation(state.TaskId, state.Status, state.Acceptance?.Outcome,
                state.Verifications.Select(invocation => new CommandObservation(invocation.InvocationId, invocation.ProfileId,
                    invocation.ResultDigest, invocation.Revision, invocation.State, invocation.Completion?.Outcome,
                    invocation.Completion?.ExitCode, invocation.Completion?.Tests)).ToArray())).ToArray());
        var path = Path.Combine(scope.ReportDirectory, scenario + ".json");
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
        Assert.IsTrue(Encoding.UTF8.GetByteCount(json) < 32_768, "Evaluation reports must remain bounded.");
        await File.WriteAllTextAsync(path, json, Ct);
        TestContext.WriteLine("Controlled replay evidence: " + path);
        return report;
    }

    private static WorkflowTaskGraph Graph(string scenario, bool parallel = false)
    {
        var final = MakeTask(parallel ? "join" : "direct", scenario, parallel ? [] : ["Calc.cs", "README.md"],
            [new("code", "Checked addition implementation", TaskArtifactKind.File, "Calc.cs"),
             new("docs", "Document the checked addition contract", TaskArtifactKind.File, "README.md")]) with
        {
            Dependencies = parallel ? ["code", "docs"] : [],
            AcceptanceCriteria = [new("integrity", "Both outputs belong to the exact sealed result") { ArtifactIds = ["code", "docs"] },
                new("build", "The combined candidate compiles"), new("tests", "Zero, negative, positive, overflow and documentation assertions pass")]
        };
        if (scenario == "missing-output")
            final = final with { ExpectedArtifacts = final.ExpectedArtifacts.Add(new("missing", "Required change note", TaskArtifactKind.File, "change-note.txt")) };
        return new("eval-" + Guid.NewGuid().ToString("n"), "Implement checked addition and its documented contract",
            parallel ? [MakeTask("code", scenario, ["Calc.cs"], [new("code", "Code", TaskArtifactKind.File, "Calc.cs")]),
                MakeTask("docs", scenario, ["README.md"], [new("docs", "Docs", TaskArtifactKind.File, "README.md")]), final] : [final]);
    }

    private static WorkflowTaskGraph ConflictGraph()
    {
        var template = Graph("dependency-conflict", parallel: true);
        return template with
        {
            Tasks = [MakeTask("left", "dependency-conflict", ["Calc.cs"], [new("code", "Left code", TaskArtifactKind.File, "Calc.cs")]),
            MakeTask("right", "dependency-conflict", ["Calc.cs"], [new("code", "Right code", TaskArtifactKind.File, "Calc.cs")]),
            template.Tasks[2] with { Dependencies = ["left", "right"] }]
        };
    }

    private static WorkflowTaskDefinition MakeTask(string role, string scenario, ImmutableArray<string> writes,
        ImmutableArray<TaskArtifactRequirement> artifacts) => new(role, $"eval:{scenario}:{role}")
        {
            ToolPolicy = new(ToolProfile.Coding),
            Resources = new() { ReadPaths = ["."], WritePaths = writes },
            ExpectedArtifacts = artifacts,
            AcceptanceCriteria = [new("integrity", "The output is present in the sealed candidate") { ArtifactIds = artifacts.Select(artifact => artifact.ArtifactId).ToImmutableArray() }]
        };

    private sealed record CommandObservation(string InvocationId, string ProfileId, string ResultDigest, string Revision,
        TaskVerificationInvocationState State, TaskCommandOutcome? Outcome, int? ExitCode, TaskVerificationTestSummary? Tests);
    private sealed record TaskObservation(string TaskId, TaskGraphTaskStatus Status, TaskAcceptanceOutcome? Acceptance, CommandObservation[] Commands);
    private sealed record EvaluationReport(string Protocol, string Scenario, string EvidenceClass, string LiveBaselineQualification,
        string BaseCommit, string InputDigest, WorkflowBudget Budget, TaskGraphRunStatus Status, bool ExpectedCompletion,
        bool VerifiedCompletion, bool FalseGraphAcceptance, WorkflowUsage Usage, long ChargedTokens,
        double ElapsedWallMilliseconds, double ChargedExecutionMilliseconds, int CommandInvocations,
        string[] VerificationProfiles, string[] OutputDigests, bool RecoveryReusedReceipts, TaskObservation[] Tasks);

    private sealed class ReplayClient(string scenario) : ILlmClient
    {
        private readonly ConcurrentDictionary<SessionKey, int> turns = new();
        private readonly TaskCompletionSource branches = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int branchCount;
        public int Calls;
        public bool IndependentBranchesAdmitted => branches.Task.IsCompletedSuccessfully;
        public ConcurrentBag<string> AdmittedRoles { get; } = [];
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var role = request.Messages.First(message => message.Role == ChatRole.User).Content.Split('\n')[0].Split(':')[2];
            AdmittedRoles.Add(role);
            if (scenario == "provider-failure") throw new IOException("Controlled provider stream failure.");
            var turn = turns.AddOrUpdate(request.SessionKey, 1, (_, value) => value + 1);
            if (scenario == "parallel" && turn == 1 && role is "code" or "docs")
            {
                if (Interlocked.Increment(ref branchCount) == 2) branches.TrySetResult();
                await branches.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            }
            if (turn == 1 && scenario != "missing-output")
            {
                if (role is "code" or "direct" or "left" or "right")
                {
                    var code = scenario switch
                    {
                        "false-output" => "public static class Calc { public static int Add(int left, int right) => left - right; }",
                        "command-failure" => "this is not valid C#;",
                        "dependency-conflict" when role == "left" => CorrectCode,
                        "dependency-conflict" => CorrectCode + " // divergent branch",
                        _ => CorrectCode
                    };
                    yield return new ToolCallDelta("write-code", "write_file", JsonSerializer.SerializeToElement(new { path = "Calc.cs", content = code }));
                }
                if (role is "docs" or "direct")
                    yield return new ToolCallDelta("write-docs", "write_file", JsonSerializer.SerializeToElement(new { path = "README.md", content = CorrectDocs }));
                if (role == "join") yield return new AssistantTextDelta("The combined candidate is ready for host checks.");
            }
            else yield return new AssistantTextDelta("I completed and verified the requested change."); // Deliberately untrusted completion claim.
            yield return new UsageDelta(10, 4, 0, 0);
            yield return new CompletedDelta();
        }
    }

    private sealed class PausingWorker(ITaskVerificationWorker inner) : ITaskVerificationWorker
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public void ValidateProfile(TaskVerificationProfile profile, CallerIdentity caller) => inner.ValidateProfile(profile, caller);
        public async Task<TaskVerificationCompletion> ExecuteAsync(TaskVerificationJob job, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var receipt = await inner.ExecuteAsync(job, ct);
            if (job.Profile.ProfileId == "tests")
            {
                Completed.TrySetResult();
                await Release.Task; // A known completion may race durable pause; preserve it before settling.
            }
            return receipt;
        }
    }

    private sealed class RejectReplayWorker : ITaskVerificationWorker
    {
        public int Calls;
        public void ValidateProfile(TaskVerificationProfile profile, CallerIdentity caller) { }
        public Task<TaskVerificationCompletion> ExecuteAsync(TaskVerificationJob job, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("A completed verification process was replayed.");
        }
    }

    private sealed class Scope : IAsyncDisposable
    {
        private const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>";
        private const string InitialSource = "public static class Calc { public static int Add(int left, int right) => left - right; }";
        private const string NugetConfig = "<configuration><packageSources><clear /></packageSources></configuration>";
        private const string Program = """
            using System.Xml.Linq;
            var checks = new (string Name, Func<bool> Test)[] {
                ("zero", () => Calc.Add(0, 0) == 0),
                ("negative", () => Calc.Add(-7, 2) == -5),
                ("positive", () => Calc.Add(17, 25) == 42),
                ("overflow", () => { try { Calc.Add(int.MaxValue, 1); return false; } catch (OverflowException) { return true; } }),
                ("docs", () => File.ReadAllText("README.md") == "Calc.Add uses checked integer addition.") };
            var outcomes = checks.Select(check => (check.Name, Passed: check.Test())).ToArray();
            var passed = outcomes.Count(check => check.Passed);
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            new XDocument(new XElement(ns + "TestRun",
                new XElement(ns + "Results", outcomes.Select(check => new XElement(ns + "UnitTestResult", new XAttribute("testName", check.Name), new XAttribute("outcome", check.Passed ? "Passed" : "Failed")))),
                new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"),
                    new XElement(ns + "Counters", new XAttribute("total", outcomes.Length), new XAttribute("executed", outcomes.Length), new XAttribute("passed", passed), new XAttribute("failed", outcomes.Length - passed), new XAttribute("notExecuted", 0)))))
                .Save(args[0]);
            Console.WriteLine($"CONTROLLED_ASSERTIONS {passed}/{outcomes.Length}");
            return passed == outcomes.Length ? 0 : 7;
            """;
        private static readonly string Base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IronPawWorkflowEvaluations"));
        public string Root { get; } = Path.Combine(Base, "workspaces", Guid.NewGuid().ToString("n"));
        public string ReportDirectory { get; } = Path.Combine(Base, "reports", Guid.NewGuid().ToString("n"));
        public string Source => Path.Combine(Root, "source");
        public string InitialCode => InitialSource;
        public static string InputDigest => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                Protocol,
                Project,
                Program,
                Calc = InitialSource,
                Readme = "TODO",
                NugetConfig,
                Objective = "Implement checked addition and its documented contract",
                Code = CorrectCode,
                Docs = CorrectDocs
            })));
        private string Database => Path.Combine(Root, "workflows.cdb");
        public CSharpDbWorkflowStore Store { get; private set; } = null!;
        private readonly GitTaskWorkspaceManager manager = new();
        private readonly string dotnet = FindDotNet();
        public static async Task<Scope> OpenAsync()
        {
            var scope = new Scope();
            Directory.CreateDirectory(scope.Source);
            Directory.CreateDirectory(scope.ReportDirectory);
            await GitAsync(scope.Source, "init");
            await GitAsync(scope.Source, "config", "user.name", "IronPaw Controlled Evaluations");
            await GitAsync(scope.Source, "config", "user.email", "evaluations@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(scope.Source, "Fixture.csproj"), Project, Ct);
            await File.WriteAllTextAsync(Path.Combine(scope.Source, "Program.cs"), Program, Ct);
            await File.WriteAllTextAsync(Path.Combine(scope.Source, "Calc.cs"), scope.InitialCode, Ct);
            await File.WriteAllTextAsync(Path.Combine(scope.Source, "README.md"), "TODO", Ct);
            await File.WriteAllTextAsync(Path.Combine(scope.Source, "NuGet.Config"), NugetConfig, Ct);
            await GitAsync(scope.Source, "add", ".");
            await GitAsync(scope.Source, "commit", "-m", "Controlled replay corpus v1");
            scope.Store = await CSharpDbWorkflowStore.OpenAsync(scope.Database, Ct);
            return scope;
        }
        public TaskGraphAgentOptions Options(WorkflowTaskGraph graph)
        {
            var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dotnet)));
            var final = graph.Tasks.Single(task => task.TaskId is "direct" or "join");
            return new(Source, Path.Combine(Root, "managed"), "HEAD", "controlled-replay", "replay-v1", new(ToolProfile.Coding), SharedBudget, MaxRounds: 3, MaxOutputTokens: 256)
            {
                DependencyMode = TaskDependencyMode.AcceptedChanges,
                ArtifactVerificationRules = graph.Tasks.Select(task => new TaskArtifactVerificationRule(task.TaskId, "integrity")).ToImmutableArray(),
                VerificationAuthority = TaskVerificationAuthority.TrustedLocalProcess,
                VerificationProfiles = [new("build", dotnet, digest, ["build", "Fixture.csproj", "--configuration", "Release", "--verbosity", "quiet"], TimeSpan.FromMinutes(2)) { MaxOutputBytes = 4096 },
                    new("tests", dotnet, digest, ["run", "--project", "Fixture.csproj", "--configuration", "Release", "--no-launch-profile", "--", "results.trx"], TimeSpan.FromMinutes(2))
                    { Kind = TaskVerificationKind.Tests, TestResultsPath = "results.trx", MaxOutputBytes = 4096 }],
                CommandVerificationRules = [new(final.TaskId, "build", "build"), new(final.TaskId, "tests", "tests")]
            };
        }
        public AgentTaskGraphRunner Runner(ILlmClient client, ITaskVerificationWorker worker) => new(Store, Store, manager, client, worker);
        public async Task ReopenAsync() { await Store.DisposeAsync(); Store = await CSharpDbWorkflowStore.OpenAsync(Database, Ct); }
        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.Combine(Base, "workspaces") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Evaluation cleanup escaped its dedicated workspace directory.");
            if (!Directory.Exists(resolved)) return;
            foreach (var path in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(resolved, recursive: true);
        }
        private static string FindDotNet()
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            throw new InvalidOperationException("Controlled evaluations require the .NET test SDK on PATH.");
        }
        private static async Task GitAsync(string source, params string[] arguments)
        {
            var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-C"); info.ArgumentList.Add(source);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stdout;
            Assert.IsTrue(process.ExitCode == 0, await stderr);
        }
    }
}
