using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

public sealed class SessionLaneSchedulerTests
{
    [Fact]
    public async Task EnqueueAsync_SameSessionKey_RunsSequentially()
    {
        var scheduler = new SessionLaneScheduler();
        var sessionKey = new SessionKey("alpha");
        var concurrent = 0;
        var maxConcurrent = 0;

        Task<int> ScheduleWork(int value, int delayMs)
        {
            return scheduler.EnqueueAsync(
                sessionKey,
                async _ =>
                {
                    var active = Interlocked.Increment(ref concurrent);
                    UpdateMax(ref maxConcurrent, active);
                    await Task.Delay(delayMs);
                    Interlocked.Decrement(ref concurrent);
                    return value;
                },
                CancellationToken.None);
        }

        var first = ScheduleWork(1, 70);
        var second = ScheduleWork(2, 70);
        await Task.WhenAll(first, second);

        Assert.Equal(1, Volatile.Read(ref maxConcurrent));
    }

    [Fact]
    public async Task EnqueueAsync_DifferentSessionKeys_CanRunInParallel()
    {
        var scheduler = new SessionLaneScheduler();
        var concurrent = 0;
        var maxConcurrent = 0;

        Task<int> ScheduleWork(string key, int value, int delayMs)
        {
            return scheduler.EnqueueAsync(
                new SessionKey(key),
                async _ =>
                {
                    var active = Interlocked.Increment(ref concurrent);
                    UpdateMax(ref maxConcurrent, active);
                    await Task.Delay(delayMs);
                    Interlocked.Decrement(ref concurrent);
                    return value;
                },
                CancellationToken.None);
        }

        var first = ScheduleWork("a", 1, 120);
        var second = ScheduleWork("b", 2, 120);
        await Task.WhenAll(first, second);

        Assert.True(Volatile.Read(ref maxConcurrent) >= 2);
    }

    [Fact]
    public async Task EnqueueAsync_CancelledWaiterReleasesLaneReference()
    {
        using var scheduler = new SessionLaneScheduler();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = new SessionKey("cancelled-waiter");
        var running = scheduler.EnqueueAsync(key, _ => release.Task, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = scheduler.EnqueueAsync(key, _ => Task.FromResult(2), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult(1);
        await running;
        Assert.Equal(0, scheduler.ActiveLaneCount);
        Assert.Equal(3, await scheduler.EnqueueAsync(key, _ => Task.FromResult(3), CancellationToken.None));
        Assert.Equal(0, scheduler.ActiveLaneCount);
    }

    [Fact]
    public async Task EnqueueAsync_ConcurrentReusePreservesSerialization()
    {
        using var scheduler = new SessionLaneScheduler();
        var key = new SessionKey("reused");
        var active = 0;
        var maximum = 0;
        await Task.WhenAll(Enumerable.Range(0, 500).Select(_ => scheduler.EnqueueAsync(key, async ct =>
        {
            UpdateMax(ref maximum, Interlocked.Increment(ref active));
            await Task.Yield();
            Interlocked.Decrement(ref active);
            return 1;
        }, CancellationToken.None)));
        Assert.Equal(1, maximum);
        Assert.Equal(0, scheduler.ActiveLaneCount);
    }

    [Fact]
    public async Task Dispose_AllowsExistingCallersToDrain()
    {
        var scheduler = new SessionLaneScheduler();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = new SessionKey("drain");
        var running = scheduler.EnqueueAsync(key, _ => release.Task, CancellationToken.None);
        var queued = scheduler.EnqueueAsync(key, _ => Task.FromResult(2), CancellationToken.None);
        scheduler.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            scheduler.EnqueueAsync(key, _ => Task.FromResult(3), CancellationToken.None));
        release.SetResult(1);
        Assert.Equal(1, await running);
        Assert.Equal(2, await queued);
        Assert.Equal(0, scheduler.ActiveLaneCount);
    }

    private static void UpdateMax(ref int maxConcurrent, int value)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref maxConcurrent);
            if (snapshot >= value)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref maxConcurrent, value, snapshot) == snapshot)
            {
                return;
            }
        }
    }
}
