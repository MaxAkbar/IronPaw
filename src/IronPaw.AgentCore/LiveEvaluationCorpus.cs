using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed record LiveEvaluationCaseBinding(string CaseId, string SourceDirectory, string BaseCommit, string InputDigest, string SdkVersion);
public sealed record LiveEvaluationScheduledTrial(string TrialId, string CaseId, string Strategy, string GraphId);
public sealed record LiveEvaluationBinding(LiveEvaluationSettings Settings, string ProtocolVersion, string OutputDirectory,
    string DotnetExecutable, string DotnetExecutableSha256, ImmutableArray<LiveEvaluationCaseBinding> Cases,
    ImmutableArray<LiveEvaluationScheduledTrial> Schedule);

/// <summary>A small, versioned coding corpus. Host assertions are outside every model read/write scope.</summary>
internal static class LiveEvaluationCorpus
{
    public const string Protocol = "ironpaw-paired-code-doc/v1";
    public static readonly WorkflowBudget Budget = new(24, 24, 200_000, TimeSpan.FromMinutes(5));
    private const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>";
    public static string Method(string caseId) => caseId switch { "addition" => "Add", "subtraction" => "Subtract", "multiplication" => "Multiply", _ => throw new ArgumentException("Unknown corpus case.") };
    private static string Operator(string caseId) => caseId switch { "addition" => "+", "subtraction" => "-", "multiplication" => "*", _ => throw new ArgumentException("Unknown corpus case.") };
    public static string Documentation(string caseId) => $"Calc.{Method(caseId)} uses checked integer {caseId}.";
    internal static string CorrectCode(string caseId) => $"public static class Calc {{ public static int {Method(caseId)}(int left, int right) => checked(left {Operator(caseId)} right); }}";

    private static Dictionary<string, string> Files(string caseId, string sdkVersion)
    {
        var method = Method(caseId);
        var negative = caseId switch { "addition" => -5, "subtraction" => -9, _ => -14 };
        var positive = caseId switch { "addition" => 42, "subtraction" => -8, _ => 425 };
        var overflow = caseId == "subtraction" ? $"Calc.{method}(int.MinValue, 1)" : $"Calc.{method}(int.MaxValue, {(caseId == "addition" ? 1 : 2)})";
        var program = $$"""
            using System.Xml.Linq;
            var checks = new (string Name, Func<bool> Test)[] {
                ("zero", () => Calc.{{method}}(0, 0) == 0),
                ("negative", () => Calc.{{method}}(-7, 2) == {{negative}}),
                ("positive", () => Calc.{{method}}(17, 25) == {{positive}}),
                ("overflow", () => { try { {{overflow}}; return false; } catch (OverflowException) { return true; } }),
                ("docs", () => File.ReadAllText("README.md").Trim() == "{{Documentation(caseId)}}") };
            var outcomes = checks.Select(check => {
                try { return (check.Name, Passed: check.Test()); } catch { return (check.Name, Passed: false); }
            }).ToArray();
            var passed = outcomes.Count(check => check.Passed);
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            new XDocument(new XElement(ns + "TestRun",
                new XElement(ns + "Results", outcomes.Select(check => new XElement(ns + "UnitTestResult", new XAttribute("testName", check.Name), new XAttribute("outcome", check.Passed ? "Passed" : "Failed")))),
                new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"),
                    new XElement(ns + "Counters", new XAttribute("total", outcomes.Length), new XAttribute("executed", outcomes.Length), new XAttribute("passed", passed), new XAttribute("failed", outcomes.Length - passed), new XAttribute("notExecuted", 0)))))
                .Save(args[0]);
            Console.WriteLine($"CORPUS_ASSERTIONS {passed}/{outcomes.Length}");
            return passed == outcomes.Length ? 0 : 7;
            """;
        return new(StringComparer.Ordinal) { ["Fixture.csproj"] = Project, ["Program.cs"] = program,
            ["Calc.cs"] = $"public static class Calc {{ public static int {method}(int left, int right) => 0; }}",
            ["README.md"] = "TODO", ["NuGet.Config"] = "<configuration><packageSources><clear /></packageSources></configuration>",
            ["global.json"] = TaskGraphDocumentJson.Serialize(new { sdk = new { version = sdkVersion, rollForward = "disable", allowPrerelease = true } }) };
    }

    public static string InputDigest(string caseId, string sdkVersion) => LiveEvaluationIdentity.Digest(TaskGraphDocumentJson.Serialize(Files(caseId, sdkVersion)));
    public static async Task<ImmutableArray<LiveEvaluationCaseBinding>> CreateAsync(string output, string dotnet, CancellationToken ct)
    {
        var sdkVersion = await SdkVersionAsync(dotnet, output, ct);
        var cases = ImmutableArray.CreateBuilder<LiveEvaluationCaseBinding>();
        foreach (var caseId in new[] { "addition", "subtraction", "multiplication" })
        {
            var source = Path.Combine(output, "source", caseId);
            if (Directory.Exists(source)) throw new IOException("A corpus source directory already exists; use resume for an accepted campaign.");
            Directory.CreateDirectory(source);
            foreach (var (name, content) in Files(caseId, sdkVersion)) await File.WriteAllTextAsync(Path.Combine(source, name), content, new UTF8Encoding(false), ct);
            await GitAsync(source, ["-c", "init.templateDir=", "init"], ct);
            await GitAsync(source, ["-c", "core.autocrlf=false", "add", "--", "."], ct);
            await GitAsync(source, ["-c", "core.hooksPath=", "-c", "commit.gpgSign=false", "-c", "user.name=IronPaw Evaluation", "-c", "user.email=evaluations@example.invalid", "commit", "-m", Protocol], ct);
            var commit = (await GitAsync(source, ["rev-parse", "HEAD"], ct)).Trim();
            cases.Add(new(caseId, source, commit, InputDigest(caseId, sdkVersion), sdkVersion));
        }
        return cases.ToImmutable();
    }

