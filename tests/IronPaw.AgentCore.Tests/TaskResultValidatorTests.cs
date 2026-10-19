using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class TaskResultValidatorTests
{
    private const string Revision = "working-tree:content-snapshot-42";
    private static WorkflowTaskDefinition TaskDefinition() => new("implement", "Implement and verify the change")
    {
        ExpectedArtifacts = [new("patch", "Reviewed source patch", TaskArtifactKind.Patch)],
        AcceptanceCriteria = [new("tests", "The resulting revision passes the relevant tests") { ArtifactIds = ["patch"] }]
    };
    private static WorkflowTaskArtifact Artifact() => new("patch", "implement", TaskArtifactKind.Patch,
        "artifacts/change.patch", "sha256:reported-content-digest", Revision);
    private static TaskVerificationEvidence Evidence() => new("test-run", "tests", Revision,
        TaskVerificationOutcome.Passed, "Relevant tests passed")
    { ArtifactIds = ["patch"], Command = "dotnet test", ExitCode = 0 };
    private static WorkflowTaskGraph Graph(WorkflowTaskDefinition? task = null) =>
        new("feature-42", "Implement the feature", [task ?? TaskDefinition()]);
    private static WorkflowTaskResult Result(WorkflowTaskGraph? graph = null)
    {
        var accepted = TaskGraphValidator.Validate(graph ?? Graph());
        return new(accepted.Graph!.GraphId, accepted.DefinitionFingerprint!, "implement", WorkflowTaskStatus.Succeeded,
            Revision, "Implemented and verified")
        { Artifacts = [Artifact()], VerificationEvidence = [Evidence()] };
    }

    [TestMethod]
    public void MatchingArtifactsAndEvidence_SatisfyStructuralAcceptanceWithoutExecutingAnything()
    {
        var result = TaskResultValidator.Validate(Graph(), "implement", Result());
        Assert.IsTrue(result.IsValid);
        Assert.IsTrue(result.HasConsistentSuccessClaim);
        Assert.IsEmpty(result.Errors);
    }

    [TestMethod]
    public void ResultFromAnotherGraphWithIdenticalTaskIds_CannotBeReused()
    {
        var other = Graph() with { GraphId = "other-feature" };
        Reject(Result(other), "GraphIdentityMismatch");
    }

    [TestMethod]
    public void ChangedAcceptedDefinitionWithSameIdentities_RejectsOldEvidenceEnvelope()
    {
        var changed = Graph(TaskDefinition() with { Objective = "A materially different objective" });
        var validation = TaskResultValidator.Validate(changed, "implement", Result());
        Assert.IsFalse(validation.HasConsistentSuccessClaim);
        Assert.Contains(error => error.Code == "GraphFingerprintMismatch", validation.Errors);
    }

    [TestMethod]
    public void MissingFingerprintCannotClaimTheAcceptedPlan()
        => Reject(Result() with { GraphFingerprint = null! }, "GraphFingerprintMismatch");

    [TestMethod]
    public void CallerMustValidateTheExpectedTaskRatherThanLetAResultChooseItsOwnTarget()
    {
        var graph = Graph() with { Tasks = [new("inspect", "Inspect the code"), TaskDefinition()] };
        var validation = TaskResultValidator.Validate(graph, "inspect", Result(graph));
        Assert.IsFalse(validation.HasConsistentSuccessClaim);
        Assert.Contains(error => error.Code == "TaskIdentityMismatch", validation.Errors);
        Assert.Contains(error => error.Code == "UnknownTask", TaskResultValidator.Validate(graph, "missing", Result(graph)).Errors);
    }

    [TestMethod]
    public void InvalidGraphCannotBeValidatedThroughAnIsolatedTaskDefinition()
    {
        var graph = Graph(TaskDefinition() with { Dependencies = ["missing"] });
        var validation = TaskResultValidator.Validate(graph, "implement", Result());
        Assert.IsFalse(validation.HasConsistentSuccessClaim);
        Assert.Contains(error => error.Code == "InvalidGraphDefinition", validation.Errors);
    }

    [TestMethod]
    public void OriginalMutablePolicyCannotAlterAcceptedGraphOrItsResultBinding()
    {
        var allowed = new List<string> { "read_file" };
        var proposal = Graph(TaskDefinition() with { ToolPolicy = new(ToolProfile.Minimal, AllowList: allowed) });
        var accepted = TaskGraphValidator.Validate(proposal).Graph!;
        var receipt = Result(accepted);
        allowed.Add("run_command");
        Assert.IsTrue(TaskResultValidator.Validate(accepted, "implement", receipt).HasConsistentSuccessClaim);
        Assert.IsFalse(TaskResultValidator.Validate(proposal, "implement", receipt).HasConsistentSuccessClaim);
    }

    [TestMethod]
    [DataRow("other-revision", "EvidenceRevisionMismatch")]
    [DataRow("", "EvidenceRevisionMismatch")]
    public void StaleOrMissingEvidenceRevision_CannotEstablishSuccess(string revision, string code)
    {
        Reject(Result() with { VerificationEvidence = [Evidence() with { Revision = revision }] }, code);
    }

    [TestMethod]
    public void ArtifactFromAnotherRevision_CannotBeVerifiedByCurrentResult()
        => Reject(Result() with { Artifacts = [Artifact() with { Revision = "old-revision" }] }, "ArtifactRevisionMismatch");

    [TestMethod]
    public void AnotherTasksArtifact_CannotReplaceExpectedOutput()
        => Reject(Result() with { Artifacts = [Artifact() with { ProducerTaskId = "other-task" }] }, "ArtifactProducerMismatch");

    [TestMethod]
    public void ExecutionSuccessWithoutVerification_CannotEstablishTaskSuccess()
        => Reject(Result() with { VerificationEvidence = [] }, "RequiredEvidenceMissing");

    [TestMethod]
    [DataRow(TaskVerificationOutcome.Failed)]
    [DataRow(TaskVerificationOutcome.Inconclusive)]
    public void RequiredEvidenceThatDidNotPass_CannotEstablishSuccess(TaskVerificationOutcome outcome)
        => Reject(Result() with { VerificationEvidence = [Evidence() with { Outcome = outcome }] }, "CriterionNotPassed");

    [TestMethod]
    public void ConflictingEvidence_CannotChooseOnlyThePassingReceipt()
        => Reject(Result() with
        {
            VerificationEvidence = [Evidence(), Evidence() with {
            EvidenceId = "second-run", Outcome = TaskVerificationOutcome.Failed, ExitCode = 1 }]
        }, "CriterionNotPassed");

    [TestMethod]
    public void MissingRequiredOutput_CannotBeHiddenByPassingEvidence()
        => Reject(Result() with { Artifacts = [], VerificationEvidence = [Evidence() with { ArtifactIds = [] }] }, "RequiredArtifactMissing");

    [TestMethod]
    public void CriterionEvidenceMustCoverItsRequiredArtifacts()
        => Reject(Result() with { VerificationEvidence = [Evidence() with { ArtifactIds = [] }] }, "CriterionArtifactNotVerified");

    [TestMethod]
    public void UndeclaredCriterion_CannotSubstituteForRequiredCriterion()
        => Reject(Result() with { VerificationEvidence = [Evidence() with { CriterionId = "unrelated" }] }, "UnknownCriterion");

    [TestMethod]
    public void EvidenceCannotReferenceAnArtifactMissingFromTheResult()
        => Reject(Result() with { VerificationEvidence = [Evidence() with { ArtifactIds = ["unknown"] }] }, "UnknownEvidenceArtifact");

    [TestMethod]
    [DataRow(null)]
    [DataRow(1)]
    public void PassingCommandRequiresZeroExitCode(int? exitCode)
        => Reject(Result() with { VerificationEvidence = [Evidence() with { ExitCode = exitCode }] }, "CommandOutcomeMismatch");

    [TestMethod]
    public void UnresolvedIssuesPreventSuccess()
        => Reject(Result() with { UnresolvedIssues = ["A required scenario remains unverified"] }, "UnresolvedIssuesRemain");

    [TestMethod]
    [DataRow(WorkflowTaskStatus.Failed)]
    [DataRow(WorkflowTaskStatus.Cancelled)]
    [DataRow(WorkflowTaskStatus.BudgetExhausted)]
    [DataRow(WorkflowTaskStatus.Blocked)]
    public void IncompleteResultsPreservePartialArtifactsAndIssuesWithoutClaimingAcceptance(WorkflowTaskStatus status)
    {
        var result = TaskResultValidator.Validate(Graph(), "implement", Result() with
        {
            Status = status,
            VerificationEvidence = [],
            UnresolvedIssues = ["Verification did not finish"]
        });
        Assert.IsTrue(result.IsValid);
        Assert.IsFalse(result.HasConsistentSuccessClaim);
    }

    [TestMethod]
    public void NoRequiredAcceptanceCriteria_CannotBecomeImplicitSuccess()
    {
        var task = TaskDefinition() with { AcceptanceCriteria = [] };
        var graph = Graph(task);
        var result = TaskResultValidator.Validate(graph, "implement", Result(graph) with { VerificationEvidence = [] });
        Assert.IsFalse(result.HasConsistentSuccessClaim);
        Assert.Contains(error => error.Code == "AcceptanceCriteriaRequired", result.Errors);
    }

    [TestMethod]
    public void OptionalOutputAndCriteriaMayBeOmittedWhileRequiredAcceptancePasses()
    {
        var task = TaskDefinition() with
        {
            ExpectedArtifacts = [.. TaskDefinition().ExpectedArtifacts, new("notes", "Optional notes", TaskArtifactKind.Report, Required: false)],
            AcceptanceCriteria = [.. TaskDefinition().AcceptanceCriteria, new("extra", "Optional scenario", Required: false)]
        };
        var graph = Graph(task);
        Assert.IsTrue(TaskResultValidator.Validate(graph, "implement", Result(graph)).HasConsistentSuccessClaim);
    }

    [TestMethod]
    public void AcceptedTaskMayDependOnUpstreamWorkWhileRetainingTheAcceptedGraphEnvelope()
    {
        var graph = Graph() with { Tasks = [new("inspect", "Inspect the relevant code"), TaskDefinition() with { Dependencies = ["inspect"] }] };
        Assert.IsTrue(TaskResultValidator.Validate(graph, "implement", Result(graph)).HasConsistentSuccessClaim);
    }

    [TestMethod]
    public void DuplicateArtifactOrEvidenceIdentitiesAreRejected()
    {
        Reject(Result() with { Artifacts = [Artifact(), Artifact()] }, "DuplicateArtifactId");
        Reject(Result() with { VerificationEvidence = [Evidence(), Evidence()] }, "DuplicateEvidenceId");
    }

    [TestMethod]
    public void UnsupportedEnumsCannotBeInterpretedAsSuccess()
    {
        Reject(Result() with { Status = (WorkflowTaskStatus)99 }, "InvalidStatus");
        Reject(Result() with { VerificationEvidence = [Evidence() with { Outcome = (TaskVerificationOutcome)99 }] }, "InvalidVerificationOutcome");
        Reject(Result() with { Artifacts = [Artifact() with { Kind = (TaskArtifactKind)99 }] }, "InvalidArtifactKind");
    }

    [TestMethod]
    public void MalformedNullAndDefaultCollectionsProduceErrorsWithoutThrowing()
    {
        Assert.IsFalse(TaskResultValidator.Validate(null, "implement", null).IsValid);
        Reject(Result() with { Artifacts = default, VerificationEvidence = default, UnresolvedIssues = default }, "InvalidCollection");
        Reject(Result() with { Artifacts = [null!] }, "InvalidArtifact");
        Reject(Result() with { VerificationEvidence = [null!] }, "InvalidEvidence");
        Reject(Result() with { VerificationEvidence = [Evidence() with { ArtifactIds = default }] }, "InvalidCollection");
    }

    private static void Reject(WorkflowTaskResult receipt, string code)
    {
        var result = TaskResultValidator.Validate(Graph(), "implement", receipt);
        Assert.IsFalse(result.IsValid);
        Assert.IsFalse(result.HasConsistentSuccessClaim);
        Assert.Contains(error => error.Code == code, result.Errors);
    }
}
