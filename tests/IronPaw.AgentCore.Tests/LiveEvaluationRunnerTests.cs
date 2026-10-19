using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class LiveEvaluationRunnerTests
{
    private static readonly CallerIdentity Owner = new("live-evaluation-test-owner", "Local evaluation test", true);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [TestMethod]
    public async Task ThreePairsUseTheSameInputsChecksAndCaps_AndCompletedResumeNeverCallsProvider()
    {
        await using var scope = await Scope.OpenAsync();
        var client = new FixtureClient();
        var report = await scope.Runner(client).StartAsync(Settings() with { PairedTrials = 3 }, "paired", scope.Root, Owner, true, Ct);

        Assert.AreEqual("Completed", report.Status);
        Assert.AreEqual("NOT_ESTABLISHED", report.LiveBaselineQualification);
        Assert.AreEqual(6, report.Trials.Length);
        foreach (var trial in report.Trials)
        {
            Assert.IsTrue(trial.VerifiedCompletion, TaskGraphDocumentJson.Serialize(trial));
        }
        Assert.AreEqual(21, client.Calls);
        foreach (var request in report.Checkpoint.Requests)
        {
            Assert.AreEqual(LiveEvaluationRequestState.Completed, request.State);
        }
        Assert.IsTrue(client.ParallelBranchesObserved);
        foreach (var pair in report.Trials.GroupBy(t => t.TrialId))
        {
            Assert.ContainsSingle(pair.Select(t => t.BaseCommit).Distinct());
            Assert.ContainsSingle(pair.Select(t => t.InputDigest).Distinct());
            Assert.AreSequenceEqual(new[] { "direct", "graph" }, pair.Select(t => t.Strategy).Order());
            foreach (var trial in pair)
            {
                Assert.AreEqual(2, trial.Tasks.Single(t => t.TaskId is "direct" or "join").Verifications.Length);
            }
        }
        var snapshot = await File.ReadAllTextAsync(Path.Combine(scope.Root, "report.json"));
        Assert.Contains("NOT_ESTABLISHED", snapshot);
        await scope.ReopenAsync();
        var reject = new FixtureClient { Reject = true };
        var resumed = await scope.Runner(reject).ResumeAsync("paired", scope.Root, Owner, true, Ct);
        Assert.AreEqual("Completed", resumed.Status);
        Assert.AreEqual(0, reject.Calls);
        Assert.AreEqual(report.Checkpoint.Requests.Length, resumed.Checkpoint.Requests.Length);
        Assert.AreEqual(report.Checkpoint.ChargedSpend, resumed.Checkpoint.ChargedSpend);
        Assert.AreSequenceEqual(report.Trials.Select(t => t.ElapsedWallMilliseconds), resumed.Trials.Select(t => t.ElapsedWallMilliseconds));
        Assert.DoesNotContain(directory => Path.GetFileName(directory) is "bin" or "obj", Directory.EnumerateDirectories(Path.Combine(scope.Root, "source"), "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task InterruptedProviderPreservesReservationAndStopsFurtherTrialsAfterReopen()
    {
        await using var scope = await Scope.OpenAsync();
        var client = new FixtureClient { Interrupt = true };
        var report = await scope.Runner(client).StartAsync(Settings(), "interrupted", scope.Root, Owner, true, Ct);
        Assert.AreEqual("NeedsReconciliation", report.Status);
        Assert.IsTrue(report.Checkpoint.NeedsReconciliation);
        Assert.IsEmpty(report.Trials);
        Assert.IsNotEmpty(report.PendingTrials);
        foreach (var request in report.Checkpoint.Requests)
        {
            Assert.AreEqual(request.Request.ReservedSpend, request.ChargedSpend);
        }
        Assert.IsTrue(report.Checkpoint.ChargedSpend > 0);
        await scope.ReopenAsync();
        var reject = new FixtureClient { Reject = true };
        var resumed = await scope.Runner(reject).ResumeAsync("interrupted", scope.Root, Owner, true, Ct);
        Assert.AreEqual("NeedsReconciliation", resumed.Status);
        Assert.AreEqual(0, reject.Calls);
        Assert.AreEqual(report.Checkpoint.ChargedSpend, resumed.Checkpoint.ChargedSpend);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => scope.Runner(reject).InspectAsync("interrupted", scope.Root, Owner with { Id = "different" }, Ct));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => scope.Runner(reject).InspectAsync("interrupted", scope.Root + "-moved", Owner, Ct));
    }

    [TestMethod]
    public async Task InsufficientCapPreventsProviderDispatchAndPreservesFailedTrial()
    {
        await using var scope = await Scope.OpenAsync();
        var client = new FixtureClient();
        var report = await scope.Runner(client).StartAsync(Settings() with { MaxTotalSpend = .000001m }, "cap", scope.Root, Owner, true, Ct);
        Assert.AreEqual(0, client.Calls);
        Assert.IsEmpty(report.Checkpoint.Requests);
        Assert.AreEqual("BudgetExhausted", report.Status);
        Assert.IsNotEmpty(report.Trials);
        foreach (var trial in report.Trials)
        {
            Assert.IsFalse(trial.VerifiedCompletion);
        }
    }

    [TestMethod]
    public async Task DisabledConfigurationOrMissingHostGrantHasNoExecutionEffects()
    {
        await using var scope = await Scope.OpenAsync();
        var reject = new FixtureClient { Reject = true };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scope.Runner(reject).StartAsync(new(), "disabled", scope.Root, Owner, true, Ct));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => scope.Runner(reject).StartAsync(Settings(), "grant", scope.Root, Owner, false, Ct));
        Assert.IsFalse(Directory.Exists(Path.Combine(scope.Root, "source")));
        Assert.IsNull(await ((ILiveEvaluationStore)scope.Store).GetEvaluationAsync("disabled", Owner, Ct));
        Assert.AreEqual(0, reject.Calls);
    }

    private static LiveEvaluationSettings Settings() => new()
    {
        Enabled = true,
        Provider = "openai",
        Model = "controlled-fixture-only",
        MaxTotalSpend = 10,
        Pricing = new()
        {
            InputPerMillion = 1,
            OutputPerMillion = 1,
            CacheReadPerMillion = 1,
            CacheWritePerMillion = 1,
            Source = "Synthetic test rates; not provider pricing",
            AsOfUtc = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero)
        },
        MaxInputTokensPerRequest = 50_000,
        MaxOutputTokensPerRequest = 1000,
        InputFramingTokenAllowance = 1024,
        PairedTrials = 1
    };

    private sealed class FixtureClient : ILlmClient
    {
        public int Calls;
        public bool Reject, Interrupt;
        private readonly ConcurrentDictionary<SessionKey, int> turns = new();
        private readonly TaskCompletionSource branches = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int branchCount;
        public bool ParallelBranchesObserved => branches.Task.IsCompletedSuccessfully;
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Reject) throw new InvalidOperationException("Provider must not be called.");
            if (Interrupt) { yield return new AssistantTextDelta("An incomplete response"); yield break; }
            var objective = request.Messages.First(m => m.Role == ChatRole.User).Content;
            var role = objective.Split('\n')[0]["Role: ".Length..];
            var caseId = objective.Contains("integer subtraction") ? "subtraction" : objective.Contains("integer multiplication") ? "multiplication" : "addition";
            var method = caseId == "addition" ? "Add" : caseId == "subtraction" ? "Subtract" : "Multiply";
            var operation = caseId == "addition" ? "+" : caseId == "subtraction" ? "-" : "*";
            var turn = turns.AddOrUpdate(request.SessionKey, 1, (_, value) => value + 1);
            if (turn == 1 && role is "code" or "docs")
            {
                if (Interlocked.Increment(ref branchCount) >= 2) branches.TrySetResult();
                await branches.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            }
            if (turn == 1 && role is "code" or "direct")
                yield return new ToolCallDelta("write-code", "write_file", JsonSerializer.SerializeToElement(new { path = "Calc.cs", content = $"public static class Calc {{ public static int {method}(int left, int right) => checked(left {operation} right); }}" }));
            if (turn == 1 && role is "docs" or "direct")
                yield return new ToolCallDelta("write-docs", "write_file", JsonSerializer.SerializeToElement(new { path = "README.md", content = $"Calc.{method} uses checked integer {caseId}." }));
            yield return new AssistantTextDelta("Work completed; the host must verify it.");
            yield return new UsageDelta(10, 4, 0, 0);
            yield return new CompletedDelta();
        }
    }

    private sealed class Scope : IAsyncDisposable
    {
        private static readonly string Base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IronPawLiveEvaluationTests"));
        public string Root { get; } = Path.Combine(Base, Guid.NewGuid().ToString("n"));
        public CSharpDbWorkflowStore Store { get; private set; } = null!;
        public static async Task<Scope> OpenAsync()
        {
            var scope = new Scope(); Directory.CreateDirectory(scope.Root);
            scope.Store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(scope.Root, "evaluation.cdb"), Ct);
            return scope;
        }
        public LiveEvaluationRunner Runner(ILlmClient client) => new((ILiveEvaluationStore)Store, Store, Store, client);
        public async Task ReopenAsync() { await Store.DisposeAsync(); Store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(Root, "evaluation.cdb"), Ct); }
        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            var target = Path.GetFullPath(Root);
            if (!target.StartsWith(Base + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Cleanup escaped the test root.");
            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(target, recursive: true);
        }
    }
}
