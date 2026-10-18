using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

public sealed class TaskResultValidatorTests
{
    private const string Revision = "working-tree:content-snapshot-42";
    private static WorkflowTaskDefinition TaskDefinition() => new("implement", "Implement and verify the change") {
        ExpectedArtifacts = [new("patch", "Reviewed source patch", TaskArtifactKind.Patch)],
        AcceptanceCriteria = [new("tests", "The resulting revision passes the relevant tests") { ArtifactIds = ["patch"] }] };
    private static WorkflowTaskArtifact Artifact() => new("patch", "implement", TaskArtifactKind.Patch,
        "artifacts/change.patch", "sha256:reported-content-digest", Revision);
    private static TaskVerificationEvidence Evidence() => new("test-run", "tests", Revision,
        TaskVerificationOutcome.Passed, "Relevant tests passed") { ArtifactIds = ["patch"], Command = "dotnet test", ExitCode = 0 };
    private static WorkflowTaskGraph Graph(WorkflowTaskDefinition? task = null) =>
        new("feature-42", "Implement the feature", [task ?? TaskDefinition()]);
    private static WorkflowTaskResult Result(WorkflowTaskGraph? graph = null)
    {
        var accepted = TaskGraphValidator.Validate(graph ?? Graph());
        return new(accepted.Graph!.GraphId, accepted.DefinitionFingerprint!, "implement", WorkflowTaskStatus.Succeeded,
            Revision, "Implemented and verified") { Artifacts = [Artifact()], VerificationEvidence = [Evidence()] };
    }

