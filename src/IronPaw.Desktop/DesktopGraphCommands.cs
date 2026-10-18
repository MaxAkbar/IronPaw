using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Providers;

namespace IronPaw.Desktop;

/// <summary>Provider preflight is pure; read-only graph actions never load provider configuration.</summary>
internal static class DesktopGraphCommands
{
    public static Task<TaskGraphCheckpoint> StartAsync(TaskGraphHostController host, TaskGraphLaunchDocument document,
        CallerIdentity caller, bool allowLocalVerification, CancellationToken ct)
    {
        host.ValidateLaunch(document, caller, allowLocalVerification);
        RequireSupportedProvider(document.Options.Provider);
        return host.StartAsync(document, caller, allowLocalVerification, ct);
    }

    public static async Task<TaskGraphCheckpoint> ResumeAsync(TaskGraphHostController host, string graphId,
        CallerIdentity caller, bool allowLocalVerification, CancellationToken ct)
    {
        RequireSupportedProvider(await host.GetRequiredProviderAsync(graphId, caller, ct,
            allowLocalVerification: allowLocalVerification));
        return await host.ResumeAsync(graphId, caller, allowLocalVerification, ct);
    }

    public static async Task<TaskGraphCheckpoint> RecoverTaskAsync(TaskGraphHostController host, string graphId,
        string taskId, CallerIdentity caller, bool allowLocalVerification, CancellationToken ct)
    {
        RequireSupportedProvider(await host.GetRequiredProviderAsync(graphId, caller, ct,
            recoverTaskId: taskId, allowLocalVerification: allowLocalVerification));
        return await host.RecoverTaskAsync(graphId, taskId, caller, allowLocalVerification, ct);
    }

    private static void RequireSupportedProvider(string? provider)
    {
        if (provider is not null && !ProviderDefaults.DefaultModels.Keys.Contains(provider, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported task graph provider '{provider}'. Supported providers: {string.Join(", ", ProviderDefaults.DefaultModels.Keys)}.");
    }
}
