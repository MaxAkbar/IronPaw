using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using IronPaw.AgentCore;
using IronPaw.Cli;

namespace IronPaw.Cli.Tests;

public sealed class EvaluationCorpusIsolationTests
{
    [Fact]
    public async Task InheritedGitOverridesCannotRedirectCorpusWritesOrSnapshotReadsIntoAnotherRepository()
    {
        using var scope = new Scope();
        var sentinel = Path.Combine(scope.Root, "sentinel");
        var workspace = Path.Combine(scope.Root, "workspace");
        var campaign = Path.Combine(scope.Root, "campaign");
        Directory.CreateDirectory(sentinel); Directory.CreateDirectory(workspace);
        await Git(sentinel, ["-c", "init.templateDir=", "init"]);
        await File.WriteAllTextAsync(Path.Combine(sentinel, "protected.txt"), "preserve this repository");
        await Git(sentinel, ["add", "--", "protected.txt"]);
        await Git(sentinel, ["-c", "core.hooksPath=", "-c", "commit.gpgSign=false", "-c", "user.name=Isolation Test",
            "-c", "user.email=isolation@example.invalid", "commit", "-m", "protected baseline"]);
        // An already staged user edit makes index preservation meaningful as well as HEAD preservation.
        await File.WriteAllTextAsync(Path.Combine(sentinel, "pending.txt"), "uncommitted staged content");
        await Git(sentinel, ["add", "--", "pending.txt"]);
        var originalHead = (await Git(sentinel, ["rev-parse", "HEAD"])).Trim();
        var originalIndex = FileDigest(Path.Combine(sentinel, ".git", "index"));
        var originalObjects = ObjectSnapshot(sentinel);
        var settings = EvaluationProviderClientTests.Settings() with {
            MaxTotalSpend = 0.000000000001m, MaxInputTokensPerRequest = 50000, PairedTrials = 1
        };
        var config = Path.Combine(scope.Root, "evaluation.json");
        await File.WriteAllTextAsync(config, TaskGraphDocumentJson.Serialize(settings));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { typeof(CliOptions).Assembly.Location, "--run-evaluation", config,
            "--evaluation-output", campaign, "--allow-local-verification" }) start.ArgumentList.Add(argument);
        // Poison only this child process. Parallel tests and the user's environment remain untouched.
        start.Environment["GIT_DIR"] = Path.Combine(sentinel, ".git");
        start.Environment["GIT_WORK_TREE"] = sentinel;
        start.Environment["GIT_INDEX_FILE"] = Path.Combine(sentinel, ".git", "index");
        start.Environment["GIT_OBJECT_DIRECTORY"] = Path.Combine(sentinel, ".git", "objects");
        start.Environment["GIT_CONFIG_COUNT"] = "1";
        start.Environment["GIT_CONFIG_KEY_0"] = "core.bare";
        start.Environment["GIT_CONFIG_VALUE_0"] = "true";
        start.Environment.Remove("OPENAI_API_KEY"); start.Environment.Remove("ANTHROPIC_API_KEY");
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 2, (await stdout) + (await stderr));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(campaign, "report.json")));
        Assert.Equal("BudgetExhausted", report.RootElement.GetProperty("status").GetString());
        Assert.Empty(report.RootElement.GetProperty("checkpoint").GetProperty("requests").EnumerateArray());
        Assert.Equal(originalIndex, FileDigest(Path.Combine(sentinel, ".git", "index")));
        Assert.Equal(originalObjects, ObjectSnapshot(sentinel));
        Assert.Equal(originalHead, (await Git(sentinel, ["rev-parse", "HEAD"])).Trim());
        Assert.Equal("preserve this repository", await File.ReadAllTextAsync(Path.Combine(sentinel, "protected.txt")));
        Assert.Equal("uncommitted staged content", await File.ReadAllTextAsync(Path.Combine(sentinel, "pending.txt")));
        foreach (var caseId in new[] { "addition", "subtraction", "multiplication" })
        {
            var source = Path.Combine(campaign, "source", caseId);
            Assert.True(Directory.Exists(Path.Combine(source, ".git")));
            Assert.NotEqual(originalHead, (await Git(source, ["rev-parse", "HEAD"])).Trim());
            var tracked = await Git(source, ["ls-tree", "-r", "--name-only", "HEAD"]);
            Assert.Contains("Calc.cs", tracked); Assert.Contains("Program.cs", tracked);
            Assert.DoesNotContain("protected.txt", tracked); Assert.DoesNotContain("pending.txt", tracked);
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
    }

    private static string FileDigest(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static string[] ObjectSnapshot(string repository) => Directory.EnumerateFiles(Path.Combine(repository, ".git", "objects"), "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(repository, path) + ":" + FileDigest(path)).ToArray();
    private static async Task<string> Git(string directory, string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    private sealed class Scope : IDisposable
    {
        private static readonly string Base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ironpaw-evaluation-isolation-tests"));
        public string Root { get; } = Path.Combine(Base, Guid.NewGuid().ToString("n"));
        public Scope() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var target = Path.GetFullPath(Root);
            if (!target.StartsWith(Base + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Cleanup escaped the test root.");
            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(target, recursive: true);
        }
    }
}
