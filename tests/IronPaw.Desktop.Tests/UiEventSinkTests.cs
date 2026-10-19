using IronPaw.AgentCore.Abstractions;
using IronPaw.Desktop;

namespace IronPaw.Desktop.Tests;

public sealed class UiEventSinkTests
{
    [Fact]
    public async Task AStalledViewCannotBlockProducersAndDrainCallbacksDoNotHoldTheBufferLock()
    {
        using var sink = new UiEventSink();
        await sink.PublishAsync(Text("first"), default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var blockedView = Task.Run(() => sink.DrainTo(_ => { entered.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Run(async () => {
                for (var i = 0; i < 10000; i++) Assert.True(sink.PublishAsync(Text("x"), default).IsCompletedSuccessfully);
                await Task.CompletedTask;
            }).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.InRange(sink.PendingCount, 1, 512);
            Assert.InRange(sink.PendingCharacters, 1, 131072);
        }
        finally { release.Set(); await blockedView; }
    }

    [Fact]
    public async Task CoalescingPreservesOrderAndDoesNotMergeDifferentAgentsOrRuns()
    {
        using var sink = new UiEventSink();
        await sink.PublishAsync(Text("a"), default);
        await sink.PublishAsync(Text("b"), default);
        await sink.PublishAsync(Text("child", agent: "child"), default);
        await sink.PublishAsync(Text("different", run: "other"), default);
        await sink.PublishAsync(Text("c"), default);
        UiEventBatch? shown = null;
        sink.DrainTo(batch => shown = batch);
        Assert.Equal(new[] { "ab", "child", "different", "c" }, shown!.Updates.Select(update => update.Content));
        Assert.Equal(1, shown.Coalesced);
        Assert.Equal(2, shown.Updates[0].EventCount);
    }

    [Fact]
    public async Task OverflowFavorsLifecycleAndToolEventsAndDisclosesOmissions()
    {
        using var sink = new UiEventSink(capacity: 3);
        await sink.PublishAsync(Event("run_started", AgentEventStream.Lifecycle), default);
        await sink.PublishAsync(Text("a"), default);
        await sink.PublishAsync(Event("tool_started", AgentEventStream.Tool), default);
        await sink.PublishAsync(Text("b"), default);
        await sink.PublishAsync(Event("run_cancelled", AgentEventStream.Lifecycle), default);
        UiEventBatch? shown = null;
        sink.DrainTo(batch => shown = batch);
        Assert.Equal(new[] { "run_started", "tool_started", "run_cancelled" }, shown!.Updates.Select(update => update.Type));
        Assert.Equal(2, shown.Omitted);
        Assert.Equal(0, sink.PendingCharacters);
    }

    [Fact]
    public async Task EvenLifecycleOnlyOverflowIsBoundedAndExplicit()
    {
        using var sink = new UiEventSink(capacity: 2);
        for (var i = 0; i < 100; i++) await sink.PublishAsync(Event("lifecycle_" + i, AgentEventStream.Lifecycle), default);
        UiEventBatch? shown = null;
        sink.DrainTo(batch => shown = batch);
        Assert.Equal(2, shown!.Updates.Count);
        Assert.Equal(98, shown.Omitted);
    }

    [Fact]
    public async Task HugePayloadsHaveAnExplicitCharacterBoundAndMutableObjectsAreNotRetainedOrFormatted()
    {
        using var sink = new UiEventSink(maxCharacters: 4096);
        for (var i = 0; i < 100; i++) await sink.PublishAsync(Text(new string('x', 100000), run: "run-" + i), default);
        Assert.InRange(sink.PendingCharacters, 1, 4096);
        UiEventBatch? shown = null;
        sink.DrainTo(batch => shown = batch);
        Assert.Equal(100, shown!.Truncated);
        var data = new Dictionary<string, object?> { ["object"] = new ForbiddenFormatter(), ["text"] = "before" };
        await sink.PublishAsync(Event("custom", AgentEventStream.Diagnostics) with { Data = data }, default);
        data["text"] = "after";
        sink.DrainTo(batch => shown = batch);
        Assert.Contains("[structured data]", shown!.Updates[0].Content);
        Assert.Contains("before", shown.Updates[0].Content);
        Assert.DoesNotContain("after", shown.Updates[0].Content);
    }

    [Fact]
    public async Task PresentationFailureDoesNotEscapeOrRetryAPartiallyDisplayedBatch()
    {
        using var sink = new UiEventSink();
        await sink.PublishAsync(Text("old"), default);
        sink.DrainTo(_ => throw new InvalidOperationException("closed view"));
        await sink.PublishAsync(Text("new"), default);
        UiEventBatch? shown = null;
        sink.DrainTo(batch => shown = batch);
        Assert.Equal("new", Assert.Single(shown!.Updates).Content);
        Assert.Equal(1, shown.PresentationFailures);
    }

    [Fact]
    public async Task ParallelPublishersStayBoundedAndDisposalMakesLaterDeliveryANoop()
    {
        using var sink = new UiEventSink(capacity: 16, maxCharacters: 4096);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Task.Run(async () => {
            for (var i = 0; i < 1000; i++) await sink.PublishAsync(Text("x", run: "run-" + index), default);
        })));
        Assert.InRange(sink.PendingCount, 1, 16);
        Assert.InRange(sink.PendingCharacters, 1, 4096);
        sink.Dispose();
        await sink.PublishAsync(Text("late"), new CancellationToken(true));
        sink.DrainTo(_ => Assert.Fail("A disposed view received a callback"));
        Assert.Equal(0, sink.PendingCount);
        Assert.Equal(0, sink.PendingCharacters);
    }

    private static AgentEvent Text(string content, string run = "root", string? agent = null) =>
        Event("assistant_delta", AgentEventStream.Assistant, run) with { AgentId = agent, Data = EventData.Of(("text", content)) };
    private static AgentEvent Event(string type, AgentEventStream stream, string run = "root") => new(new(run), new("session"), stream, type, DateTimeOffset.UtcNow, EventData.Of());
    private sealed class ForbiddenFormatter { public override string ToString() => throw new InvalidOperationException("Payload formatting must not execute arbitrary code"); }
}
