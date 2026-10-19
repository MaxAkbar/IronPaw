using System.Collections.Immutable;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class TaskGraphValidatorTests
{
    [TestMethod]
    public void DiamondWithForwardReferences_ProducesStableDependencyOrder()
    {
        var graph = Graph(Task("deliver", "review", "test"), Task("review", "build"),
            Task("test", "build"), Task("build"), Task("independent"));

        var result = TaskGraphValidator.Validate(graph);

        Assert.IsTrue(result.IsValid);
        Assert.IsEmpty(result.Errors);
        Assert.AreSequenceEqual(["build", "review", "test", "deliver", "independent"], result.TopologicalTaskIds.ToArray());
        Assert.AreNotSame<object>(graph, result.Graph);
    }

    [TestMethod]
    public void DisconnectedTasksAndCaseDistinctIds_AreValidAndKeepDeclarationOrder()
    {
        var result = TaskGraphValidator.Validate(Graph(Task("task"), Task("Task"), Task("other")));

        Assert.IsTrue(result.IsValid);
        Assert.AreSequenceEqual(["task", "Task", "other"], result.TopologicalTaskIds.ToArray());
    }

    [TestMethod]
    public void SnapshotDefensivelyCopiesCollectionsThroughoutInheritedPolicy()
    {
        var allow = new List<string> { "read_file" };
        var deny = new List<string> { "write_file" };
        var groups = new List<string> { "group:fs" };
        var parent = new ToolSelectionPolicy(ToolProfile.Coding, AllowList: allow, DenyList: deny,
            AllowedGroups: groups);
        var childAllow = new List<string> { "read_file" };
        var child = new ToolSelectionPolicy(ToolProfile.Minimal, AllowList: childAllow, InheritedPolicy: parent);
        var task = Task("inspect") with { ToolPolicy = child };

        var result = TaskGraphValidator.Validate(Graph(task));
        Assert.IsTrue(result.IsValid);
        var snapshot = Assert.ContainsSingle(result.Graph!.Tasks).ToolPolicy;
        allow.Clear();
        deny.Add("read_file");
        groups.Clear();
        childAllow.Add("write_file");

        Assert.AreNotSame<object>(child, snapshot);
        Assert.AreNotSame<object>(parent, snapshot.InheritedPolicy);
        Assert.AreSequenceEqual(["read_file"], snapshot.AllowList);
        Assert.AreSequenceEqual(["read_file"], snapshot.InheritedPolicy!.AllowList);
        Assert.AreSequenceEqual(["write_file"], snapshot.InheritedPolicy.DenyList);
        Assert.AreSequenceEqual(["group:fs"], snapshot.InheritedPolicy.AllowedGroups);
    }

    [TestMethod]
    public void SnapshotValidatesTheFrozenEnumeration_WithoutReadingCallerCollectionAgain()
    {
        var changing = new ChangingNames();
        var result = TaskGraphValidator.Validate(Graph(Task("a") with
        {
            ToolPolicy = new(ToolProfile.Minimal, AllowList: changing)
        }));

        Assert.IsTrue(result.IsValid);
        Assert.AreSequenceEqual(["read_file"], result.Graph!.Tasks[0].ToolPolicy.AllowList);
        Assert.AreEqual(1, changing.Enumerations);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PolicyCollectionSnapshotFailure_ReturnsStructuredValidationError(bool argumentException)
    {
        Invalid(Graph(Task("a") with
        {
            ToolPolicy = new(ToolProfile.Minimal, AllowList: new FailingNames(argumentException))
        }), "InvalidToolPolicy");
    }

    [TestMethod]
    public void RichGraph_SurvivesJsonRoundTripAndRetainsRestrictions()
    {
        var original = Graph(Task("change") with
        {
            AgentId = "coder",
            Budget = new(4, 0, 2000, TimeSpan.FromMinutes(1)),
            ToolPolicy = new(ToolProfile.Coding, DenyList: ["write_file"],
                InheritedPolicy: new(ToolProfile.Minimal)),
            Resources = new() { ReadPaths = ["src"], NetworkHosts = ["example.test"] },
            ExpectedArtifacts = [new("patch", "Proposed patch", TaskArtifactKind.Patch, "changes/fix.patch")],
            AcceptanceCriteria = [new("tests", "Relevant tests pass") { ArtifactIds = ["patch"] }]
        });

        var restored = JsonSerializer.Deserialize<WorkflowTaskGraph>(JsonSerializer.Serialize(original));
        var result = TaskGraphValidator.Validate(restored);

        Assert.IsTrue(result.IsValid);
        var task = Assert.ContainsSingle(result.Graph!.Tasks);
        Assert.AreEqual(original.Tasks[0].Budget, task.Budget);
        Assert.AreEqual("coder", task.AgentId);
        Assert.AreSequenceEqual(["src"], task.Resources.ReadPaths.ToArray());
        Assert.IsFalse(task.Resources.AllowExternalMutations);
        Assert.AreEqual("changes/fix.patch", Assert.ContainsSingle(task.ExpectedArtifacts).RelativePath);
        Assert.AreSequenceEqual(["patch"], Assert.ContainsSingle(task.AcceptanceCriteria).ArtifactIds.ToArray());
        Assert.AreEqual(ToolProfile.Minimal, task.ToolPolicy.InheritedPolicy!.Profile);
    }

    [TestMethod]
    public void DeepGraph_IsValidatedWithoutRecursiveTraversal()
    {
        const int count = 12_000;
        var tasks = Enumerable.Range(0, count).Select(index => index == 0
            ? Task($"t{index}") : Task($"t{index}", $"t{index - 1}")).ToImmutableArray();

        var result = TaskGraphValidator.Validate(new("deep", "Deep chain", tasks));

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(count, result.TopologicalTaskIds.Length);
        Assert.AreEqual("t11999", result.TopologicalTaskIds[^1]);
    }

    [TestMethod]
    public void CyclesInDisconnectedComponent_InvalidateEntireExecutionPlan()
    {
        var graph = Graph(Task("independent"), Task("a", "b"), Task("b", "a"), Task("blocked", "b"));

        var result = Invalid(graph, "DependencyCycle");

        Assert.AreSequenceEqual(["a", "b", "blocked"], result.Errors.Where(error => error.Code == "DependencyCycle").Select(error => error.TaskId));
        Assert.DoesNotContain(error => error.TaskId == "independent", result.Errors);
    }

    [TestMethod]
    public void DependencyErrorsRetainTaskAndRelatedIdentity()
    {
        var result = Invalid(Graph(Task("a", "a", "absent", "absent", " padded")), "SelfDependency");

        Assert.Contains(error => error.Code == "UnknownDependency" && error.TaskId == "a" && error.RelatedTaskId == "absent", result.Errors);
        Assert.Contains(error => error.Code == "DuplicateDependency" && error.RelatedTaskId == "absent", result.Errors);
        Assert.Contains(error => error.Code == "InvalidDependencyId" && error.RelatedTaskId == " padded", result.Errors);
    }

    [TestMethod]
    [DataRow(0, 0, null, null)]
    [DataRow(1, -1, null, null)]
    [DataRow(1, 0, 0L, null)]
    [DataRow(1, 0, -1L, null)]
    [DataRow(1, 0, null, 0)]
    [DataRow(1, 0, null, -1)]
    public void InvalidBudgets_AreRejected(int models, int tools, long? tokens, int? seconds)
    {
        Invalid(Graph(Task("a") with
        {
            Budget = new(models, tools, tokens,
            seconds is { } value ? TimeSpan.FromSeconds(value) : null)
        }), "InvalidBudget");
    }

    [TestMethod]
    public void ZeroToolBudgetAndAbsentAcceptanceCriteria_AreStructurallyValid()
    {
        Assert.IsTrue(TaskGraphValidator.Validate(Graph(Task("a") with { Budget = new(1, 0) })).IsValid);
    }

    [TestMethod]
    [DataRow("/absolute")]
    [DataRow("\\rooted")]
    [DataRow("C:\\absolute")]
    [DataRow("C:relative")]
    [DataRow("../escape")]
    [DataRow("safe/../escape")]
    [DataRow("safe\\..\\escape")]
    [DataRow("safe//file")]
    [DataRow("./file")]
    [DataRow("safe/.")]
    [DataRow("file\nname")]
    public void ArtifactPathSyntax_IsCheckedIndependentlyOfHostOs(string path)
    {
        Invalid(Graph(Task("a") with
        {
            ExpectedArtifacts = [new("output", "Output", TaskArtifactKind.File, path)]
        }), "InvalidArtifact");
    }

    [TestMethod]
    public void ArtifactIdsAreTaskScoped_AndCriteriaMustReferenceOwnOutputs()
    {
        var artifact = new TaskArtifactRequirement("output", "Output", TaskArtifactKind.Report);
        var first = Task("a") with { ExpectedArtifacts = [artifact] };
        var second = Task("b") with
        {
            ExpectedArtifacts = [artifact],
            AcceptanceCriteria = [new("review", "Review output") { ArtifactIds = ["output"] }]
        };
        Assert.IsTrue(TaskGraphValidator.Validate(Graph(first, second)).IsValid);
        Invalid(Graph(first, second with
        {
            ExpectedArtifacts = [],
            AcceptanceCriteria = [new("review", "Review output") { ArtifactIds = ["output"] }]
        }), "UnknownArtifactReference");
    }

    [TestMethod]
    public void DuplicateArtifactsCriteriaAndCriterionReferences_AreRejected()
    {
        var artifact = new TaskArtifactRequirement("out", "Output", TaskArtifactKind.File);
        var criterion = new TaskAcceptanceCriterion("check", "Review output") { ArtifactIds = ["out", "out"] };
        var result = Invalid(Graph(Task("a") with
        {
            ExpectedArtifacts = [artifact, artifact],
            AcceptanceCriteria = [criterion, criterion]
        }), "DuplicateArtifactId");
        Assert.Contains(error => error.Code == "DuplicateCriterionId", result.Errors);
        Assert.Contains(error => error.Code == "DuplicateArtifactReference", result.Errors);
    }

    [TestMethod]
    public void MalformedGraphAndTaskMetadata_ProduceErrorsWithoutExceptions()
    {
        Invalid(null, "GraphRequired");
        Invalid(new("graph", "Goal", default), "TasksRequired");
        Invalid(new("graph", "Goal", []), "TasksRequired");
        Invalid(new(" graph", " ", [Task(" a")], 2), "UnsupportedSchemaVersion");
        Invalid(Graph(Task("a"), Task("a")), "DuplicateTaskId");
        Invalid(Graph(Task("a") with { Objective = " ", AgentId = "agent " }), "InvalidObjective");
        Invalid(Graph((WorkflowTaskDefinition)null!), "TaskRequired");
    }

    [TestMethod]
    public void DefaultArraysAndNullNestedRecords_ProduceErrorsWithoutExceptions()
    {
        Invalid(Graph(Task("a") with { Dependencies = default }), "InvalidCollection");
        Invalid(Graph(Task("a") with { ExpectedArtifacts = default }), "InvalidCollection");
        Invalid(Graph(Task("a") with { AcceptanceCriteria = default }), "InvalidCollection");
        Invalid(Graph(Task("a") with { Resources = null! }), "InvalidResource");
        Invalid(Graph(Task("a") with { ToolPolicy = null! }), "InvalidToolPolicy");
        Invalid(Graph(Task("a") with { Resources = new() { ReadPaths = default } }), "InvalidCollection");
        Invalid(Graph(Task("a") with { Resources = new() { WritePaths = default } }), "InvalidCollection");
        Invalid(Graph(Task("a") with { Resources = new() { NetworkHosts = default } }), "InvalidCollection");
        Invalid(Graph(Task("a") with { ExpectedArtifacts = [null!] }), "InvalidArtifact");
        Invalid(Graph(Task("a") with { AcceptanceCriteria = [null!] }), "InvalidCriterion");
        Invalid(Graph(Task("a") with { AcceptanceCriteria = [new("c", "Check") { ArtifactIds = default }] }), "InvalidCollection");
        Invalid(Graph(Task("a") with { ToolPolicy = new(ToolProfile.Minimal, AllowList: default(ImmutableArray<string>)) }), "InvalidCollection");
    }

    [TestMethod]
    public void MalformedResourcesAndUnknownEnums_AreRejected()
    {
        var result = Invalid(Graph(Task("a") with
        {
            ToolPolicy = new((ToolProfile)999, SubagentDepth: -1, AllowList: ["read_file", "READ_FILE"]),
            Resources = new() { ReadPaths = ["src", "src"], WritePaths = [" "], NetworkHosts = [null!] },
            ExpectedArtifacts = [new("artifact", "Output", (TaskArtifactKind)999)]
        }), "InvalidToolPolicy");
        Assert.Contains(error => error.Code == "DuplicateResource", result.Errors);
        Assert.Contains(error => error.Code == "InvalidResource", result.Errors);
        Assert.Contains(error => error.Code == "InvalidArtifact", result.Errors);
    }

    [TestMethod]
    public void CyclicAndExcessivelyDeepInheritedPolicy_AreRejectedWithoutRecursion()
    {
        var cyclic = new ToolSelectionPolicy(ToolProfile.Minimal);
        typeof(ToolSelectionPolicy).GetProperty(nameof(ToolSelectionPolicy.InheritedPolicy))!.SetValue(cyclic, cyclic);
        Invalid(Graph(Task("a") with { ToolPolicy = cyclic }), "PolicyCycle");
        var deep = new ToolSelectionPolicy(ToolProfile.Minimal);
        for (var index = 0; index < 65; index++) deep = new(ToolProfile.Minimal, InheritedPolicy: deep);
        Invalid(Graph(Task("a") with { ToolPolicy = deep }), "PolicyDepthExceeded");
    }

    [TestMethod]
    public void RepeatedValidation_ProducesDeterministicDiagnostics()
    {
        var graph = Graph(Task("a", "missing", "missing"), Task("b", "b"));
        Assert.AreSequenceEqual(TaskGraphValidator.Validate(graph).Errors.ToArray(), TaskGraphValidator.Validate(graph).Errors.ToArray());
    }

    [TestMethod]
    public void DefinitionFingerprint_IsStableAcrossValidationAndJsonRoundTrip()
    {
        var graph = Graph(Task("a") with
        {
            ToolPolicy = new(ToolProfile.Minimal, AllowList: ["read_file"],
                InheritedPolicy: new(ToolProfile.Coding)),
            Resources = new() { ReadPaths = ["src"] },
            ExpectedArtifacts = [new("report", "Findings", TaskArtifactKind.Report)],
            AcceptanceCriteria = [new("check", "Review findings") { ArtifactIds = ["report"] }]
        }, Task("b", "a"));

        var first = TaskGraphValidator.Validate(graph);
        var second = TaskGraphValidator.Validate(graph);
        var restored = JsonSerializer.Deserialize<WorkflowTaskGraph>(JsonSerializer.Serialize(first.Graph));
        var roundTrip = TaskGraphValidator.Validate(restored);

        Assert.IsTrue(first.IsValid);
        Assert.IsTrue(roundTrip.IsValid);
        Assert.MatchesRegex("^sha256:[a-f0-9]{64}$", first.DefinitionFingerprint!);
        Assert.AreEqual(first.DefinitionFingerprint, second.DefinitionFingerprint);
        Assert.AreEqual(first.DefinitionFingerprint, roundTrip.DefinitionFingerprint);
    }

    [TestMethod]
    [DataRow("graphId")]
    [DataRow("graphObjective")]
    [DataRow("taskObjective")]
    [DataRow("budget")]
    [DataRow("dependencies")]
    [DataRow("policy")]
    [DataRow("resources")]
    [DataRow("artifact")]
    [DataRow("criterion")]
    [DataRow("agent")]
    public void DefinitionFingerprint_ChangesWhenDeclaredMetadataChanges(string field)
    {
        var firstTask = Task("a") with
        {
            ExpectedArtifacts = [new("out", "Report", TaskArtifactKind.Report)],
            AcceptanceCriteria = [new("check", "Review output") { ArtifactIds = ["out"] }]
        };
        var graph = Graph(firstTask, Task("b", "a"));
        var changed = field switch
        {
            "graphId" => graph with { GraphId = "other" },
            "graphObjective" => graph with { Objective = "Changed goal" },
            "taskObjective" => graph with { Tasks = graph.Tasks.SetItem(0, firstTask with { Objective = "Changed task" }) },
            "budget" => graph with { Tasks = graph.Tasks.SetItem(0, firstTask with { Budget = new(1, 0) }) },
            "dependencies" => graph with { Tasks = graph.Tasks.SetItem(1, Task("b")) },
            "policy" => graph with { Tasks = graph.Tasks.SetItem(0, firstTask with { ToolPolicy = new(ToolProfile.Coding) }) },
            "resources" => graph with { Tasks = graph.Tasks.SetItem(0, firstTask with { Resources = new() { ReadPaths = ["src"] } }) },
            "artifact" => graph with { Tasks = graph.Tasks.SetItem(0, firstTask with { ExpectedArtifacts = [new("out", "Changed report", TaskArtifactKind.Report)] }) },
            "criterion" => graph with { Tasks = graph.Tasks.SetItem(0, firstTask with { AcceptanceCriteria = [new("check", "Changed acceptance") { ArtifactIds = ["out"] }] }) },
            "agent" => graph with { Tasks = graph.Tasks.SetItem(0, firstTask with { AgentId = "reviewer" }) },
            _ => throw new ArgumentException("Unknown field.", nameof(field))
        };

        var originalResult = TaskGraphValidator.Validate(graph);
        var changedResult = TaskGraphValidator.Validate(changed);
        Assert.IsTrue(originalResult.IsValid);
        Assert.IsTrue(changedResult.IsValid);
        Assert.AreNotEqual(originalResult.DefinitionFingerprint, changedResult.DefinitionFingerprint);
    }

    [TestMethod]
    public void MaximumAcceptedPolicyDepth_HasAFingerprint()
    {
        var policy = new ToolSelectionPolicy(ToolProfile.Minimal);
        for (var index = 1; index < 64; index++) policy = new(ToolProfile.Minimal, InheritedPolicy: policy);

        var result = TaskGraphValidator.Validate(Graph(Task("a") with { ToolPolicy = policy }));

        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(result.DefinitionFingerprint);
    }

    [TestMethod]
    [DataRow("graph", "InvalidGraphId")]
    [DataRow("task", "InvalidTaskId")]
    [DataRow("dependency", "InvalidDependencyId")]
    [DataRow("artifact", "InvalidArtifact")]
    [DataRow("criterion", "InvalidCriterion")]
    [DataRow("resource", "InvalidResource")]
    [DataRow("policy", "InvalidToolPolicy")]
    public void EmbeddedControlCharacters_AreRejectedInIdentitiesAndDeclarations(string field, string code)
    {
        const string invalid = "before\u0000after";
        var graph = field switch
        {
            "graph" => Graph(Task("a")) with { GraphId = invalid },
            "task" => Graph(Task(invalid)),
            "dependency" => Graph(Task("a", invalid)),
            "artifact" => Graph(Task("a") with { ExpectedArtifacts = [new(invalid, "Output", TaskArtifactKind.File)] }),
            "criterion" => Graph(Task("a") with { AcceptanceCriteria = [new(invalid, "Check output")] }),
            "resource" => Graph(Task("a") with { Resources = new() { ReadPaths = [invalid] } }),
            "policy" => Graph(Task("a") with { ToolPolicy = new(ToolProfile.Minimal, AllowList: [invalid]) }),
            _ => throw new ArgumentException("Unknown test field.", nameof(field))
        };
        Invalid(graph, code);
    }

    private static WorkflowTaskDefinition Task(string taskId, params string[] dependencies) =>
        new(taskId, $"Complete {taskId}") { Dependencies = dependencies.ToImmutableArray() };

    private static WorkflowTaskGraph Graph(params WorkflowTaskDefinition[] tasks) =>
        new("graph", "Complete the work", tasks.ToImmutableArray());

    private static TaskGraphValidationResult Invalid(WorkflowTaskGraph? graph, string expectedCode)
    {
        var result = TaskGraphValidator.Validate(graph);
        Assert.IsFalse(result.IsValid);
        Assert.IsNull(result.Graph);
        Assert.IsNull(result.DefinitionFingerprint);
        Assert.IsEmpty(result.TopologicalTaskIds);
        Assert.Contains(error => error.Code == expectedCode, result.Errors);
        return result;
    }

    private sealed class ChangingNames : IReadOnlyCollection<string>
    {
        public int Count => 1;
        public int Enumerations { get; private set; }
        public IEnumerator<string> GetEnumerator() =>
            ((IEnumerable<string>)(++Enumerations == 1 ? new[] { "read_file" } : new[] { " " })).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class FailingNames(bool argumentException) : IReadOnlyCollection<string>
    {
        public int Count => 1;
        public IEnumerator<string> GetEnumerator() => argumentException
            ? throw new ArgumentException("Collection changed during copying.")
            : throw new InvalidOperationException("Collection changed during enumeration.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
