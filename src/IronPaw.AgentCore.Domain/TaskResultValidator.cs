using System.Collections.Immutable;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed record TaskResultValidationError(string Code, string? ReferenceId, string Message);

public sealed record TaskResultValidationResult(ImmutableArray<TaskResultValidationError> Errors, bool HasConsistentSuccessClaim)
{
    public bool IsValid => Errors.IsEmpty;
}

/// <summary>
/// Checks receipt consistency against a task definition from an accepted graph. This does not
/// execute verification, inspect artifacts, authenticate evidence, or authorize any resource access.
/// </summary>
public static class TaskResultValidator
{
    public static TaskResultValidationResult Validate(WorkflowTaskGraph? graph, string taskId, WorkflowTaskResult? result)
    {
        var errors = ImmutableArray.CreateBuilder<TaskResultValidationError>();
        void Error(string code, string? id, string message) => errors.Add(new(code, id, message));
        if (graph is null) Error("GraphRequired", null, "An accepted task graph is required.");
        if (result is null) Error("ResultRequired", null, "A task result is required.");
        if (graph is null || result is null) return new(errors.ToImmutable(), false);

        var definition = TaskGraphValidator.Validate(graph);
        if (!definition.IsValid)
        {
            foreach (var error in definition.Errors)
                Error("InvalidGraphDefinition", error.TaskId, error.Message);
            return new(errors.ToImmutable(), false);
        }
        if (result.GraphId != definition.Graph!.GraphId)
            Error("GraphIdentityMismatch", result.GraphId, "The result belongs to a different task graph.");
        if (result.GraphFingerprint != definition.DefinitionFingerprint)
            Error("GraphFingerprintMismatch", result.GraphId, "The result does not identify the exact accepted graph definition.");
        var task = definition.Graph.Tasks.SingleOrDefault(item => item.TaskId == taskId);
        if (task is null)
        {
            Error("UnknownTask", taskId, "The requested task is not declared in this graph.");
            return new(errors.ToImmutable(), false);
        }
        if (result.TaskId != task.TaskId) Error("TaskIdentityMismatch", result.TaskId, "The result belongs to a different task.");
        if (!Enum.IsDefined(result.Status)) Error("InvalidStatus", result.TaskId, "The task status is not supported.");
        if (!Identity(result.Revision)) Error("RevisionRequired", result.TaskId, "A nonblank, unpadded revision identity is required.");
        if (string.IsNullOrWhiteSpace(result.Explanation)) Error("ExplanationRequired", result.TaskId, "Explain the task outcome.");
        if (result.Artifacts.IsDefault) Error("InvalidCollection", "Artifacts", "Artifacts must be an initialized collection.");
        if (result.VerificationEvidence.IsDefault) Error("InvalidCollection", "VerificationEvidence", "Evidence must be an initialized collection.");
        if (result.UnresolvedIssues.IsDefault) Error("InvalidCollection", "UnresolvedIssues", "Unresolved issues must be an initialized collection.");

        var requirements = task.ExpectedArtifacts.ToDictionary(item => item.ArtifactId, StringComparer.Ordinal);
        var criteria = task.AcceptanceCriteria.ToDictionary(item => item.CriterionId, StringComparer.Ordinal);
        var artifacts = new Dictionary<string, WorkflowTaskArtifact>(StringComparer.Ordinal);
        foreach (var artifact in result.Artifacts.IsDefault ? [] : result.Artifacts)
        {
            if (artifact is null) { Error("InvalidArtifact", null, "An artifact cannot be null."); continue; }
            if (!Identity(artifact.ArtifactId)) { Error("InvalidArtifactId", artifact.ArtifactId, "An artifact requires an identity."); continue; }
            if (!artifacts.TryAdd(artifact.ArtifactId, artifact)) Error("DuplicateArtifactId", artifact.ArtifactId, "Artifact identities must be unique.");
            if (artifact.ProducerTaskId != task.TaskId) Error("ArtifactProducerMismatch", artifact.ArtifactId, "The artifact producer must match this task.");
            if (!Enum.IsDefined(artifact.Kind)) Error("InvalidArtifactKind", artifact.ArtifactId, "The artifact kind is not supported.");
            if (requirements.TryGetValue(artifact.ArtifactId, out var requirement) && requirement.Kind != artifact.Kind)
                Error("ArtifactKindMismatch", artifact.ArtifactId, "The artifact kind differs from its declared requirement.");
            if (string.IsNullOrWhiteSpace(artifact.Location)) Error("ArtifactLocationRequired", artifact.ArtifactId, "An artifact requires a location.");
            if (!Identity(artifact.ContentDigest)) Error("ArtifactDigestRequired", artifact.ArtifactId, "An artifact requires a content digest.");
            if (!Identity(artifact.Revision) || artifact.Revision != result.Revision)
                Error("ArtifactRevisionMismatch", artifact.ArtifactId, "The artifact must identify the exact result revision.");
        }

        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        var evidenceByCriterion = new Dictionary<string, List<TaskVerificationEvidence>>(StringComparer.Ordinal);
        foreach (var evidence in result.VerificationEvidence.IsDefault ? [] : result.VerificationEvidence)
        {
            if (evidence is null) { Error("InvalidEvidence", null, "Evidence cannot be null."); continue; }
            if (!Identity(evidence.EvidenceId)) Error("InvalidEvidenceId", evidence.EvidenceId, "Evidence requires an identity.");
            else if (!evidenceIds.Add(evidence.EvidenceId)) Error("DuplicateEvidenceId", evidence.EvidenceId, "Evidence identities must be unique.");
            if (!Identity(evidence.CriterionId) || !criteria.ContainsKey(evidence.CriterionId))
                Error("UnknownCriterion", evidence.EvidenceId, "Evidence must refer to a declared acceptance criterion.");
            else
            {
                if (!evidenceByCriterion.TryGetValue(evidence.CriterionId, out var entries))
                    evidenceByCriterion[evidence.CriterionId] = entries = [];
                entries.Add(evidence);
            }
            if (!Enum.IsDefined(evidence.Outcome)) Error("InvalidVerificationOutcome", evidence.EvidenceId, "The evidence outcome is not supported.");
            if (!Identity(evidence.Revision) || evidence.Revision != result.Revision)
                Error("EvidenceRevisionMismatch", evidence.EvidenceId, "Verification evidence must evaluate the exact result revision.");
            if (string.IsNullOrWhiteSpace(evidence.Summary)) Error("EvidenceSummaryRequired", evidence.EvidenceId, "Describe what the evidence establishes.");
            if (evidence.Command is not null && string.IsNullOrWhiteSpace(evidence.Command))
                Error("InvalidCommand", evidence.EvidenceId, "A recorded command cannot be blank.");
            if (evidence.Outcome == TaskVerificationOutcome.Passed &&
                (evidence.ExitCode is not null && evidence.ExitCode != 0 || evidence.Command is not null && evidence.ExitCode is null))
                Error("CommandOutcomeMismatch", evidence.EvidenceId, "Passing command evidence requires a recorded zero exit code.");
            if (evidence.ArtifactIds.IsDefault)
            { Error("InvalidCollection", evidence.EvidenceId, "Evidence artifact references must be initialized."); continue; }
            var references = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artifactId in evidence.ArtifactIds)
            {
                if (!Identity(artifactId) || !artifacts.ContainsKey(artifactId))
                    Error("UnknownEvidenceArtifact", evidence.EvidenceId, "Evidence must refer to an artifact included in this result.");
                else if (!references.Add(artifactId)) Error("DuplicateArtifactReference", evidence.EvidenceId, "An evidence artifact reference cannot be repeated.");
            }
        }

