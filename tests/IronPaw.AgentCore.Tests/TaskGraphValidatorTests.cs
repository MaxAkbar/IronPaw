using System.Collections.Immutable;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

public sealed class TaskGraphValidatorTests
{
    [Fact]
    public void DiamondWithForwardReferences_ProducesStableDependencyOrder()
    {
        var graph = Graph(Task("deliver", "review", "test"), Task("review", "build"),
            Task("test", "build"), Task("build"), Task("independent"));

        var result = TaskGraphValidator.Validate(graph);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(["build", "review", "test", "deliver", "independent"], result.TopologicalTaskIds.ToArray());
        Assert.NotSame(graph, result.Graph);
    }

    [Fact]
    public void DisconnectedTasksAndCaseDistinctIds_AreValidAndKeepDeclarationOrder()
    {
        var result = TaskGraphValidator.Validate(Graph(Task("task"), Task("Task"), Task("other")));

        Assert.True(result.IsValid);
        Assert.Equal(["task", "Task", "other"], result.TopologicalTaskIds.ToArray());
    }

    [Fact]
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
        Assert.True(result.IsValid);
        var snapshot = Assert.Single(result.Graph!.Tasks).ToolPolicy;
        allow.Clear();
        deny.Add("read_file");
        groups.Clear();
        childAllow.Add("write_file");

