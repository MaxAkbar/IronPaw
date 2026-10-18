using System.Collections.Immutable;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

public sealed class TaskGraphContractTests
{
    [Fact]
    public void GraphJsonRoundTrip_PreservesPolicyResourcesBudgetsAndAcceptanceRequirements()
    {
        var graph = new WorkflowTaskGraph("feature-42", "Implement and verify the feature",
        [
            new WorkflowTaskDefinition("implement", "Produce the implementation")
            {
                AgentId = "coding-agent",
                ToolPolicy = new ToolSelectionPolicy(ToolProfile.Coding, SenderIsOwner: true,
                    AllowList: ["read_file", "apply_patch", "run_command"], DenyList: ["send_message"],
                    AllowedGroups: ["group:files", "group:system"], SubagentDepth: 2,
                    InheritedPolicy: new ToolSelectionPolicy(ToolProfile.Minimal,
                        AllowList: ["read_file", "apply_patch"], DenyList: ["delete_file"],
                        AllowedGroups: ["group:files"], SubagentDepth: 1)),
                Resources = new TaskResourceScope
                {
                    ReadPaths = ["src", "tests"],
                    WritePaths = ["src/Feature.cs", "tests/FeatureTests.cs"],
                    NetworkHosts = ["packages.example.test"],
                    AllowProcessExecution = true,
                    AllowCredentialAccess = false,
                    AllowExternalMutations = true
                },
                Budget = new WorkflowBudget(5, 12, 4096, TimeSpan.FromMinutes(3)),
                ExpectedArtifacts =
                [
                    new("implementation", "Reviewable code change", TaskArtifactKind.Patch, "feature.patch"),
                    new("notes", "Optional implementation notes", TaskArtifactKind.Report, Required: false)
                ],
                AcceptanceCriteria =
                [
                    new TaskAcceptanceCriterion("tests-pass", "Feature tests pass")
                    {
                        ArtifactIds = ["implementation"]
                    },
                    new("notes-reviewed", "Optional notes are reviewed", Required: false)
                ]
            },
            new WorkflowTaskDefinition("verify", "Verify the produced revision")
            {
                Dependencies = ["implement"]
            }
        ]);

        var restored = RoundTrip(graph);

        Assert.Equal(graph.GraphId, restored.GraphId);
        Assert.Equal(graph.Objective, restored.Objective);
        Assert.Equal(1, restored.SchemaVersion);
        Assert.Equal(new[] { "implement", "verify" }, restored.Tasks.Select(task => task.TaskId));
        var task = restored.Tasks[0];
        Assert.Equal("Produce the implementation", task.Objective);
        Assert.Equal("coding-agent", task.AgentId);
        Assert.Equal(ToolProfile.Coding, task.ToolPolicy.Profile);
        Assert.True(task.ToolPolicy.SenderIsOwner);
        Assert.Equal(new[] { "read_file", "apply_patch", "run_command" }, task.ToolPolicy.AllowList);
        Assert.Equal(new[] { "send_message" }, task.ToolPolicy.DenyList);
        Assert.Equal(new[] { "group:files", "group:system" }, task.ToolPolicy.AllowedGroups);
        Assert.Equal(2, task.ToolPolicy.SubagentDepth);
        var inherited = Assert.IsType<ToolSelectionPolicy>(task.ToolPolicy.InheritedPolicy);
        Assert.Equal(ToolProfile.Minimal, inherited.Profile);
        Assert.False(inherited.SenderIsOwner);
        Assert.Equal(new[] { "read_file", "apply_patch" }, inherited.AllowList);
        Assert.Equal(new[] { "delete_file" }, inherited.DenyList);
        Assert.Equal(new[] { "group:files" }, inherited.AllowedGroups);
        Assert.Equal(1, inherited.SubagentDepth);
        Assert.Null(inherited.InheritedPolicy);
        Assert.Equal(new[] { "src", "tests" }, task.Resources.ReadPaths);
        Assert.Equal(new[] { "src/Feature.cs", "tests/FeatureTests.cs" }, task.Resources.WritePaths);
        Assert.Equal(new[] { "packages.example.test" }, task.Resources.NetworkHosts);
        Assert.True(task.Resources.AllowProcessExecution);
        Assert.False(task.Resources.AllowCredentialAccess);
        Assert.True(task.Resources.AllowExternalMutations);
        Assert.Equal(graph.Tasks[0].Budget, task.Budget);
        Assert.Equal(graph.Tasks[0].ExpectedArtifacts.ToArray(), task.ExpectedArtifacts.ToArray());
        Assert.Equal(new[] { "tests-pass", "notes-reviewed" }, task.AcceptanceCriteria.Select(item => item.CriterionId));
        Assert.Equal("Feature tests pass", task.AcceptanceCriteria[0].Description);
        Assert.True(task.AcceptanceCriteria[0].Required);
        Assert.Equal(new[] { "implementation" }, task.AcceptanceCriteria[0].ArtifactIds);
        Assert.False(task.AcceptanceCriteria[1].Required);
        Assert.Empty(task.AcceptanceCriteria[1].ArtifactIds);
        Assert.Equal(new[] { "implement" }, restored.Tasks[1].Dependencies);
        Assert.Empty(task.Dependencies);
    }

