using System.Text.Json;
using System.IO;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Hosting;

internal static class LocalReleaseSmoke
{
    internal sealed record SmokeReport(string Host, bool Completed, string Provider, bool LiveProviderCalled,
        bool StateReopened, string RunId, int TranscriptMessages, int HistoryEntries, string Runtime, bool BrowserInstallationVerified = false,
        bool ToolArtifactVerified = false);
    internal const string Prompt = "Create a readiness plan and write it to Readiness.md.";
    internal static string Prepare(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new ArgumentException("Smoke evidence needs an absolute path to a new directory.");
        var root = Path.GetFullPath(directory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new ArgumentException("Smoke evidence requires a new or empty directory.");
        Directory.CreateDirectory(root);
        using (new FileStream(Path.Combine(root, "smoke.marker"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        Directory.CreateDirectory(Path.Combine(root, "workspace"));
        return root;
    }

    internal static async Task<SmokeReport> VerifyAsync(string host, string workspace, IWorkflowStore workflows, ISessionStore sessions,
        ITranscriptStore transcripts, IRunHistoryStore history, CancellationToken ct)
    {
        var runs = await workflows.ListAsync(ct).ConfigureAwait(false);
        if (runs.Count != 1 || runs[0].Status != WorkflowStatus.Succeeded || runs[0].Request.Provider != "local" ||
            runs[0].Result is not { Status: AgentRunStatus.Succeeded } result || string.IsNullOrWhiteSpace(result.FinalAssistantMessage))
            throw new InvalidOperationException("The isolated local workflow did not persist successful completion.");
        var savedTranscript = await transcripts.LoadAsync(runs[0].Request.SessionKey, ct).ConfigureAwait(false);
        var savedHistory = await history.LoadAllAsync(ct).ConfigureAwait(false);
        if ((await sessions.LoadAllAsync(ct).ConfigureAwait(false)).Count != 1 || savedHistory.Count != 1 ||
            savedHistory[0].Status != AgentRunStatus.Succeeded || savedHistory[0].RunId != result.RunId.Value ||
            !savedTranscript.Any(message => message.Role == ChatRole.User && message.Content == Prompt) ||
            !savedTranscript.Any(message => message.Role == ChatRole.Assistant && message.Content == result.FinalAssistantMessage))
            throw new InvalidOperationException("The local workflow's application records did not survive reopen.");
        var artifact = Path.Combine(workspace, "Readiness.md");
        var writes = (runs[0].ToolExecutions ?? []).Where(execution => execution.Call.ToolName == "write_file").ToArray();
        if (writes.Length != 1 || writes[0].State != WorkflowToolState.Completed || writes[0].Result is not { IsError: false } ||
            !File.Exists(artifact) || !(await File.ReadAllTextAsync(artifact, ct).ConfigureAwait(false)).Contains(Prompt, StringComparison.Ordinal))
            throw new InvalidOperationException("The local workflow's tool artifact and durable tool outcome did not qualify.");
        return new(host, true, "local", false, true, result.RunId.Value, savedTranscript.Count, savedHistory.Count,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, ToolArtifactVerified: true);
    }
    internal static Task WriteReportAsync(string root, object report) => File.WriteAllTextAsync(Path.Combine(root, "smoke-report.json"),
        JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
}