    public static async Task ValidateAsync(LiveEvaluationCaseBinding item, CancellationToken ct)
    {
        if (item.InputDigest != InputDigest(item.CaseId, item.SdkVersion) ||
            (await GitAsync(item.SourceDirectory, ["rev-parse", "HEAD"], ct)).Trim() != item.BaseCommit)
            throw new WorkflowConflictException("The frozen evaluation corpus or commit changed.");
        foreach (var (name, content) in Files(item.CaseId, item.SdkVersion))
            if (await File.ReadAllTextAsync(Path.Combine(item.SourceDirectory, name), ct) != content)
                throw new WorkflowConflictException("Evaluation source files changed after campaign admission.");
    }

    private static async Task<string> SdkVersionAsync(string dotnet, string output, CancellationToken ct)
    {
        var info = new ProcessStartInfo(dotnet) { WorkingDirectory = output, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--version");
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        using var process = Process.Start(info) ?? throw new IOException("The .NET SDK could not be inspected.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var cancelled = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        await process.WaitForExitAsync(ct);
        var version = (await stdout).Trim();
        var error = await stderr;
        if (process.ExitCode != 0 || version.Length > 128 || !System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$"))
            throw new IOException("An installed SDK version could not be pinned: " + error);
        return version;
    }

    public static ImmutableArray<LiveEvaluationScheduledTrial> Schedule(string campaignId, LiveEvaluationSettings settings)
    {
        var result = ImmutableArray.CreateBuilder<LiveEvaluationScheduledTrial>();
        string[] cases = ["addition", "subtraction", "multiplication"];
        for (var pair = 0; pair < settings.PairedTrials; pair++)
        {
            var trialId = $"pair-{pair + 1:D4}";
            var order = SHA256.HashData(Encoding.UTF8.GetBytes($"{settings.Seed}/{pair}"))[0] % 2 == 0
                ? new[] { "graph", "direct" } : new[] { "direct", "graph" };
            foreach (var strategy in order)
                result.Add(new(trialId, cases[pair % cases.Length], strategy,
                    "evaluation-" + LiveEvaluationIdentity.Digest($"{campaignId}/{trialId}/{strategy}")));
        }
        return result.ToImmutable();
    }

    public static WorkflowTaskGraph Graph(LiveEvaluationScheduledTrial trial)
    {
        var objective = $"Implement Calc.{Method(trial.CaseId)}(int left, int right) using checked integer {trial.CaseId}. " +
            $"Update README.md to exactly this contract (a final newline is allowed): {Documentation(trial.CaseId)}";
        WorkflowTaskDefinition Task(string role, string instruction, ImmutableArray<string> writes,
            ImmutableArray<TaskArtifactRequirement> artifacts) => new(role, $"Role: {role}\n{objective}\n{instruction}") {
                ToolPolicy = new(ToolProfile.Coding), Resources = new() { ReadPaths = ["Calc.cs", "README.md"], WritePaths = writes },
                ExpectedArtifacts = artifacts, AcceptanceCriteria = [new("integrity", "The exact sealed output contains the required files") { ArtifactIds = artifacts.Select(a => a.ArtifactId).ToImmutableArray() }]
            };
        ImmutableArray<TaskArtifactRequirement> outputs = [new("code", "Checked integer implementation", TaskArtifactKind.File, "Calc.cs"), new("docs", "Documented contract", TaskArtifactKind.File, "README.md")];
        var final = Task(trial.Strategy == "graph" ? "join" : "direct", trial.Strategy == "graph"
            ? "Review the combined accepted implementation and documentation. Report any unresolved issue."
            : "Make both changes in this task, then report completion.", trial.Strategy == "graph" ? [] : ["Calc.cs", "README.md"], outputs) with {
                Dependencies = trial.Strategy == "graph" ? ["code", "docs"] : [],
                AcceptanceCriteria = [new("integrity", "Both exact outputs are present") { ArtifactIds = ["code", "docs"] }, new("build", "The candidate compiles"), new("tests", "All host assertions pass")]
            };
        return new(trial.GraphId, objective, trial.Strategy == "graph"
            ? [Task("code", "Change only the implementation.", ["Calc.cs"], [outputs[0]]), Task("docs", "Change only the documentation.", ["README.md"], [outputs[1]]), final]
            : [final]);
    }

    private static async Task<string> GitAsync(string directory, string[] arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-C"); info.ArgumentList.Add(directory);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["GIT_NO_LAZY_FETCH"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(info) ?? throw new IOException("Git could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        var text = await stdout;
        if (process.ExitCode != 0) throw new IOException("Evaluation corpus Git operation failed: " + await stderr);
        await stderr;
        return text;
    }
}