    [Fact]
    public void ResultJsonRoundTrip_PreservesExactRevisionsEvidenceAndUnresolvedIssues()
    {
        var result = new WorkflowTaskResult("feature-42", "sha256:accepted-graph-content", "implement", WorkflowTaskStatus.Failed,
            "revision-abc123", "Tests failed for the reported revision")
        {
            Artifacts =
            [
                new("implementation", "implement", TaskArtifactKind.Patch,
                    "artifacts/feature.patch", "sha256:patch-content", "revision-abc123"),
                new("test-report", "implement", TaskArtifactKind.Report,
                    "artifacts/test-results.json", "sha256:report-content", "revision-abc123")
            ],
            VerificationEvidence =
            [
                new TaskVerificationEvidence("test-run-42", "tests-pass", "revision-abc123",
                    TaskVerificationOutcome.Failed, "One feature test failed")
                {
                    ArtifactIds = ["implementation", "test-report"],
                    Command = "dotnet test --no-restore",
                    ExitCode = 1
                },
                new("review-43", "reviewed", "revision-abc123",
                    TaskVerificationOutcome.Inconclusive, "Review requires additional context")
            ],
            UnresolvedIssues = ["FeatureTests.RejectsInvalidInput failed", "Review is pending"]
        };

        var restored = RoundTrip(result);

        Assert.Equal(result.GraphId, restored.GraphId);
        Assert.Equal(result.GraphFingerprint, restored.GraphFingerprint);
        Assert.Equal(result.TaskId, restored.TaskId);
        Assert.Equal(result.Status, restored.Status);
        Assert.Equal(result.Revision, restored.Revision);
        Assert.Equal(result.Explanation, restored.Explanation);
        Assert.Equal(result.Artifacts.ToArray(), restored.Artifacts.ToArray());
        var evidence = restored.VerificationEvidence[0];
        Assert.Equal("test-run-42", evidence.EvidenceId);
        Assert.Equal("tests-pass", evidence.CriterionId);
        Assert.Equal("revision-abc123", evidence.Revision);
        Assert.Equal(TaskVerificationOutcome.Failed, evidence.Outcome);
        Assert.Equal("One feature test failed", evidence.Summary);
        Assert.Equal(new[] { "implementation", "test-report" }, evidence.ArtifactIds);
        Assert.Equal("dotnet test --no-restore", evidence.Command);
        Assert.Equal(1, evidence.ExitCode);
        var inconclusive = restored.VerificationEvidence[1];
        Assert.Equal("review-43", inconclusive.EvidenceId);
        Assert.Equal(TaskVerificationOutcome.Inconclusive, inconclusive.Outcome);
        Assert.Empty(inconclusive.ArtifactIds);
        Assert.Null(inconclusive.Command);
        Assert.Null(inconclusive.ExitCode);
        Assert.Equal(result.UnresolvedIssues.ToArray(), restored.UnresolvedIssues.ToArray());
    }

