using System.Collections.Immutable;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class TaskGraphContractTests
{
    [TestMethod]
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

        Assert.AreEqual(graph.GraphId, restored.GraphId);
        Assert.AreEqual(graph.Objective, restored.Objective);
        Assert.AreEqual(1, restored.SchemaVersion);
        Assert.AreSequenceEqual(new[] { "implement", "verify" }, restored.Tasks.Select(task => task.TaskId));
        var task = restored.Tasks[0];
        Assert.AreEqual("Produce the implementation", task.Objective);
        Assert.AreEqual("coding-agent", task.AgentId);
        Assert.AreEqual(ToolProfile.Coding, task.ToolPolicy.Profile);
        Assert.IsTrue(task.ToolPolicy.SenderIsOwner);
        Assert.AreSequenceEqual(new[] { "read_file", "apply_patch", "run_command" }, task.ToolPolicy.AllowList);
        Assert.AreSequenceEqual(new[] { "send_message" }, task.ToolPolicy.DenyList);
        Assert.AreSequenceEqual(new[] { "group:files", "group:system" }, task.ToolPolicy.AllowedGroups);
        Assert.AreEqual(2, task.ToolPolicy.SubagentDepth);
        var inherited = Assert.IsExactInstanceOfType<ToolSelectionPolicy>(task.ToolPolicy.InheritedPolicy);
        Assert.AreEqual(ToolProfile.Minimal, inherited.Profile);
        Assert.IsFalse(inherited.SenderIsOwner);
        Assert.AreSequenceEqual(new[] { "read_file", "apply_patch" }, inherited.AllowList);
        Assert.AreSequenceEqual(new[] { "delete_file" }, inherited.DenyList);
        Assert.AreSequenceEqual(new[] { "group:files" }, inherited.AllowedGroups);
        Assert.AreEqual(1, inherited.SubagentDepth);
        Assert.IsNull(inherited.InheritedPolicy);
        Assert.AreSequenceEqual(new[] { "src", "tests" }, task.Resources.ReadPaths);
        Assert.AreSequenceEqual(new[] { "src/Feature.cs", "tests/FeatureTests.cs" }, task.Resources.WritePaths);
        Assert.AreSequenceEqual(new[] { "packages.example.test" }, task.Resources.NetworkHosts);
        Assert.IsTrue(task.Resources.AllowProcessExecution);
        Assert.IsFalse(task.Resources.AllowCredentialAccess);
        Assert.IsTrue(task.Resources.AllowExternalMutations);
        Assert.AreEqual(graph.Tasks[0].Budget, task.Budget);
        Assert.AreSequenceEqual(graph.Tasks[0].ExpectedArtifacts.ToArray(), task.ExpectedArtifacts.ToArray());
        Assert.AreSequenceEqual(new[] { "tests-pass", "notes-reviewed" }, task.AcceptanceCriteria.Select(item => item.CriterionId));
        Assert.AreEqual("Feature tests pass", task.AcceptanceCriteria[0].Description);
        Assert.IsTrue(task.AcceptanceCriteria[0].Required);
        Assert.AreSequenceEqual(new[] { "implementation" }, task.AcceptanceCriteria[0].ArtifactIds);
        Assert.IsFalse(task.AcceptanceCriteria[1].Required);
        Assert.IsEmpty(task.AcceptanceCriteria[1].ArtifactIds);
        Assert.AreSequenceEqual(new[] { "implement" }, restored.Tasks[1].Dependencies);
        Assert.IsEmpty(task.Dependencies);
    }

    [TestMethod]
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

        Assert.AreEqual(result.GraphId, restored.GraphId);
        Assert.AreEqual(result.GraphFingerprint, restored.GraphFingerprint);
        Assert.AreEqual(result.TaskId, restored.TaskId);
        Assert.AreEqual(result.Status, restored.Status);
        Assert.AreEqual(result.Revision, restored.Revision);
        Assert.AreEqual(result.Explanation, restored.Explanation);
        Assert.AreSequenceEqual(result.Artifacts.ToArray(), restored.Artifacts.ToArray());
        var evidence = restored.VerificationEvidence[0];
        Assert.AreEqual("test-run-42", evidence.EvidenceId);
        Assert.AreEqual("tests-pass", evidence.CriterionId);
        Assert.AreEqual("revision-abc123", evidence.Revision);
        Assert.AreEqual(TaskVerificationOutcome.Failed, evidence.Outcome);
        Assert.AreEqual("One feature test failed", evidence.Summary);
        Assert.AreSequenceEqual(new[] { "implementation", "test-report" }, evidence.ArtifactIds);
        Assert.AreEqual("dotnet test --no-restore", evidence.Command);
        Assert.AreEqual(1, evidence.ExitCode);
        var inconclusive = restored.VerificationEvidence[1];
        Assert.AreEqual("review-43", inconclusive.EvidenceId);
        Assert.AreEqual(TaskVerificationOutcome.Inconclusive, inconclusive.Outcome);
        Assert.IsEmpty(inconclusive.ArtifactIds);
        Assert.IsNull(inconclusive.Command);
        Assert.IsNull(inconclusive.ExitCode);
        Assert.AreSequenceEqual(result.UnresolvedIssues.ToArray(), restored.UnresolvedIssues.ToArray());
    }

    [TestMethod]
    public void MinimalTaskJson_UsesEmptyCollectionsAndRestrictiveDefaults()
    {
        var task = JsonSerializer.Deserialize<WorkflowTaskDefinition>(
            """{"TaskId":"task-1","Objective":"Inspect the code"}""")!;

        Assert.IsFalse(task.Dependencies.IsDefault);
        Assert.IsEmpty(task.Dependencies);
        Assert.IsFalse(task.ExpectedArtifacts.IsDefault);
        Assert.IsEmpty(task.ExpectedArtifacts);
        Assert.IsFalse(task.AcceptanceCriteria.IsDefault);
        Assert.IsEmpty(task.AcceptanceCriteria);
        Assert.IsNull(task.AgentId);
        Assert.IsNull(task.Budget);
        Assert.AreEqual(ToolProfile.Minimal, task.ToolPolicy.Profile);
        Assert.IsFalse(task.ToolPolicy.SenderIsOwner);
        Assert.IsNull(task.ToolPolicy.AllowList);
        Assert.IsNull(task.ToolPolicy.DenyList);
        Assert.IsNull(task.ToolPolicy.AllowedGroups);
        Assert.IsNull(task.ToolPolicy.InheritedPolicy);
        Assert.IsFalse(task.Resources.ReadPaths.IsDefault);
        Assert.IsEmpty(task.Resources.ReadPaths);
        Assert.IsFalse(task.Resources.WritePaths.IsDefault);
        Assert.IsEmpty(task.Resources.WritePaths);
        Assert.IsFalse(task.Resources.NetworkHosts.IsDefault);
        Assert.IsEmpty(task.Resources.NetworkHosts);
        Assert.IsFalse(task.Resources.AllowProcessExecution);
        Assert.IsFalse(task.Resources.AllowCredentialAccess);
        Assert.IsFalse(task.Resources.AllowExternalMutations);

        var restored = RoundTrip(task);
        Assert.IsEmpty(restored.Dependencies);
        Assert.IsEmpty(restored.ExpectedArtifacts);
        Assert.IsEmpty(restored.AcceptanceCriteria);
        Assert.IsNull(restored.AgentId);
        Assert.IsNull(restored.Budget);
        Assert.IsNull(restored.ToolPolicy.AllowList);
    }

    [TestMethod]
    public void MinimalResultJson_UsesEmptyArtifactsEvidenceAndIssues()
    {
        var result = JsonSerializer.Deserialize<WorkflowTaskResult>(
            """{"GraphId":"graph-1","GraphFingerprint":"sha256:accepted-graph-content","TaskId":"task-1","Status":0,"Revision":"revision-1","Explanation":"Waiting for a dependency"}""")!;

        Assert.AreEqual("graph-1", result.GraphId);
        Assert.AreEqual("sha256:accepted-graph-content", result.GraphFingerprint);
        Assert.AreEqual(WorkflowTaskStatus.Blocked, result.Status);
        Assert.IsFalse(result.Artifacts.IsDefault);
        Assert.IsEmpty(result.Artifacts);
        Assert.IsFalse(result.VerificationEvidence.IsDefault);
        Assert.IsEmpty(result.VerificationEvidence);
        Assert.IsFalse(result.UnresolvedIssues.IsDefault);
        Assert.IsEmpty(result.UnresolvedIssues);

        var restored = RoundTrip(result);
        Assert.AreEqual(result.GraphId, restored.GraphId);
        Assert.AreEqual(result.GraphFingerprint, restored.GraphFingerprint);
        Assert.IsEmpty(restored.Artifacts);
        Assert.IsEmpty(restored.VerificationEvidence);
        Assert.IsEmpty(restored.UnresolvedIssues);
    }

    [TestMethod]
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

        Assert.IsNull(restoredTask.AgentId);
        Assert.IsNull(restoredTask.ToolPolicy.AllowList);
        Assert.IsNull(restoredTask.ToolPolicy.DenyList);
        Assert.IsNull(restoredTask.ToolPolicy.AllowedGroups);
        Assert.IsNull(restoredTask.ToolPolicy.InheritedPolicy);
        Assert.IsNotNull(restoredTask.Budget);
        Assert.IsNull(restoredTask.Budget.MaxTotalTokens);
        Assert.IsNull(restoredTask.Budget.MaxExecutionTime);
        Assert.IsNull(Assert.ContainsSingle(restoredTask.ExpectedArtifacts).RelativePath);
        Assert.IsTrue(Assert.ContainsSingle(restoredTask.ExpectedArtifacts).Required);
        Assert.IsTrue(Assert.ContainsSingle(restoredTask.AcceptanceCriteria).Required);
        Assert.IsNull(restoredEvidence.Command);
        Assert.IsNull(restoredEvidence.ExitCode);
    }

    [TestMethod]
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

        Assert.ContainsSingle(graph.Tasks);
        var saved = graph.Tasks[0];
        Assert.AreSequenceEqual(new[] { "prepare" }, saved.Dependencies);
        Assert.AreSequenceEqual(new[] { "src" }, saved.Resources.ReadPaths);
        Assert.AreEqual("report", Assert.ContainsSingle(saved.ExpectedArtifacts).ArtifactId);
        Assert.AreSequenceEqual(new[] { "report" }, Assert.ContainsSingle(saved.AcceptanceCriteria).ArtifactIds);
        Assert.AreSequenceEqual(new[] { "Review pending" }, result.UnresolvedIssues);
    }

    [TestMethod]
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

        Assert.IsTrue(validation.IsValid);
        var accepted = Assert.IsExactInstanceOfType<WorkflowTaskGraph>(validation.Graph);
        allowed.Add("run_command");
        denied.Clear();
        groups[0] = "group:messaging";
        inheritedAllowed.Clear();
        inheritedDenied.Clear();
        inheritedGroups.Add("group:messaging");

        var policy = Assert.ContainsSingle(accepted.Tasks).ToolPolicy;
        Assert.AreSequenceEqual(new[] { "read_file" }, policy.AllowList);
        Assert.AreSequenceEqual(new[] { "delete_file" }, policy.DenyList);
        Assert.AreSequenceEqual(new[] { "group:files" }, policy.AllowedGroups);
        var inherited = Assert.IsExactInstanceOfType<ToolSelectionPolicy>(policy.InheritedPolicy);
        Assert.AreSequenceEqual(new[] { "read_file", "apply_patch" }, inherited.AllowList);
        Assert.AreSequenceEqual(new[] { "send_message" }, inherited.DenyList);
        Assert.AreSequenceEqual(new[] { "group:files", "group:system" }, inherited.AllowedGroups);
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
            Assert.IsTrue(mutableInterface.IsReadOnly);
            Assert.ThrowsExactly<NotSupportedException>(() => mutableInterface.Add("injected-tool"));
        }
    }

    private static T RoundTrip<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
