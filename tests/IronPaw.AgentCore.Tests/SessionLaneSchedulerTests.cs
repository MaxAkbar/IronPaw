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