    [Fact]
    public void MinimalTaskJson_UsesEmptyCollectionsAndRestrictiveDefaults()
    {
        var task = JsonSerializer.Deserialize<WorkflowTaskDefinition>(
            """{"TaskId":"task-1","Objective":"Inspect the code"}""")!;

        Assert.False(task.Dependencies.IsDefault);
        Assert.Empty(task.Dependencies);
        Assert.False(task.ExpectedArtifacts.IsDefault);
        Assert.Empty(task.ExpectedArtifacts);
        Assert.False(task.AcceptanceCriteria.IsDefault);
        Assert.Empty(task.AcceptanceCriteria);
        Assert.Null(task.AgentId);
        Assert.Null(task.Budget);
        Assert.Equal(ToolProfile.Minimal, task.ToolPolicy.Profile);
        Assert.False(task.ToolPolicy.SenderIsOwner);
        Assert.Null(task.ToolPolicy.AllowList);
        Assert.Null(task.ToolPolicy.DenyList);
        Assert.Null(task.ToolPolicy.AllowedGroups);
        Assert.Null(task.ToolPolicy.InheritedPolicy);
        Assert.False(task.Resources.ReadPaths.IsDefault);
        Assert.Empty(task.Resources.ReadPaths);
        Assert.False(task.Resources.WritePaths.IsDefault);
        Assert.Empty(task.Resources.WritePaths);
        Assert.False(task.Resources.NetworkHosts.IsDefault);
        Assert.Empty(task.Resources.NetworkHosts);
        Assert.False(task.Resources.AllowProcessExecution);
        Assert.False(task.Resources.AllowCredentialAccess);
        Assert.False(task.Resources.AllowExternalMutations);

        var restored = RoundTrip(task);
        Assert.Empty(restored.Dependencies);
        Assert.Empty(restored.ExpectedArtifacts);
        Assert.Empty(restored.AcceptanceCriteria);
        Assert.Null(restored.AgentId);
        Assert.Null(restored.Budget);
        Assert.Null(restored.ToolPolicy.AllowList);
    }

    [Fact]
    public void MinimalResultJson_UsesEmptyArtifactsEvidenceAndIssues()
    {
        var result = JsonSerializer.Deserialize<WorkflowTaskResult>(
            """{"GraphId":"graph-1","GraphFingerprint":"sha256:accepted-graph-content","TaskId":"task-1","Status":0,"Revision":"revision-1","Explanation":"Waiting for a dependency"}""")!;

        Assert.Equal("graph-1", result.GraphId);
        Assert.Equal("sha256:accepted-graph-content", result.GraphFingerprint);
        Assert.Equal(WorkflowTaskStatus.Blocked, result.Status);
        Assert.False(result.Artifacts.IsDefault);
        Assert.Empty(result.Artifacts);
        Assert.False(result.VerificationEvidence.IsDefault);
        Assert.Empty(result.VerificationEvidence);
        Assert.False(result.UnresolvedIssues.IsDefault);
        Assert.Empty(result.UnresolvedIssues);

        var restored = RoundTrip(result);
        Assert.Equal(result.GraphId, restored.GraphId);
        Assert.Equal(result.GraphFingerprint, restored.GraphFingerprint);
        Assert.Empty(restored.Artifacts);
        Assert.Empty(restored.VerificationEvidence);
        Assert.Empty(restored.UnresolvedIssues);
    }

    [Fact]
    public void OptionalNullsRoundTrip_WithoutBecomingEmptyOrImplicitPermissions()
    {
        var task = new WorkflowTaskDefinition("task-1", "Read the code")
        {
            Budget = new WorkflowBudget(),
            ExpectedArtifacts = [new("notes", "Notes", TaskArtifactKind.Report)],
            AcceptanceCriteria = [new("review", "Review notes")]
        };
        var evidence = new TaskVerificationEvidence("evidence-1", "review", "revision-1",
            TaskVerificationOutcome.Inconclusive, "No command has run");

        var restoredTask = RoundTrip(task);
        var restoredEvidence = RoundTrip(evidence);

        Assert.Null(restoredTask.AgentId);
        Assert.Null(restoredTask.ToolPolicy.AllowList);
        Assert.Null(restoredTask.ToolPolicy.DenyList);
        Assert.Null(restoredTask.ToolPolicy.AllowedGroups);
        Assert.Null(restoredTask.ToolPolicy.InheritedPolicy);
        Assert.NotNull(restoredTask.Budget);
        Assert.Null(restoredTask.Budget.MaxTotalTokens);
        Assert.Null(restoredTask.Budget.MaxExecutionTime);
        Assert.Null(Assert.Single(restoredTask.ExpectedArtifacts).RelativePath);
        Assert.True(Assert.Single(restoredTask.ExpectedArtifacts).Required);
        Assert.True(Assert.Single(restoredTask.AcceptanceCriteria).Required);
        Assert.Null(restoredEvidence.Command);
        Assert.Null(restoredEvidence.ExitCode);
    }