        Assert.NotSame(child, snapshot);
        Assert.NotSame(parent, snapshot.InheritedPolicy);
        Assert.Equal(["read_file"], snapshot.AllowList);
        Assert.Equal(["read_file"], snapshot.InheritedPolicy!.AllowList);
        Assert.Equal(["write_file"], snapshot.InheritedPolicy.DenyList);
        Assert.Equal(["group:fs"], snapshot.InheritedPolicy.AllowedGroups);
    }

    [Fact]
    public void SnapshotValidatesTheFrozenEnumeration_WithoutReadingCallerCollectionAgain()
    {
        var changing = new ChangingNames();
        var result = TaskGraphValidator.Validate(Graph(Task("a") with
        {
            ToolPolicy = new(ToolProfile.Minimal, AllowList: changing)
        }));

        Assert.True(result.IsValid);
        Assert.Equal(["read_file"], result.Graph!.Tasks[0].ToolPolicy.AllowList);
        Assert.Equal(1, changing.Enumerations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PolicyCollectionSnapshotFailure_ReturnsStructuredValidationError(bool argumentException)
    {
        Invalid(Graph(Task("a") with
        {
            ToolPolicy = new(ToolProfile.Minimal, AllowList: new FailingNames(argumentException))
        }), "InvalidToolPolicy");
    }

    [Fact]
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

        Assert.True(result.IsValid);
        var task = Assert.Single(result.Graph!.Tasks);
        Assert.Equal(original.Tasks[0].Budget, task.Budget);
        Assert.Equal("coder", task.AgentId);
        Assert.Equal(["src"], task.Resources.ReadPaths.ToArray());
        Assert.False(task.Resources.AllowExternalMutations);
        Assert.Equal("changes/fix.patch", Assert.Single(task.ExpectedArtifacts).RelativePath);
        Assert.Equal(["patch"], Assert.Single(task.AcceptanceCriteria).ArtifactIds.ToArray());
        Assert.Equal(ToolProfile.Minimal, task.ToolPolicy.InheritedPolicy!.Profile);
    }

    [Fact]
    public void DeepGraph_IsValidatedWithoutRecursiveTraversal()
    {
        const int count = 12_000;
        var tasks = Enumerable.Range(0, count).Select(index => index == 0
            ? Task($"t{index}") : Task($"t{index}", $"t{index - 1}")).ToImmutableArray();

        var result = TaskGraphValidator.Validate(new("deep", "Deep chain", tasks));

        Assert.True(result.IsValid);
        Assert.Equal(count, result.TopologicalTaskIds.Length);
        Assert.Equal("t11999", result.TopologicalTaskIds[^1]);
    }

    [Fact]
    public void CyclesInDisconnectedComponent_InvalidateEntireExecutionPlan()
    {
        var graph = Graph(Task("independent"), Task("a", "b"), Task("b", "a"), Task("blocked", "b"));

        var result = Invalid(graph, "DependencyCycle");

        Assert.Equal(["a", "b", "blocked"], result.Errors.Where(error => error.Code == "DependencyCycle").Select(error => error.TaskId));
        Assert.DoesNotContain(result.Errors, error => error.TaskId == "independent");
    }

    [Fact]
    public void DependencyErrorsRetainTaskAndRelatedIdentity()
    {
        var result = Invalid(Graph(Task("a", "a", "absent", "absent", " padded")), "SelfDependency");

        Assert.Contains(result.Errors, error => error.Code == "UnknownDependency" && error.TaskId == "a" && error.RelatedTaskId == "absent");
        Assert.Contains(result.Errors, error => error.Code == "DuplicateDependency" && error.RelatedTaskId == "absent");
        Assert.Contains(result.Errors, error => error.Code == "InvalidDependencyId" && error.RelatedTaskId == " padded");
    }

    [Theory]
    [InlineData(0, 0, null, null)]
    [InlineData(1, -1, null, null)]
    [InlineData(1, 0, 0L, null)]
    [InlineData(1, 0, -1L, null)]
    [InlineData(1, 0, null, 0)]
    [InlineData(1, 0, null, -1)]
    public void InvalidBudgets_AreRejected(int models, int tools, long? tokens, int? seconds)
    {
        Invalid(Graph(Task("a") with { Budget = new(models, tools, tokens,
            seconds is { } value ? TimeSpan.FromSeconds(value) : null) }), "InvalidBudget");
    }

    [Fact]
    public void ZeroToolBudgetAndAbsentAcceptanceCriteria_AreStructurallyValid()
    {
        Assert.True(TaskGraphValidator.Validate(Graph(Task("a") with { Budget = new(1, 0) })).IsValid);
    }

    [Theory]
    [InlineData("/absolute")]
    [InlineData("\\rooted")]
    [InlineData("C:\\absolute")]
    [InlineData("C:relative")]
    [InlineData("../escape")]
    [InlineData("safe/../escape")]
    [InlineData("safe\\..\\escape")]
    [InlineData("safe//file")]
    [InlineData("./file")]
    [InlineData("safe/.")]
    [InlineData("file\nname")]
    public void ArtifactPathSyntax_IsCheckedIndependentlyOfHostOs(string path)
    {
        Invalid(Graph(Task("a") with
        {
            ExpectedArtifacts = [new("output", "Output", TaskArtifactKind.File, path)]
        }), "InvalidArtifact");
    }

    [Fact]
    public void ArtifactIdsAreTaskScoped_AndCriteriaMustReferenceOwnOutputs()
    {
        var artifact = new TaskArtifactRequirement("output", "Output", TaskArtifactKind.Report);
        var first = Task("a") with { ExpectedArtifacts = [artifact] };
        var second = Task("b") with
        {
            ExpectedArtifacts = [artifact],
            AcceptanceCriteria = [new("review", "Review output") { ArtifactIds = ["output"] }]
        };
        Assert.True(TaskGraphValidator.Validate(Graph(first, second)).IsValid);
        Invalid(Graph(first, second with
        {
            ExpectedArtifacts = [],
            AcceptanceCriteria = [new("review", "Review output") { ArtifactIds = ["output"] }]
        }), "UnknownArtifactReference");
    }

    [Fact]
    public void DuplicateArtifactsCriteriaAndCriterionReferences_AreRejected()
    {
        var artifact = new TaskArtifactRequirement("out", "Output", TaskArtifactKind.File);
        var criterion = new TaskAcceptanceCriterion("check", "Review output") { ArtifactIds = ["out", "out"] };
        var result = Invalid(Graph(Task("a") with
        {
            ExpectedArtifacts = [artifact, artifact], AcceptanceCriteria = [criterion, criterion]
        }), "DuplicateArtifactId");
        Assert.Contains(result.Errors, error => error.Code == "DuplicateCriterionId");
        Assert.Contains(result.Errors, error => error.Code == "DuplicateArtifactReference");
    }

    [Fact]
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

    [Fact]
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

    [Fact]
    public void MalformedResourcesAndUnknownEnums_AreRejected()
    {
        var result = Invalid(Graph(Task("a") with
        {
            ToolPolicy = new((ToolProfile)999, SubagentDepth: -1, AllowList: ["read_file", "READ_FILE"]),
            Resources = new() { ReadPaths = ["src", "src"], WritePaths = [" "], NetworkHosts = [null!] },
            ExpectedArtifacts = [new("artifact", "Output", (TaskArtifactKind)999)]
        }), "InvalidToolPolicy");
        Assert.Contains(result.Errors, error => error.Code == "DuplicateResource");
        Assert.Contains(result.Errors, error => error.Code == "InvalidResource");
        Assert.Contains(result.Errors, error => error.Code == "InvalidArtifact");
    }

    [Fact]
    public void CyclicAndExcessivelyDeepInheritedPolicy_AreRejectedWithoutRecursion()
    {
        var cyclic = new ToolSelectionPolicy(ToolProfile.Minimal);
        typeof(ToolSelectionPolicy).GetProperty(nameof(ToolSelectionPolicy.InheritedPolicy))!.SetValue(cyclic, cyclic);
        Invalid(Graph(Task("a") with { ToolPolicy = cyclic }), "PolicyCycle");
        var deep = new ToolSelectionPolicy(ToolProfile.Minimal);
        for (var index = 0; index < 65; index++) deep = new(ToolProfile.Minimal, InheritedPolicy: deep);
        Invalid(Graph(Task("a") with { ToolPolicy = deep }), "PolicyDepthExceeded");
    }

    [Fact]
    public void RepeatedValidation_ProducesDeterministicDiagnostics()
    {
        var graph = Graph(Task("a", "missing", "missing"), Task("b", "b"));
        Assert.Equal(TaskGraphValidator.Validate(graph).Errors.ToArray(), TaskGraphValidator.Validate(graph).Errors.ToArray());
    }

    [Fact]
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

        Assert.True(first.IsValid);
        Assert.True(roundTrip.IsValid);
        Assert.Matches("^sha256:[a-f0-9]{64}$", first.DefinitionFingerprint!);
        Assert.Equal(first.DefinitionFingerprint, second.DefinitionFingerprint);
        Assert.Equal(first.DefinitionFingerprint, roundTrip.DefinitionFingerprint);
    }

    [Theory]
    [InlineData("graphId")]
    [InlineData("graphObjective")]
    [InlineData("taskObjective")]
    [InlineData("budget")]
    [InlineData("dependencies")]
    [InlineData("policy")]
    [InlineData("resources")]
    [InlineData("artifact")]
    [InlineData("criterion")]
    [InlineData("agent")]
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
        Assert.True(originalResult.IsValid);
        Assert.True(changedResult.IsValid);
        Assert.NotEqual(originalResult.DefinitionFingerprint, changedResult.DefinitionFingerprint);
    }

    [Fact]
    public void MaximumAcceptedPolicyDepth_HasAFingerprint()
    {
        var policy = new ToolSelectionPolicy(ToolProfile.Minimal);
        for (var index = 1; index < 64; index++) policy = new(ToolProfile.Minimal, InheritedPolicy: policy);

        var result = TaskGraphValidator.Validate(Graph(Task("a") with { ToolPolicy = policy }));

        Assert.True(result.IsValid);
        Assert.NotNull(result.DefinitionFingerprint);
    }

    [Theory]
    [InlineData("graph", "InvalidGraphId")]
    [InlineData("task", "InvalidTaskId")]
    [InlineData("dependency", "InvalidDependencyId")]
    [InlineData("artifact", "InvalidArtifact")]
    [InlineData("criterion", "InvalidCriterion")]
    [InlineData("resource", "InvalidResource")]
    [InlineData("policy", "InvalidToolPolicy")]
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
        Assert.False(result.IsValid);
        Assert.Null(result.Graph);
        Assert.Null(result.DefinitionFingerprint);
        Assert.Empty(result.TopologicalTaskIds);
        Assert.Contains(result.Errors, error => error.Code == expectedCode);
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
