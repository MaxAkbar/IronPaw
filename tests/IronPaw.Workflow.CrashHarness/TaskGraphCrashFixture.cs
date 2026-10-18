using System.Security.Cryptography;
using System.Text;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Workflow.CrashHarness;

public static class TaskGraphCrashFixture
{
    public const string TaskId = "record-effect";
    public static CallerIdentity Caller { get; } = CallerIdentity.Authenticated("graph-crash-caller", "Graph crash caller");

    public static async Task RunAsync(string root, ITaskGraphStore store, CrashPoint point, CancellationToken ct)
    {
        var graph = new WorkflowTaskGraph("graph-crash-" + Guid.NewGuid().ToString("n"),
            "Record exactly one durable effect and independently inspect it",
        [
            new WorkflowTaskDefinition(TaskId, "Append exactly one visible effect")
            {
                ExpectedArtifacts = [new("effect-log", "The durable effect log", TaskArtifactKind.File, "effects.txt")],
                AcceptanceCriteria = [new TaskAcceptanceCriterion("one-effect", "The matching attempt was recorded exactly once")
                    { ArtifactIds = ["effect-log"] }]
            }
        ]);
        var scheduler = new TaskGraphScheduler(store, new EffectExecutor(root, point), new EffectVerifier(root, point));
        var completed = await scheduler.StartAsync(graph, Caller, 1, ct);
        if (point == CrashPoint.AfterGraphAcceptance)
        {
            var state = completed.Tasks.Single();
            if (state.Status != TaskGraphTaskStatus.Succeeded || state.Acceptance?.Outcome != TaskAcceptanceOutcome.Accepted)
                throw new InvalidOperationException("The graph acceptance checkpoint was not reached.");
            await CrashFixture.SignalAsync(root, new RunId(graph.GraphId), ct);
        }
    }

    public static WorkflowTaskResult EstablishedResult(string root, string graphId, string graphFingerprint, string attemptId)
    {
        var revision = "effect:" + attemptId;
        var path = CrashFixture.EffectsPath(root);
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        return new(graphId, graphFingerprint, TaskId, WorkflowTaskStatus.Succeeded, revision,
            "The durable log contains the exact task attempt")
        {
            Artifacts = [new("effect-log", TaskId, TaskArtifactKind.File, path, digest, revision)],
            VerificationEvidence = [new TaskVerificationEvidence("effect-count", "one-effect", revision,
                TaskVerificationOutcome.Passed, "Inspected the recorded attempt and content digest") { ArtifactIds = ["effect-log"] }]
        };
    }

    public static TaskAcceptanceDecision InspectEffect(string root, TaskGraphClaim claim, WorkflowTaskResult result)
    {
        var lines = File.ReadAllLines(CrashFixture.EffectsPath(root));
        var expected = EstablishedResult(root, claim.GraphId, claim.GraphFingerprint, claim.AttemptId);
        if (lines.Length != 1 || lines[0] != $"{claim.GraphId}:{claim.AttemptId}" ||
            result.GraphId != expected.GraphId || result.GraphFingerprint != expected.GraphFingerprint ||
            result.TaskId != expected.TaskId || result.Revision != expected.Revision ||
            result.Artifacts.Length != 1 || result.Artifacts[0] != expected.Artifacts[0])
            throw new InvalidOperationException("The exact graph attempt, revision and effect digest could not be independently confirmed.");
        return new(TaskAcceptanceOutcome.Accepted, "graph-crash-verifier",
            "Independently confirmed exactly one matching attempt and the persisted artifact digest");
    }

    private sealed class EffectExecutor(string root, CrashPoint point) : IWorkflowTaskExecutor
    {
        public async Task<WorkflowTaskResult> ExecuteAsync(TaskGraphClaim claim, CancellationToken ct)
        {
            var payload = Encoding.UTF8.GetBytes($"{claim.GraphId}:{claim.AttemptId}{Environment.NewLine}");
            await using (var file = new FileStream(CrashFixture.EffectsPath(root), FileMode.Append, FileAccess.Write,
                FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                await file.WriteAsync(payload, ct);
                file.Flush(flushToDisk: true);
            }
            if (point == CrashPoint.AfterGraphEffect)
                await CrashFixture.SignalAsync(root, new RunId(claim.GraphId), ct);
            return EstablishedResult(root, claim.GraphId, claim.GraphFingerprint, claim.AttemptId);
        }
    }

    private sealed class EffectVerifier(string root, CrashPoint point) : ITaskAcceptanceVerifier
    {
        public async Task<TaskAcceptanceDecision> VerifyAsync(TaskGraphClaim claim, WorkflowTaskResult result, CancellationToken ct)
        {
            // The scheduler reaches the verifier only after committing the successful proposal.
            if (point == CrashPoint.AfterGraphProposal)
                await CrashFixture.SignalAsync(root, new RunId(claim.GraphId), ct);
            return InspectEffect(root, claim, result);
        }
    }
}