    [Fact]
    public void ImmutableCollectionSnapshots_DoNotFollowMutationsOfSourceLists()
    {
        var dependencies = new List<string> { "prepare" };
        var readPaths = new List<string> { "src" };
        var artifacts = new List<TaskArtifactRequirement>
        {
            new("report", "Inspection report", TaskArtifactKind.Report)
        };
        var artifactIds = new List<string> { "report" };
        var criteria = new List<TaskAcceptanceCriterion>
        {
            new("review", "Report reviewed") { ArtifactIds = artifactIds.ToImmutableArray() }
        };
        var task = new WorkflowTaskDefinition("inspect", "Inspect source")
        {
            Dependencies = dependencies.ToImmutableArray(),
            Resources = new TaskResourceScope { ReadPaths = readPaths.ToImmutableArray() },
            ExpectedArtifacts = artifacts.ToImmutableArray(),
            AcceptanceCriteria = criteria.ToImmutableArray()
        };
        var tasks = new List<WorkflowTaskDefinition> { task };
        var graph = new WorkflowTaskGraph("graph-1", "Inspect the feature", tasks.ToImmutableArray());
        var issues = new List<string> { "Review pending" };
        var result = new WorkflowTaskResult("graph-1", "sha256:accepted-graph-content", "inspect", WorkflowTaskStatus.Blocked, "revision-1", "Waiting")
        {
            UnresolvedIssues = issues.ToImmutableArray()
        };

        dependencies[0] = "different-dependency";
        readPaths.Add("secrets");
        artifacts.Clear();
        artifactIds[0] = "different-artifact";
        criteria.Clear();
        tasks.Clear();
        issues.Clear();

        Assert.Single(graph.Tasks);
        var saved = graph.Tasks[0];
        Assert.Equal(new[] { "prepare" }, saved.Dependencies);
        Assert.Equal(new[] { "src" }, saved.Resources.ReadPaths);
        Assert.Equal("report", Assert.Single(saved.ExpectedArtifacts).ArtifactId);
        Assert.Equal(new[] { "report" }, Assert.Single(saved.AcceptanceCriteria).ArtifactIds);
        Assert.Equal(new[] { "Review pending" }, result.UnresolvedIssues);
    }

    [Fact]
    public void ValidatedGraph_RecursivelySnapshotsMutablePolicyLists()
    {
        var allowed = new List<string> { "read_file" };
        var denied = new List<string> { "delete_file" };
        var groups = new List<string> { "group:files" };
        var inheritedAllowed = new List<string> { "read_file", "apply_patch" };
        var inheritedDenied = new List<string> { "send_message" };
        var inheritedGroups = new List<string> { "group:files", "group:system" };
        var proposed = new WorkflowTaskGraph("graph-1", "Review source",
        [
            new WorkflowTaskDefinition("review", "Review the code")
            {
                ToolPolicy = new ToolSelectionPolicy(ToolProfile.Minimal,
                    AllowList: allowed, DenyList: denied, AllowedGroups: groups,
                    InheritedPolicy: new ToolSelectionPolicy(ToolProfile.Coding,
                        AllowList: inheritedAllowed, DenyList: inheritedDenied,
                        AllowedGroups: inheritedGroups))
            }
        ]);

        var validation = TaskGraphValidator.Validate(proposed);

        Assert.True(validation.IsValid);
        var accepted = Assert.IsType<WorkflowTaskGraph>(validation.Graph);
        allowed.Add("run_command");
        denied.Clear();
        groups[0] = "group:messaging";
        inheritedAllowed.Clear();
        inheritedDenied.Clear();
        inheritedGroups.Add("group:messaging");

        var policy = Assert.Single(accepted.Tasks).ToolPolicy;
        Assert.Equal(new[] { "read_file" }, policy.AllowList);
        Assert.Equal(new[] { "delete_file" }, policy.DenyList);
        Assert.Equal(new[] { "group:files" }, policy.AllowedGroups);
        var inherited = Assert.IsType<ToolSelectionPolicy>(policy.InheritedPolicy);
        Assert.Equal(new[] { "read_file", "apply_patch" }, inherited.AllowList);
        Assert.Equal(new[] { "send_message" }, inherited.DenyList);
        Assert.Equal(new[] { "group:files", "group:system" }, inherited.AllowedGroups);
        AssertCannotMutate(policy.AllowList!);
        AssertCannotMutate(policy.DenyList!);
        AssertCannotMutate(policy.AllowedGroups!);
        AssertCannotMutate(inherited.AllowList!);
        AssertCannotMutate(inherited.DenyList!);
        AssertCannotMutate(inherited.AllowedGroups!);
    }

    private static void AssertCannotMutate(IReadOnlyCollection<string> collection)
    {
        if (collection is ICollection<string> mutableInterface)
        {
            Assert.True(mutableInterface.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => mutableInterface.Add("injected-tool"));
        }
    }

    private static T RoundTrip<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
