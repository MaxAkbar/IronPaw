using System.Collections.Concurrent;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed class SessionLaneScheduler : ISessionLaneScheduler
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lanes = new(StringComparer.Ordinal);

    public async Task<T> EnqueueAsync<T>(SessionKey sessionKey, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var lane = _lanes.GetOrAdd(sessionKey.Value, _ => new SemaphoreSlim(1, 1));
        await lane.WaitAsync(ct);
        try
        {
            return await work(ct);
        }
        finally
        {
            lane.Release();
        }
    }
}
