using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed class SessionLaneScheduler : ISessionLaneScheduler, IDisposable
{
    private sealed class LaneEntry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    private readonly Dictionary<string, LaneEntry> _lanes = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    internal int ActiveLaneCount
    {
        get { lock (_gate) { return _lanes.Count; } }
    }

    public async Task<T> EnqueueAsync<T>(SessionKey sessionKey, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        LaneEntry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_lanes.TryGetValue(sessionKey.Value, out entry!))
            {
                entry = new LaneEntry();
                _lanes.Add(sessionKey.Value, entry);
            }
            entry.RefCount++;
        }

        var acquired = false;
        try
        {
            await entry.Semaphore.WaitAsync(ct);
            acquired = true;
            return await work(ct);
        }
        finally
        {
            if (acquired)
            {
                entry.Semaphore.Release();
            }
            lock (_gate)
            {
                if (--entry.RefCount == 0)
                {
                    _lanes.Remove(sessionKey.Value);
                    entry.Semaphore.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            // Active and queued callers retain their entries until their finally blocks run.
            _disposed = true;
        }
    }
}
