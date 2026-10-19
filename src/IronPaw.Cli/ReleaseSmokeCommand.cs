using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.Hosting;
using IronPaw.AgentCore.Browser;

namespace IronPaw.Cli;

internal static class ReleaseSmokeCommand
{
    public static async Task<int> ExecuteAsync(string directory)
    {
        try
        {
            var root = LocalReleaseSmoke.Prepare(directory);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using (var application = AppBootstrapper.Build(stateRoot: Path.Combine(root, "state"), workspaceRoot: Path.Combine(root, "workspace"), settings: new IronPawSettings()))
            {
                var result = await application.Orchestrator.RunAsync(new(new("release-smoke"), LocalReleaseSmoke.Prompt,
                    "local", "rule-based-v1", new(ToolProfile.Coding), CallerIdentity.LocalOwner()), NullAgentEventSink.Instance, deadline.Token);
                if (result.Status != AgentRunStatus.Succeeded) throw new InvalidOperationException("Local release workflow did not succeed.");
            }
            using var reopened = AppBootstrapper.Build(stateRoot: Path.Combine(root, "state"), workspaceRoot: Path.Combine(root, "workspace"), settings: new IronPawSettings());
            var report = await LocalReleaseSmoke.VerifyAsync("CLI", Path.Combine(root, "workspace"), reopened.WorkflowStore, reopened.SessionStore, reopened.TranscriptStore, reopened.RunHistoryStore, deadline.Token);
            await using (var browser = new ChromiumBrowserService(new(1, ["https://example.invalid"])))
                await browser.VerifyInstallationAsync(deadline.Token);
            report = report with { BrowserInstallationVerified = true };
            await LocalReleaseSmoke.WriteReportAsync(root, report);
            Console.WriteLine("Isolated CLI release smoke passed. No live provider was called.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("Release smoke failed: " + error.Message); return 1; }
    }
}
