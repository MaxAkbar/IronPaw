using System.Collections.Concurrent;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed class SessionLaneScheduler : ISessionLaneScheduler, IDisposable
{
    private sealed class LaneEntry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount; // mutated via Interlocked
    }

    private readonly ConcurrentDictionary<string, LaneEntry> _lanes = new(StringComparer.Ordinal);
    private bool _disposed;

    public async Task<T> EnqueueAsync<T>(SessionKey sessionKey, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Atomically claim a reference so the entry cannot be evicted between GetOrAdd and WaitAsync.
        LaneEntry entry;
        while (true)
        {
            entry = _lanes.GetOrAdd(sessionKey.Value, _ => new LaneEntry());
            var prev = Interlocked.Increment(ref entry.RefCount);

            // If prev was 0 before our increment it means another thread already decremented to 0
            // and may be in the process of removing this entry. Retry with a fresh one.
            if (prev > 0)
            {
                // Verify we incremented the entry that is still current in the dictionary.
                if (_lanes.TryGetValue(sessionKey.Value, out var current) && ReferenceEquals(current, entry))
                {
                    break;
                }
            }

            // The entry we got is stale; undo our increment and try again.
            Interlocked.Decrement(ref entry.RefCount);
        }

        await entry.Semaphore.WaitAsync(ct);
        try
        {
            return await work(ct);
        }
        finally
        {
            entry.Semaphore.Release();

            // Evict the entry when no other callers hold a reference to it.
            if (Interlocked.Decrement(ref entry.RefCount) == 0)
            {
                _lanes.TryRemove(new KeyValuePair<string, LaneEntry>(sessionKey.Value, entry));
                entry.Semaphore.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var entry in _lanes.Values)
        {
            entry.Semaphore.Dispose();
        }

        _lanes.Clear();
    }
}
