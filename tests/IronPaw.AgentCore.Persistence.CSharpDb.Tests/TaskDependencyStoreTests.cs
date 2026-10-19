using System.Collections.Immutable;
using System.Text.Json;
using CSharpDB.Engine;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;

namespace IronPaw.AgentCore.Persistence.CSharpDb.Tests;

[TestClass]
public sealed class TaskDependencyStoreTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "IronPaw-DependencyStore-" + Guid.NewGuid().ToString("n"));
    private string DatabasePath => Path.Combine(_directory, "workflows.cdb");
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly CallerIdentity Caller = new("owner", "Original owner", true);

    [TestMethod]
    public async Task ClaimAtomicallyFreezesTransitiveAcceptedInputsAndFencesTampering()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph(Task("a"), Task("b", "a"), Task("c", "a", "b"));
        var accepted = await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, Binding(graph), Ct);
        Assert.AreEqual(3, accepted.SchemaVersion);
        var a = (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!;
        Assert.IsEmpty(a.Inputs!.Dependencies);
        await AcceptAsync(store, a);
        var b = (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!;
        Assert.AreEqual("a", Assert.ContainsSingle(b.Inputs!.Dependencies).TaskId);
        await AcceptAsync(store, b);
        var c = (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!;
        Assert.AreSequenceEqual(new[] { "a", "b" }, c.Inputs!.Dependencies.Select(pin => pin.TaskId));
        var saved = (await store.GetTaskGraphAsync(graph.GraphId, Ct))!;
        Assert.IsTrue(TaskDependencyResolver.SameInputs(c.Inputs, saved.Tasks[2].Inputs));
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.RecordTaskResultAsync(c with { Inputs = c.Inputs with { Digest = "changed" } }, Proposal(c), Ct));
        Assert.AreEqual(saved.Revision, (await store.GetTaskGraphAsync(graph.GraphId, Ct))!.Revision);
    }

    [TestMethod]
    public async Task RecoveryAndVerificationReuseExactPinnedInputs()
    {
        var graph = Graph(Task("a"), Task("b", "a"));
        TaskAttemptInputs? pinned;
        string attempt;
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, Binding(graph), Ct);
            await AcceptAsync(store, (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!);
            var claim = (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!;
            pinned = claim.Inputs;
            attempt = claim.AttemptId;
        }
        await using var reopened = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var recovered = await reopened.ReclaimAgentTaskAsync(graph.GraphId, "b", Caller, Ct);
        Assert.AreEqual(attempt, recovered.AttemptId);
        Assert.IsTrue(TaskDependencyResolver.SameInputs(pinned, recovered.Inputs));
        await reopened.RecordTaskResultAsync(recovered, Proposal(recovered), Ct);
        await reopened.AbandonTaskClaimAsync(recovered, "Stopped before verification", Ct);
        var verification = (await reopened.ClaimTaskAsync(graph.GraphId, Caller, Ct))!;
        Assert.IsTrue(TaskDependencyResolver.SameInputs(pinned, verification.Inputs));
        Assert.IsNotNull(verification.ProposedResult);
        await reopened.RecordTaskAcceptanceAsync(verification, new(TaskAcceptanceOutcome.Accepted, "host", "Exact receipt accepted"), Ct);
        Assert.AreEqual(TaskGraphTaskStatus.Succeeded, (await reopened.GetTaskGraphAsync(graph.GraphId, Ct))!.Tasks[1].Status);
    }

    [TestMethod]
    public async Task ChildAdmissionBindsFrozenInputMetadata()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph(Task("a"));
        var accepted = await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, Binding(graph), Ct);
        var claim = (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!;
        var request = AgentTaskBindingValidator.CreateChildRequest(graph, Caller, accepted.AgentBinding!, claim.Task, claim.AttemptId, claim.Inputs);
        var child = new WorkflowCheckpoint(request.RequestedRunId!.Value.Value, accepted.AgentBinding!.RootRunId, accepted.AgentBinding.RootRunId,
            "a", request, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Budget: request.Budget, Usage: new());
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.CreateAsync(child with { Request = request with { UserInput = claim.Task.Objective } }, Ct));
        await store.CreateAsync(child, Ct);
        Assert.Contains(claim.Inputs!.Digest, (await store.GetAsync(request.RequestedRunId.Value, Ct))!.Request.UserInput);
    }

    [TestMethod]
    public async Task AcceptedBindingCannotSwitchDependencyMode()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph(Task("a"));
        var binding = Binding(graph);
        await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, binding, Ct);
        await Assert.ThrowsExactlyAsync<WorkflowConflictException>(() => store.AcceptAgentTaskGraphAsync(graph, Caller, 1,
            binding with { DependencyMode = TaskDependencyMode.BaseCommitOnly }, Ct));
    }

    [TestMethod]
    public async Task LegacyAndGenericGraphsRetainBaseOnlyBehaviorWithoutRetroseedingInputs()
    {
        await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath);
        var graph = Graph(Task("a"), Task("b", "a"));
        var accepted = await store.AcceptAgentTaskGraphAsync(graph, Caller, 1, Binding(graph) with { DependencyMode = TaskDependencyMode.BaseCommitOnly }, Ct);
        Assert.AreEqual(2, accepted.SchemaVersion);
        var a = (await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!;
        Assert.IsNull(a.Inputs);
        await AcceptAsync(store, a);
        Assert.IsNull((await store.ClaimTaskAsync(graph.GraphId, Caller, Ct))!.Inputs);
        var generic = Graph(Task("x")) with { GraphId = "generic" };
        Assert.AreEqual(1, (await store.AcceptTaskGraphAsync(generic, Caller, 1, Ct)).SchemaVersion);
        Assert.IsNull((await store.ClaimTaskAsync(generic.GraphId, Caller, Ct))!.Inputs);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("digest")]
    [DataRow("base")]
    public async Task ReopeningRejectsPersistedInputDrift(string defect)
    {
        TaskGraphCheckpoint graph;
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            var definition = Graph(Task("a"));
            await store.AcceptAgentTaskGraphAsync(definition, Caller, 1, Binding(definition), Ct);
            await store.ClaimTaskAsync(definition.GraphId, Caller, Ct);
            graph = (await store.GetTaskGraphAsync(definition.GraphId, Ct))!;
        }
        var inputs = graph.Tasks[0].Inputs!;
        graph = graph with
        {
            Tasks = graph.Tasks.SetItem(0, graph.Tasks[0] with
            {
                Inputs = defect switch
                {
                    "missing" => null,
                    "base" => inputs with { BaseCommit = new string('b', 40) },
                    _ => inputs with { Digest = "changed" }
                }
            })
        };
        var payload = JsonSerializer.Serialize(graph, new JsonSerializerOptions { MaxDepth = 256 });
        await using (var database = await Database.OpenAsync(DatabasePath))
        {
            await using var command = await database.ExecuteAsync("UPDATE task_graphs SET payload = '" + payload.Replace("'", "''") + "' WHERE graph_id = 'graph'");
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath); });
    }

    [TestMethod]
    public async Task ReopeningRejectsChangedAncestorAcceptanceAfterConsumerClaimedItsInputs()
    {
        TaskGraphCheckpoint graph;
        await using (var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath))
        {
            var definition = Graph(Task("a"), Task("b", "a"));
            await store.AcceptAgentTaskGraphAsync(definition, Caller, 1, Binding(definition), Ct);
            await AcceptAsync(store, (await store.ClaimTaskAsync(definition.GraphId, Caller, Ct))!);
            await store.ClaimTaskAsync(definition.GraphId, Caller, Ct);
            graph = (await store.GetTaskGraphAsync(definition.GraphId, Ct))!;
        }
        // The modified receipt still identifies its producer result. It differs from the
        // exact accepted receipt already consumed by b, so merely validating a is insufficient.
        graph = graph with
        {
            Tasks = graph.Tasks.SetItem(0, graph.Tasks[0] with
            { Acceptance = graph.Tasks[0].Acceptance! with { Explanation = "Changed after consumer admission" } })
        };
        var payload = JsonSerializer.Serialize(graph, new JsonSerializerOptions { MaxDepth = 256 });
        await using (var database = await Database.OpenAsync(DatabasePath))
        {
            await using var command = await database.ExecuteAsync("UPDATE task_graphs SET payload = '" + payload.Replace("'", "''") + "' WHERE graph_id = 'graph'");
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var store = await CSharpDbWorkflowStore.OpenAsync(DatabasePath); });
    }

    private TaskGraphAgentBinding Binding(WorkflowTaskGraph graph) => new(AgentTaskIdentity.RootRunId(graph.GraphId).Value,
        Path.Combine(_directory, "source"), new string('a', 40), Path.Combine(_directory, "managed"), "test", "test-model", new(ToolProfile.Minimal), new(20, 20))
    { DependencyMode = TaskDependencyMode.AcceptedChanges };
    private static WorkflowTaskGraph Graph(params WorkflowTaskDefinition[] tasks) => new("graph", "Deliver objective", tasks.ToImmutableArray());
    private static WorkflowTaskDefinition Task(string id, params string[] dependencies) => new(id, "Execute " + id)
    { Dependencies = dependencies.ToImmutableArray(), AcceptanceCriteria = [new("criterion", "Check exact output")] };
    private static WorkflowTaskResult Proposal(TaskGraphClaim claim) => new(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId,
        WorkflowTaskStatus.AwaitingVerification, claim.Task.TaskId + "-revision", "Candidate completed");
    private static async Task AcceptAsync(CSharpDbWorkflowStore store, TaskGraphClaim claim)
    {
        await store.RecordTaskResultAsync(claim, Proposal(claim), Ct);
        await store.RecordTaskAcceptanceAsync(claim, new(TaskAcceptanceOutcome.Accepted, "host", "Exact receipt accepted"), Ct);
    }
    [TestCleanup]
    public void Cleanup()
    {
        if (!Path.GetFullPath(_directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test directory is outside the temporary root.");
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
