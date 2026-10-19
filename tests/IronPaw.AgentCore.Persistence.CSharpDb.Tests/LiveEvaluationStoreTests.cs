using System.Diagnostics;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence.CSharpDb.Tests;

[TestClass]
public sealed class LiveEvaluationStoreTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "IronPaw-LiveLedger-" + Guid.NewGuid().ToString("n"));
    private string DatabasePath => Path.Combine(_directory, "campaigns.cdb");
    private static readonly CallerIdentity Owner = new("owner", "Owner", true);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [TestMethod]
    public async Task KnownUsageRefundsUnusedReservationAndCompletedReplayHasNoEffects()
    {
        await using var store = await OpenAsync();
        var request = Request("request", 4m);
        Assert.IsTrue((await store.BeginEvaluationRequestAsync("campaign", request, Owner, Ct)).ShouldExecute);
        Assert.AreEqual(4m, (await GetAsync(store)).ChargedSpend);
        var receipt = Receipt(1.25m);
        var completed = await store.CompleteEvaluationRequestAsync("campaign", request.RequestId, receipt, Owner, Ct);
        Assert.AreEqual(1.25m, completed.ChargedSpend);
        Assert.AreEqual(8.75m, completed.RemainingSpend);
        Assert.AreEqual(LiveEvaluationRequestState.Completed, completed.Requests[0].State);
        var repeated = await store.CompleteEvaluationRequestAsync("campaign", request.RequestId, receipt, Owner, Ct);
        Assert.AreEqual(completed.Revision, repeated.Revision);
        var replay = await store.BeginEvaluationRequestAsync("campaign", request, Owner, Ct);
        Assert.IsFalse(replay.ShouldExecute);
        Assert.AreEqual(receipt, replay.Invocation.Receipt);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CompleteEvaluationRequestAsync("campaign", request.RequestId,
            receipt with { ActualSpend = 0m }, Owner, Ct));
    }

    [TestMethod]
    public async Task SharedCapAtomicallyCoversBothStrategiesAndAllTrials()
    {
        await using var store = await OpenAsync();
        async Task<bool> ReserveAsync(string id, string strategy, string trial)
        {
            try
            {
                return (await store.BeginEvaluationRequestAsync("campaign", Request(id, 6m) with { Strategy = strategy, TrialId = trial }, Owner, Ct)).ShouldExecute;
            }
            catch (WorkflowBudgetExceededException) { return false; }
        }
        var admitted = await Task.WhenAll(ReserveAsync("first", "direct", "one"), ReserveAsync("second", "graph", "one"),
            ReserveAsync("third", "graph", "two"));
        Assert.ContainsSingle(value => value, admitted);
        Assert.AreEqual(6m, (await GetAsync(store)).ChargedSpend);
    }

    [TestMethod]
    public async Task ConcurrentSameRequestGetsOneDispatchPermit()
    {
        await using var store = await OpenAsync();
        async Task<bool> ReserveAsync()
        {
            try { return (await store.BeginEvaluationRequestAsync("campaign", Request("same", 3m), Owner, Ct)).ShouldExecute; }
            catch (WorkflowReconciliationRequiredException) { return false; }
        }
        Assert.ContainsSingle(value => value, await Task.WhenAll(ReserveAsync(), ReserveAsync(), ReserveAsync()));
        Assert.ContainsSingle((await GetAsync(store)).Requests);
    }

    [TestMethod]
    public async Task RestartTurnsStartedIntoUnknownRetainsMoneyAndNeverRepeatsPaidDispatch()
    {
        var request = Request("paid", 4m);
        await using (var store = await OpenAsync())
            await store.BeginEvaluationRequestAsync("campaign", request, Owner, Ct);
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var recovered = await GetAsync(reopened);
        Assert.IsTrue(recovered.NeedsReconciliation);
        Assert.AreEqual(4m, recovered.ChargedSpend);
        Assert.AreEqual(LiveEvaluationRequestState.NeedsReconciliation, recovered.Requests[0].State);
        await Assert.ThrowsExactlyAsync<WorkflowReconciliationRequiredException>(() => reopened.BeginEvaluationRequestAsync("campaign", request, Owner, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowReconciliationRequiredException>(() => reopened.BeginEvaluationRequestAsync("campaign", Request("other", 1m), Owner, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowReconciliationRequiredException>(() => reopened.CompleteEvaluationRequestAsync("campaign", "paid", Receipt(1m), Owner, Ct));
        var reconciled = await reopened.ReconcileEvaluationRequestAsync("campaign", "paid", Receipt(1m), "Confirmed complete usage from provider record", Owner, Ct);
        Assert.IsFalse(reconciled.NeedsReconciliation);
        Assert.AreEqual(1m, reconciled.ChargedSpend);
        Assert.IsFalse((await reopened.BeginEvaluationRequestAsync("campaign", request, Owner, Ct)).ShouldExecute);
        Assert.IsTrue((await reopened.BeginEvaluationRequestAsync("campaign", Request("next", 1m), Owner, Ct)).ShouldExecute);
    }

    [TestMethod]
    public async Task PartialUsageRetainsReservationUntilExplicitReconciliation()
    {
        await using var store = await OpenAsync();
        await store.BeginEvaluationRequestAsync("campaign", Request("partial", 4m), Owner, Ct);
        var receipt = new LiveEvaluationReceipt(false, 0.5m, 10, null, "{\"reason\":\"cancelled stream\"}");
        var partial = await store.CompleteEvaluationRequestAsync("campaign", "partial", receipt, Owner, Ct);
        Assert.IsTrue(partial.NeedsReconciliation);
        Assert.AreEqual(4m, partial.ChargedSpend);
        var repeated = await store.CompleteEvaluationRequestAsync("campaign", "partial", receipt, Owner, Ct);
        Assert.AreEqual(partial.Revision, repeated.Revision);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CompleteEvaluationRequestAsync("campaign", "partial", Receipt(1m), Owner, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.ReconcileEvaluationRequestAsync("campaign", "partial", Receipt(0.1m), "Reduced observed bill", Owner, Ct));
        var reconciled = await store.ReconcileEvaluationRequestAsync("campaign", "partial", Receipt(1m), "Established complete bill", Owner, Ct);
        Assert.AreEqual(1m, reconciled.ChargedSpend);
    }

    [TestMethod]
    [DataRow(11)]
    [DataRow(5)]
    public async Task ActualOverrunIsRecordedAndBlocksAllFurtherPaidWorkEvenBelowCap(int observedSpend)
    {
        await using var store = await OpenAsync();
        await store.BeginEvaluationRequestAsync("campaign", Request("overshoot", 4m), Owner, Ct);
        var completed = await store.CompleteEvaluationRequestAsync("campaign", "overshoot", Receipt(observedSpend), Owner, Ct);
        Assert.AreEqual(observedSpend, completed.ChargedSpend);
        Assert.IsTrue(completed.HasReservationOverrun);
        Assert.AreEqual(Math.Max(0, 10m - observedSpend), completed.RemainingSpend);
        await Assert.ThrowsExactlyAsync<WorkflowBudgetExceededException>(() => store.BeginEvaluationRequestAsync("campaign", Request("later", 1m), Owner, Ct));
    }

    [TestMethod]
    public async Task UnknownObservedOverrunCannotUnderchargeReservationOrDropObservedUsage()
    {
        await using var store = await OpenAsync();
        await store.BeginEvaluationRequestAsync("campaign", Request("partial", 4m), Owner, Ct);
        var partial = await store.CompleteEvaluationRequestAsync("campaign", "partial", new(false, 12m, 100, 20, "{}"), Owner, Ct);
        Assert.AreEqual(12m, partial.ChargedSpend);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.ReconcileEvaluationRequestAsync("campaign", "partial", Receipt(11m), "Discard observed spend", Owner, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.ReconcileEvaluationRequestAsync("campaign", "partial", new(true, 12m, 99, 20, "{}"), "Discard observed tokens", Owner, Ct));
        var reconciled = await store.ReconcileEvaluationRequestAsync("campaign", "partial", new(true, 12m, 100, 20, "{}"), "Confirmed full usage", Owner, Ct);
        Assert.AreEqual(12m, reconciled.ChargedSpend);
        Assert.IsTrue(reconciled.HasReservationOverrun);
    }

    [TestMethod]
    public async Task TokenCeilingOverrunBlocksFutureAdmissionEvenWhenKnownCostFitsReservation()
    {
        await using var store = await OpenAsync();
        await store.BeginEvaluationRequestAsync("campaign", Request("overrun", 4m), Owner, Ct);
        var completed = await store.CompleteEvaluationRequestAsync("campaign", "overrun", Receipt(1m) with { ReservationAssumptionsExceeded = true }, Owner, Ct);
        Assert.AreEqual(1m, completed.ChargedSpend);
        Assert.IsTrue(completed.HasReservationOverrun);
        await Assert.ThrowsExactlyAsync<WorkflowBudgetExceededException>(() => store.BeginEvaluationRequestAsync("campaign", Request("later", 1m), Owner, Ct));
    }

    [TestMethod]
    public async Task ReconciliationCannotEraseAnObservedTokenCeilingViolation()
    {
        await using var store = await OpenAsync();
        await store.BeginEvaluationRequestAsync("campaign", Request("partial", 4m), Owner, Ct);
        await store.CompleteEvaluationRequestAsync("campaign", "partial", new(false, 1m, 100, null, "{}", true), Owner, Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.ReconcileEvaluationRequestAsync("campaign", "partial", Receipt(1m), "Ignore invalid assumptions", Owner, Ct));
        var reconciled = await store.ReconcileEvaluationRequestAsync("campaign", "partial", Receipt(1m) with { ReservationAssumptionsExceeded = true }, "Established complete usage", Owner, Ct);
        Assert.IsTrue(reconciled.HasReservationOverrun);
    }

    [TestMethod]
    [DataRow("started")]
    [DataRow("completed")]
    public async Task AbruptProcessKillPreservesReservationOrKnownReceiptWithoutPaidRedispatch(string phase)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(LiveEvaluationStoreTests).Assembly.Location);
        start.ArgumentList.Add("evaluation-crash-writer");
        start.ArgumentList.Add(DatabasePath);
        start.ArgumentList.Add(phase);
        using var child = Process.Start(start)!;
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.AreEqual("evaluation-committed", ready);
            await Assert.ThrowsAsync<IOException>(() => CSharpDbWorkflowStore.OpenAsync(DatabasePath));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(child.HasExited);
            await using var recovered = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
            var checkpoint = await GetAsync(recovered);
            var request = Request("paid", 4m);
            Assert.ContainsSingle(checkpoint.Requests);
            if (phase == "started")
            {
                Assert.IsTrue(checkpoint.NeedsReconciliation);
                Assert.AreEqual(4m, checkpoint.ChargedSpend);
                await Assert.ThrowsExactlyAsync<WorkflowReconciliationRequiredException>(() => recovered.BeginEvaluationRequestAsync("campaign", request, Owner, Ct));
                await Assert.ThrowsExactlyAsync<WorkflowReconciliationRequiredException>(() => recovered.BeginEvaluationRequestAsync("campaign", Request("new", 1m), Owner, Ct));
            }
            else
            {
                Assert.IsFalse(checkpoint.NeedsReconciliation);
                Assert.AreEqual(1m, checkpoint.ChargedSpend);
                var replay = await recovered.BeginEvaluationRequestAsync("campaign", request, Owner, Ct);
                Assert.IsFalse(replay.ShouldExecute);
                Assert.Contains("bounded response", replay.Invocation.Receipt!.EvidenceJson);
            }
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestMethod]
    [DataRow(WorkflowControl.Pause)]
    [DataRow(WorkflowControl.Cancel)]
    public async Task KnownReceiptCanSettleAfterControlButNewPaidWorkCannotStart(WorkflowControl control)
    {
        await using var store = await OpenAsync();
        await store.BeginEvaluationRequestAsync("campaign", Request("active", 3m), Owner, Ct);
        await store.SetEvaluationControlAsync("campaign", control, Owner, Ct);
        var completed = await store.CompleteEvaluationRequestAsync("campaign", "active", Receipt(1m), Owner, Ct);
        Assert.AreEqual(control, completed.Control);
        Assert.AreEqual(1m, completed.ChargedSpend);
        if (control == WorkflowControl.Pause)
        {
            await Assert.ThrowsExactlyAsync<WorkflowSuspendedException>(() => store.BeginEvaluationRequestAsync("campaign", Request("later", 1m), Owner, Ct));
            await store.SetEvaluationControlAsync("campaign", WorkflowControl.None, Owner, Ct);
            Assert.IsTrue((await store.BeginEvaluationRequestAsync("campaign", Request("later", 1m), Owner, Ct)).ShouldExecute);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<WorkflowCancelledException>(() => store.BeginEvaluationRequestAsync("campaign", Request("later", 1m), Owner, Ct));
            await Assert.ThrowsExactlyAsync<WorkflowCancelledException>(() => store.SetEvaluationControlAsync("campaign", WorkflowControl.None, Owner, Ct));
        }
    }

    [TestMethod]
    public async Task ReopeningCompletedRequestRetainsReceiptWithoutChargingAgain()
    {
        await using (var store = await OpenAsync())
        {
            await store.BeginEvaluationRequestAsync("campaign", Request("done", 3m), Owner, Ct);
            await store.CompleteEvaluationRequestAsync("campaign", "done", Receipt(1m), Owner, Ct);
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        Assert.IsFalse((await GetAsync(reopened)).NeedsReconciliation);
        Assert.AreEqual(1m, (await GetAsync(reopened)).ChargedSpend);
        Assert.IsFalse((await reopened.BeginEvaluationRequestAsync("campaign", Request("done", 3m), Owner, Ct)).ShouldExecute);
    }

    [TestMethod]
    public async Task ConfigurationAndPricingBindingAreImmutableAcrossResume()
    {
        await using var store = await OpenAsync();
        var current = await GetAsync(store);
        Assert.AreEqual(current, await store.CreateEvaluationAsync(Definition(), Owner, Ct));
        foreach (var changed in new[] { Definition() with { Model = "different" }, Definition() with { Provider = "different" },
            Definition() with { Currency = "EUR" }, Definition() with { MaxTotalSpend = 20m },
            Definition() with { BindingJson = "{\"pricing\":\"changed\"}" } })
            await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateEvaluationAsync(changed, Owner, Ct));
    }

    [TestMethod]
    public async Task OriginalOwnerIsRequiredForEveryReadWriteAndReconciliation()
    {
        await using var store = await OpenAsync();
        var stranger = new CallerIdentity("different", "Owner", true);
        var downgraded = Owner with { IsOwner = false };
        foreach (var caller in new[] { stranger, downgraded })
        {
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.GetEvaluationAsync("campaign", caller, Ct));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.CreateEvaluationAsync(Definition(), caller, Ct));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.BeginEvaluationRequestAsync("campaign", Request("paid", 1m), caller, Ct));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.SetEvaluationControlAsync("campaign", WorkflowControl.Pause, caller, Ct));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.RecordEvaluationTrialAsync("campaign", Trial(), caller, Ct));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.CompleteEvaluationRequestAsync("campaign", "paid", Receipt(1m), caller, Ct));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.ReconcileEvaluationRequestAsync("campaign", "paid", Receipt(1m), "known", caller, Ct));
        }
    }

    [TestMethod]
    public async Task RequestIdentityCannotSwapPromptStrategyTrialOrReservation()
    {
        await using var store = await OpenAsync();
        var request = Request("paid", 1m);
        await store.BeginEvaluationRequestAsync("campaign", request, Owner, Ct);
        foreach (var changed in new[] { request with { RequestFingerprint = new string('b', 64) }, request with { Strategy = "graph" },
            request with { TrialId = "another" }, request with { ReservedSpend = 2m } })
            await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.BeginEvaluationRequestAsync("campaign", changed, Owner, Ct));
    }

    [TestMethod]
    public async Task HistoricalTrialResultIsImmutableAndPreventsFreshPaidDispatch()
    {
        await using var store = await OpenAsync();
        await store.BeginEvaluationRequestAsync("campaign", Request("paid", 1m), Owner, Ct);
        await Assert.ThrowsExactlyAsync<WorkflowReconciliationRequiredException>(() => store.RecordEvaluationTrialAsync("campaign", Trial(), Owner, Ct));
        await store.CompleteEvaluationRequestAsync("campaign", "paid", Receipt(0.25m), Owner, Ct);
        var trial = Trial();
        var saved = await store.RecordEvaluationTrialAsync("campaign", trial, Owner, Ct);
        Assert.ContainsSingle(saved.Trials);
        Assert.AreEqual(saved.Revision, (await store.RecordEvaluationTrialAsync("campaign", trial, Owner, Ct)).Revision);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.RecordEvaluationTrialAsync("campaign", trial with { ResultJson = "{\"outcome\":\"replaced\"}" }, Owner, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.BeginEvaluationRequestAsync("campaign", Request("fresh", 1m), Owner, Ct));
        Assert.IsTrue((await store.BeginEvaluationRequestAsync("campaign", Request("other-strategy", 1m) with { Strategy = "graph" }, Owner, Ct)).ShouldExecute);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task NonpositiveReservationsAreRejected(int reserve)
    {
        await using var store = await OpenAsync();
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => store.BeginEvaluationRequestAsync("campaign", Request("invalid", reserve), Owner, Ct));
        Assert.IsEmpty((await GetAsync(store)).Requests);
    }

    [TestMethod]
    public async Task MalformedIdentityUsageCurrencyAndBindingsAreRejectedBeforeMutation()
    {
        await using var store = await OpenAsync();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.CreateEvaluationAsync(Definition() with { Currency = "usd" }, Owner, Ct));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.CreateEvaluationAsync(Definition() with { CampaignId = " padded" }, Owner, Ct));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.CreateEvaluationAsync(Definition() with { BindingJson = "invalid" }, Owner, Ct));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.BeginEvaluationRequestAsync("campaign", Request("invalid", 1m) with { RequestFingerprint = "not-a-digest" }, Owner, Ct));
        await store.BeginEvaluationRequestAsync("campaign", Request("paid", 1m), Owner, Ct);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.CompleteEvaluationRequestAsync("campaign", "paid", new(true, 1m, null, 1, "{}"), Owner, Ct));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.CompleteEvaluationRequestAsync("campaign", "paid", Receipt(-1m), Owner, Ct));
        Assert.AreEqual(LiveEvaluationRequestState.Started, (await GetAsync(store)).Requests[0].State);
    }

    private async Task<CSharpDbWorkflowStore> OpenAsync()
    {
        var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        await store.CreateEvaluationAsync(Definition(), Owner, Ct);
        return store;
    }
    private static LiveEvaluationDefinition Definition() => new("campaign", "provider", "exact-model", "USD", 10m,
        "{\"pricing\":{\"input\":1,\"output\":2},\"workloads\":[\"fixture\"]}");
    private static LiveEvaluationRequest Request(string id, decimal reserve) => new(id, "trial", "direct", new string('a', 64), reserve);
    private static LiveEvaluationReceipt Receipt(decimal cost) => new(true, cost, 100, 20, "{\"terminal\":true}");
    private static LiveEvaluationTrialResult Trial() => new("trial", "direct", new string('c', 64), "{\"outcome\":\"passed\",\"latencyMs\":12}");
    private static async Task<LiveEvaluationCheckpoint> GetAsync(CSharpDbWorkflowStore store) => (await store.GetEvaluationAsync("campaign", Owner, Ct))!;
    [TestCleanup]
    public void Cleanup()
    {
        var full = Path.GetFullPath(_directory);
        if (Directory.Exists(full) && full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            Directory.Delete(full, recursive: true);
    }
}