        var issues = result.UnresolvedIssues.IsDefault ? [] : result.UnresolvedIssues;
        foreach (var issue in issues)
            if (string.IsNullOrWhiteSpace(issue)) Error("InvalidUnresolvedIssue", null, "An unresolved issue cannot be blank.");
        if (result.Status == WorkflowTaskStatus.Succeeded)
        {
            if (issues.Length > 0) Error("UnresolvedIssuesRemain", result.TaskId, "A successful task cannot retain unresolved issues.");
            if (!criteria.Values.Any(criterion => criterion.Required))
                Error("AcceptanceCriteriaRequired", result.TaskId, "Success requires at least one required acceptance criterion.");
            foreach (var required in requirements.Values.Where(item => item.Required))
                if (!artifacts.ContainsKey(required.ArtifactId)) Error("RequiredArtifactMissing", required.ArtifactId, "A required artifact is missing.");
            foreach (var criterion in criteria.Values.Where(item => item.Required))
            {
                if (!evidenceByCriterion.TryGetValue(criterion.CriterionId, out var evidence) || evidence.Count == 0)
                { Error("RequiredEvidenceMissing", criterion.CriterionId, "A required criterion has no verification evidence."); continue; }
                if (evidence.Any(item => item.Outcome != TaskVerificationOutcome.Passed))
                    Error("CriterionNotPassed", criterion.CriterionId, "All recorded evidence for a required criterion must pass.");
                var covered = evidence.Where(item => !item.ArtifactIds.IsDefault).SelectMany(item => item.ArtifactIds).ToHashSet(StringComparer.Ordinal);
                foreach (var artifactId in criterion.ArtifactIds)
                    if (!covered.Contains(artifactId)) Error("CriterionArtifactNotVerified", criterion.CriterionId,
                        $"Verification does not cover required artifact '{artifactId}'.");
            }
        }
        return new(errors.ToImmutable(), result.Status == WorkflowTaskStatus.Succeeded && errors.Count == 0);
    }

    private static bool Identity(string? value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() && !value.Any(char.IsControl);
}
