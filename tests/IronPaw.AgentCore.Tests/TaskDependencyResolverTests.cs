using System.Collections.Immutable;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class TaskDependencyResolverTests
{
    private static readonly CallerIdentity Caller = new("owner", "Owner", true);

    [TestMethod]
    public void DiamondInputsContainEachAncestorOnceInStableTopologyOrder()
    {
        var graph = Graph(Task("join", "right", "left"), Task("left", "root"), Task("right", "root"), Task("root"));
        graph = Accept(Accept(Accept(graph, "root"), "left"), "right");
        var inputs = TaskDependencyResolver.CreateInputs(graph, "join", "join-attempt");
        Assert.AreSequenceEqual(new[] { "root", "left", "right" }, inputs.Dependencies.Select(pin => pin.TaskId));
        var claim = new TaskGraphClaim(graph.Graph.GraphId, graph.DefinitionFingerprint, graph.Graph.Tasks[0], "join-attempt", "lease", Inputs: inputs);
        Assert.AreSequenceEqual(inputs.Dependencies.Select(pin => pin.TaskId), TaskDependencyResolver.Resolve(graph, claim).Select(input => input.Task.TaskId));
        Assert.AreEqual(TaskInputIdentity.Digest(inputs), inputs.Digest);
    }

    [TestMethod]
    public void AcceptedReceiptMustBindExactProducerResult()
    {
        var graph = Accept(Graph(Task("a"), Task("b", "a")), "a");
        graph = graph with { Tasks = graph.Tasks.SetItem(0, graph.Tasks[0] with { Result = graph.Tasks[0].Result! with { Explanation = "changed" } }) };
        Assert.ThrowsExactly<InvalidDataException>(() => TaskDependencyResolver.CreateInputs(graph, "b", "attempt"));
    }

    [TestMethod]
    public void UnacceptedProducerCannotSupplyInputs()
    {
        var graph = Graph(Task("a"), Task("b", "a"));
        Assert.ThrowsExactly<InvalidDataException>(() => TaskDependencyResolver.CreateInputs(graph, "b", "attempt"));
    }

    [TestMethod]
    public void ResolveRejectsProducerWithoutItsOwnFrozenBaseline()
    {
        var graph = Accept(Graph(Task("a"), Task("b", "a")), "a");
        var inputs = TaskDependencyResolver.CreateInputs(graph, "b", "attempt");
        graph = graph with { Tasks = graph.Tasks.SetItem(0, graph.Tasks[0] with { Inputs = null }) };
        var claim = new TaskGraphClaim(graph.Graph.GraphId, graph.DefinitionFingerprint, graph.Graph.Tasks[1], "attempt", "lease", Inputs: inputs);
        Assert.ThrowsExactly<InvalidDataException>(() => TaskDependencyResolver.Resolve(graph, claim));
    }

    [TestMethod]
    public void ResolveRejectsChangedClaimInputDigestOrDefinition()
    {
        var graph = Accept(Graph(Task("a"), Task("b", "a")), "a");
        var inputs = TaskDependencyResolver.CreateInputs(graph, "b", "attempt");
        var claim = new TaskGraphClaim(graph.Graph.GraphId, graph.DefinitionFingerprint, graph.Graph.Tasks[1], "attempt", "lease", Inputs: inputs);
        Assert.ThrowsExactly<InvalidDataException>(() => TaskDependencyResolver.Resolve(graph, claim with { Inputs = inputs with { Digest = "changed" } }));
        Assert.ThrowsExactly<InvalidDataException>(() => TaskDependencyResolver.Resolve(graph, claim with { Task = claim.Task with { Objective = "changed" } }));
    }

    [TestMethod]
    public void AttemptAndExactAcceptedReceiptArePartOfInputDigest()
    {
        var graph = Accept(Graph(Task("a"), Task("b", "a")), "a");
        var original = TaskDependencyResolver.CreateInputs(graph, "b", "attempt");
        Assert.AreNotEqual(original.Digest, TaskDependencyResolver.CreateInputs(graph, "b", "different-attempt").Digest);
        graph = graph with { Tasks = graph.Tasks.SetItem(0, graph.Tasks[0] with { Acceptance = graph.Tasks[0].Acceptance! with { Explanation = "Another accepted explanation" } }) };
        Assert.AreNotEqual(original.Digest, TaskDependencyResolver.CreateInputs(graph, "b", "attempt").Digest);
    }

    [TestMethod]
    public void ChildRequestRequiresFrozenInputsAndRetainsObjectiveFirst()
    {
        var graph = Accept(Graph(Task("a"), Task("b", "a")), "a");
        var binding = graph.AgentBinding!;
        var inputs = TaskDependencyResolver.CreateInputs(graph, "b", "attempt");
        Assert.ThrowsExactly<InvalidDataException>(() => AgentTaskBindingValidator.CreateChildRequest(graph.Graph, Caller, binding, graph.Graph.Tasks[1], "attempt"));
        var request = AgentTaskBindingValidator.CreateChildRequest(graph.Graph, Caller, binding, graph.Graph.Tasks[1], "attempt", inputs);
        Assert.StartsWith(graph.Graph.Tasks[1].Objective + "\n", request.UserInput);
        Assert.Contains(inputs.Digest, request.UserInput);
        Assert.Contains(inputs.Dependencies[0].ResultDigest, request.UserInput);
        Assert.ThrowsExactly<InvalidDataException>(() => AgentTaskBindingValidator.CreateChildRequest(graph.Graph, Caller, binding, graph.Graph.Tasks[1], "attempt", inputs with { BaseCommit = new string('b', 40) }));
    }

    [TestMethod]
    public void LegacyAndUndefinedModesCannotAcquireDependencyInputs()
    {
        var graph = Graph(Task("a"));
        Assert.ThrowsExactly<ArgumentException>(() => AgentTaskBindingValidator.Validate(graph.Graph, Caller, graph.AgentBinding! with { DependencyMode = (TaskDependencyMode)100 }));
        graph = graph with { AgentBinding = graph.AgentBinding! with { DependencyMode = TaskDependencyMode.BaseCommitOnly }, SchemaVersion = 2 };
        Assert.ThrowsExactly<InvalidDataException>(() => TaskDependencyResolver.CreateInputs(graph, "a", "attempt"));
        Assert.AreEqual(graph.Graph.Tasks[0].Objective, AgentTaskBindingValidator.CreateChildRequest(graph.Graph, Caller, graph.AgentBinding!, graph.Graph.Tasks[0], "attempt").UserInput);
    }

    private static TaskGraphCheckpoint Graph(params WorkflowTaskDefinition[] tasks)
    {
        var graph = new WorkflowTaskGraph("graph", "Deliver objective", tasks.ToImmutableArray());
        var validation = TaskGraphValidator.Validate(graph);
        var binding = new TaskGraphAgentBinding(AgentTaskIdentity.RootRunId(graph.GraphId).Value,
            Path.Combine(Path.GetTempPath(), "dependency-source"), new string('a', 40), Path.Combine(Path.GetTempPath(), "dependency-managed"),
            "test", "model", new(ToolProfile.Minimal), new(20, 20))
        { DependencyMode = TaskDependencyMode.AcceptedChanges };
        return new(graph, validation.DefinitionFingerprint!, Caller, 4, tasks.Select(task => new TaskGraphTaskState(task.TaskId)).ToImmutableArray(),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SchemaVersion: 3, AgentBinding: binding);
    }
    private static WorkflowTaskDefinition Task(string id, params string[] dependencies) => new(id, "Perform " + id)
    { Dependencies = dependencies.ToImmutableArray(), AcceptanceCriteria = [new("criterion", "Check output")] };
    private static TaskGraphCheckpoint Accept(TaskGraphCheckpoint graph, string taskId)
    {
        var index = Array.FindIndex(graph.Tasks.ToArray(), state => state.TaskId == taskId);
        var attempt = taskId + "-attempt";
        var inputs = TaskDependencyResolver.CreateInputs(graph, taskId, attempt);
        var result = new WorkflowTaskResult(graph.Graph.GraphId, graph.DefinitionFingerprint, taskId,
            WorkflowTaskStatus.AwaitingVerification, taskId + "-revision", "Candidate completed");
        var receipt = new TaskAcceptanceReceipt(TaskAcceptanceOutcome.Accepted, "host", "Accepted exact artifact revision", graph.DefinitionFingerprint,
            taskId, attempt, TaskDependencyResolver.ResultDigest(result), result.Revision, DateTimeOffset.UtcNow);
        return graph with { Tasks = graph.Tasks.SetItem(index, new(taskId, TaskGraphTaskStatus.Succeeded, attempt, Result: result, Acceptance: receipt, Inputs: inputs)) };
    }
}
