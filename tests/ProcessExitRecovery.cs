using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.Testing;

internal static class ProcessExitRecovery
{
    // Only for recovery probes after the owned child has exited. While-live writer
    // exclusion is checked separately against the normal fail-fast store API.
    public static async Task<CSharpDbWorkflowStore> OpenWorkflowAsync(string path,
        CancellationToken ct = default, TimeProvider? timeProvider = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { return await CSharpDbWorkflowStore.OpenAsync(path, deadline.Token, timeProvider); }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33 && !deadline.IsCancellationRequested)
            { await Task.Delay(25, deadline.Token); }
        }
    }
}