    [Fact]
    public void MatchingArtifactsAndEvidence_SatisfyStructuralAcceptanceWithoutExecutingAnything()
    {
        var result = TaskResultValidator.Validate(Graph(), "implement", Result());
        Assert.True(result.IsValid);
        Assert.True(result.HasConsistentSuccessClaim);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ResultFromAnotherGraphWithIdenticalTaskIds_CannotBeReused()
    {
        var other = Graph() with { GraphId = "other-feature" };
        Reject(Result(other), "GraphIdentityMismatch");
    }

    [Fact]
    public void ChangedAcceptedDefinitionWithSameIdentities_RejectsOldEvidenceEnvelope()
    {
        var changed = Graph(TaskDefinition() with { Objective = "A materially different objective" });
        var validation = TaskResultValidator.Validate(changed, "implement", Result());
        Assert.False(validation.HasConsistentSuccessClaim);
        Assert.Contains(validation.Errors, error => error.Code == "GraphFingerprintMismatch");
    }

    [Fact]
    public void MissingFingerprintCannotClaimTheAcceptedPlan()
        => Reject(Result() with { GraphFingerprint = null! }, "GraphFingerprintMismatch");

    [Fact]
    public void CallerMustValidateTheExpectedTaskRatherThanLetAResultChooseItsOwnTarget()
    {
        var graph = Graph() with { Tasks = [new("inspect", "Inspect the code"), TaskDefinition()] };
        var validation = TaskResultValidator.Validate(graph, "inspect", Result(graph));
        Assert.False(validation.HasConsistentSuccessClaim);
        Assert.Contains(validation.Errors, error => error.Code == "TaskIdentityMismatch");
        Assert.Contains(TaskResultValidator.Validate(graph, "missing", Result(graph)).Errors, error => error.Code == "UnknownTask");
    }

    [Fact]
    public void InvalidGraphCannotBeValidatedThroughAnIsolatedTaskDefinition()
    {
        var graph = Graph(TaskDefinition() with { Dependencies = ["missing"] });
        var validation = TaskResultValidator.Validate(graph, "implement", Result());
        Assert.False(validation.HasConsistentSuccessClaim);
        Assert.Contains(validation.Errors, error => error.Code == "InvalidGraphDefinition");
    }

    [Fact]
    public void OriginalMutablePolicyCannotAlterAcceptedGraphOrItsResultBinding()
    {
        var allowed = new List<string> { "read_file" };
        var proposal = Graph(TaskDefinition() with { ToolPolicy = new(ToolProfile.Minimal, AllowList: allowed) });
        var accepted = TaskGraphValidator.Validate(proposal).Graph!;
        var receipt = Result(accepted);
        allowed.Add("run_command");
        Assert.True(TaskResultValidator.Validate(accepted, "implement", receipt).HasConsistentSuccessClaim);
        Assert.False(TaskResultValidator.Validate(proposal, "implement", receipt).HasConsistentSuccessClaim);
    }

    [Theory]
    [InlineData("other-revision", "EvidenceRevisionMismatch")]
    [InlineData("", "EvidenceRevisionMismatch")]
    public void StaleOrMissingEvidenceRevision_CannotEstablishSuccess(string revision, string code)
    {
        Reject(Result() with { VerificationEvidence = [Evidence() with { Revision = revision }] }, code);
    }

    [Fact]
    public void ArtifactFromAnotherRevision_CannotBeVerifiedByCurrentResult()
        => Reject(Result() with { Artifacts = [Artifact() with { Revision = "old-revision" }] }, "ArtifactRevisionMismatch");

    [Fact]
    public void AnotherTasksArtifact_CannotReplaceExpectedOutput()
        => Reject(Result() with { Artifacts = [Artifact() with { ProducerTaskId = "other-task" }] }, "ArtifactProducerMismatch");

    [Fact]
    public void ExecutionSuccessWithoutVerification_CannotEstablishTaskSuccess()
        => Reject(Result() with { VerificationEvidence = [] }, "RequiredEvidenceMissing");

    [Theory]
    [InlineData(TaskVerificationOutcome.Failed)]
    [InlineData(TaskVerificationOutcome.Inconclusive)]
    public void RequiredEvidenceThatDidNotPass_CannotEstablishSuccess(TaskVerificationOutcome outcome)
        => Reject(Result() with { VerificationEvidence = [Evidence() with { Outcome = outcome }] }, "CriterionNotPassed");

    [Fact]
    public void ConflictingEvidence_CannotChooseOnlyThePassingReceipt()
        => Reject(Result() with { VerificationEvidence = [Evidence(), Evidence() with {
            EvidenceId = "second-run", Outcome = TaskVerificationOutcome.Failed, ExitCode = 1 }] }, "CriterionNotPassed");

    [Fact]
    public void MissingRequiredOutput_CannotBeHiddenByPassingEvidence()
        => Reject(Result() with { Artifacts = [], VerificationEvidence = [Evidence() with { ArtifactIds = [] }] }, "RequiredArtifactMissing");

    [Fact]
    public void CriterionEvidenceMustCoverItsRequiredArtifacts()
        => Reject(Result() with { VerificationEvidence = [Evidence() with { ArtifactIds = [] }] }, "CriterionArtifactNotVerified");

    [Fact]
    public void UndeclaredCriterion_CannotSubstituteForRequiredCriterion()
        => Reject(Result() with { VerificationEvidence = [Evidence() with { CriterionId = "unrelated" }] }, "UnknownCriterion");

    [Fact]
    public void EvidenceCannotReferenceAnArtifactMissingFromTheResult()
        => Reject(Result() with { VerificationEvidence = [Evidence() with { ArtifactIds = ["unknown"] }] }, "UnknownEvidenceArtifact");

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void PassingCommandRequiresZeroExitCode(int? exitCode)
        => Reject(Result() with { VerificationEvidence = [Evidence() with { ExitCode = exitCode }] }, "CommandOutcomeMismatch");

    [Fact]
    public void UnresolvedIssuesPreventSuccess()
        => Reject(Result() with { UnresolvedIssues = ["A required scenario remains unverified"] }, "UnresolvedIssuesRemain");

    [Theory]
    [InlineData(WorkflowTaskStatus.Failed)]
    [InlineData(WorkflowTaskStatus.Cancelled)]
    [InlineData(WorkflowTaskStatus.BudgetExhausted)]
    [InlineData(WorkflowTaskStatus.Blocked)]
    public void IncompleteResultsPreservePartialArtifactsAndIssuesWithoutClaimingAcceptance(WorkflowTaskStatus status)
    {
        var result = TaskResultValidator.Validate(Graph(), "implement", Result() with {
            Status = status, VerificationEvidence = [], UnresolvedIssues = ["Verification did not finish"] });
        Assert.True(result.IsValid);
        Assert.False(result.HasConsistentSuccessClaim);
    }

    [Fact]
    public void NoRequiredAcceptanceCriteria_CannotBecomeImplicitSuccess()
    {
        var task = TaskDefinition() with { AcceptanceCriteria = [] };
        var graph = Graph(task);
        var result = TaskResultValidator.Validate(graph, "implement", Result(graph) with { VerificationEvidence = [] });
        Assert.False(result.HasConsistentSuccessClaim);
        Assert.Contains(result.Errors, error => error.Code == "AcceptanceCriteriaRequired");
    }

    [Fact]
    public void OptionalOutputAndCriteriaMayBeOmittedWhileRequiredAcceptancePasses()
    {
        var task = TaskDefinition() with {
            ExpectedArtifacts = [.. TaskDefinition().ExpectedArtifacts, new("notes", "Optional notes", TaskArtifactKind.Report, Required: false)],
            AcceptanceCriteria = [.. TaskDefinition().AcceptanceCriteria, new("extra", "Optional scenario", Required: false)] };
        var graph = Graph(task);
        Assert.True(TaskResultValidator.Validate(graph, "implement", Result(graph)).HasConsistentSuccessClaim);
    }

    [Fact]
    public void AcceptedTaskMayDependOnUpstreamWorkWhileRetainingTheAcceptedGraphEnvelope()
    {
        var graph = Graph() with { Tasks = [new("inspect", "Inspect the relevant code"), TaskDefinition() with { Dependencies = ["inspect"] }] };
        Assert.True(TaskResultValidator.Validate(graph, "implement", Result(graph)).HasConsistentSuccessClaim);
    }

    [Fact]
    public void DuplicateArtifactOrEvidenceIdentitiesAreRejected()
    {
        Reject(Result() with { Artifacts = [Artifact(), Artifact()] }, "DuplicateArtifactId");
        Reject(Result() with { VerificationEvidence = [Evidence(), Evidence()] }, "DuplicateEvidenceId");
    }

    [Fact]
    public void UnsupportedEnumsCannotBeInterpretedAsSuccess()
    {
        Reject(Result() with { Status = (WorkflowTaskStatus)99 }, "InvalidStatus");
        Reject(Result() with { VerificationEvidence = [Evidence() with { Outcome = (TaskVerificationOutcome)99 }] }, "InvalidVerificationOutcome");
        Reject(Result() with { Artifacts = [Artifact() with { Kind = (TaskArtifactKind)99 }] }, "InvalidArtifactKind");
    }

    [Fact]
    public void MalformedNullAndDefaultCollectionsProduceErrorsWithoutThrowing()
    {
        Assert.False(TaskResultValidator.Validate(null, "implement", null).IsValid);
        Reject(Result() with { Artifacts = default, VerificationEvidence = default, UnresolvedIssues = default }, "InvalidCollection");
        Reject(Result() with { Artifacts = [null!] }, "InvalidArtifact");
        Reject(Result() with { VerificationEvidence = [null!] }, "InvalidEvidence");
        Reject(Result() with { VerificationEvidence = [Evidence() with { ArtifactIds = default }] }, "InvalidCollection");
    }

    private static void Reject(WorkflowTaskResult receipt, string code)
    {
        var result = TaskResultValidator.Validate(Graph(), "implement", receipt);
        Assert.False(result.IsValid);
        Assert.False(result.HasConsistentSuccessClaim);
        Assert.Contains(result.Errors, error => error.Code == code);
    }
}
