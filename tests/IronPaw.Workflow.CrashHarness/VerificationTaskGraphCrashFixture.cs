using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;

namespace IronPaw.Workflow.CrashHarness;

/// <summary>OS-kill barriers around command intent and completion, before graph acceptance.</summary>
public static class VerificationTaskGraphCrashFixture
{
    public const string TaskId = "verified-file";
    public static CallerIdentity Caller { get; } = new("verification-crash-owner", "Verification crash owner", true);
    public static string WorkerEffectsPath(string root) => Path.Combine(root, "worker-effects.txt");
    public static string ProviderCallsPath(string root) => Path.Combine(root, "provider-calls.txt");

    public static async Task RunAsync(string root, CSharpDbWorkflowStore store, CrashPoint point, CancellationToken ct)
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await GitAsync(source, ["init"], ct);
        await GitAsync(source, ["config", "user.name", "IronPaw Verification Crash Tests"], ct);
        await GitAsync(source, ["config", "user.email", "tests@example.invalid"], ct);
        await File.WriteAllTextAsync(Path.Combine(source, "source.txt"), "committed verification input", ct);
        await GitAsync(source, ["add", "source.txt"], ct);
        await GitAsync(source, ["commit", "-m", "Controlled verification crash fixture"], ct);
        var graph = new WorkflowTaskGraph("verification-crash-" + Guid.NewGuid().ToString("n"), "Produce and verify one controlled candidate",
        [new WorkflowTaskDefinition(TaskId, "Write the controlled verification output")
        {
            ToolPolicy = new(ToolProfile.Coding),
            Resources = new() { ReadPaths = ["source.txt"], WritePaths = ["result.txt"] },
            ExpectedArtifacts = [new("output", "Controlled output", TaskArtifactKind.File, "result.txt")],
            AcceptanceCriteria = [new("integrity", "Output belongs to the sealed revision") { ArtifactIds = ["output"] },
                new("build", "Controlled worker established a successful command")]
        }]);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The harness executable path is unavailable.");
        var profile = new TaskVerificationProfile("controlled", executable,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(executable, ct))), ["--version"], TimeSpan.FromSeconds(30));
        var options = new TaskGraphAgentOptions(source, Path.Combine(root, "managed"), "HEAD", "mock", "verification-crash",
            new(ToolProfile.Coding), new(10, 10, 100_000, TimeSpan.FromMinutes(2)), MaxOutputTokens: 256)
        {
            DependencyMode = TaskDependencyMode.AcceptedChanges,
            ArtifactVerificationRules = [new(TaskId, "integrity")],
            VerificationAuthority = TaskVerificationAuthority.TrustedLocalProcess,
            VerificationProfiles = [profile], CommandVerificationRules = [new(TaskId, "build", profile.ProfileId)]
        };
        await new AgentTaskGraphRunner(store, store, new BarrierWorkspaces(root, store, point), new Model(root), new Worker(root, store, point))
            .StartAsync(graph, Caller, 1, options, ct);
        throw new InvalidOperationException("The requested command verification crash boundary was not reached.");
    }

    public static TaskVerificationCompletion EstablishedCompletion() => new(TaskCommandOutcome.Passed,
        "controlled-crash-worker/v1", "The controlled worker effect and successful outcome were independently established.", 0,
        new("", TaskVerificationIdentity.EmptyLogDigest, 0, false), new("", TaskVerificationIdentity.EmptyLogDigest, 0, false));

    private sealed class Worker(string root, CSharpDbWorkflowStore store, CrashPoint point) : ITaskVerificationWorker
    {
        public void ValidateProfile(TaskVerificationProfile profile, CallerIdentity caller)
        {
            if (caller != Caller) throw new UnauthorizedAccessException("Unexpected verification fixture caller.");
        }
        public async Task<TaskVerificationCompletion> ExecuteAsync(TaskVerificationJob job, CancellationToken ct)
        {
            var graph = await store.GetTaskGraphAsync(job.Claim.GraphId, ct);
            var state = graph?.Tasks.Single();
            var invocation = state?.Verifications.Single();
            if (state?.Status != TaskGraphTaskStatus.AwaitingAcceptance || state.Acceptance is not null ||
                invocation?.State != TaskVerificationInvocationState.Started || invocation.Completion is not null ||
                invocation.InvocationId != job.Copy.InvocationId)
                throw new InvalidOperationException("The command intent was not durably persisted before worker execution.");
            await AppendDurableAsync(WorkerEffectsPath(root), invocation.InvocationId, ct);
            if (point == CrashPoint.AfterVerificationStarted)
                await CrashFixture.SignalAsync(root, new RunId(job.Claim.GraphId), ct);
            return EstablishedCompletion();
        }
    }

    private sealed class BarrierWorkspaces(string root, CSharpDbWorkflowStore store, CrashPoint point) : ITaskWorkspaceManager
    {
        private readonly GitTaskWorkspaceManager inner = new();
        public Task<string> ResolveCommitAsync(string sourceWorkspaceRoot, string revision, CancellationToken ct) => inner.ResolveCommitAsync(sourceWorkspaceRoot, revision, ct);
        public Task<TaskWorkspace> OpenOrCreateAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct) => inner.OpenOrCreateAsync(binding, claim, ct);
        public Task<TaskWorkspace> OpenExistingAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct) => inner.OpenExistingAsync(binding, claim, ct);
        public Task<TaskWorkspace> PrepareAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, ImmutableArray<TaskDependencyInput> dependencies, CancellationToken ct) => inner.PrepareAsync(binding, claim, dependencies, ct);
        public Task ValidateWorkingStateAsync(TaskWorkspace workspace, ImmutableArray<WorkflowToolExecution> tools, CancellationToken ct) => inner.ValidateWorkingStateAsync(workspace, tools, ct);
        public Task<TaskWorkspaceCapture> CaptureAsync(TaskWorkspace workspace, ImmutableArray<TaskArtifactRequirement> requirements, CancellationToken ct) => inner.CaptureAsync(workspace, requirements, ct);
        public Task<TaskVerificationCopy> CreateVerificationCopyAsync(TaskWorkspace workspace, WorkflowTaskResult result, string invocationId, CancellationToken ct) => inner.CreateVerificationCopyAsync(workspace, result, invocationId, ct);
        public async Task<bool> VerifyAsync(TaskWorkspace workspace, WorkflowTaskResult result, CancellationToken ct)
        {
            if (point == CrashPoint.AfterVerificationCompleted)
            {
                var graph = await store.GetTaskGraphAsync(workspace.GraphId, ct);
                var state = graph?.Tasks.Single();
                if (state?.Verifications.SingleOrDefault() is { State: TaskVerificationInvocationState.Completed } invocation)
                {
                    if (state.Status != TaskGraphTaskStatus.AwaitingAcceptance || state.Acceptance is not null || state.LeaseToken is null ||
                        invocation.Completion?.Outcome != TaskCommandOutcome.Passed || invocation.Revision != result.Revision)
                        throw new InvalidOperationException("The completion-before-acceptance boundary was not durably established.");
                    await CrashFixture.SignalAsync(root, new RunId(workspace.GraphId), ct);
                }
            }
            return await inner.VerifyAsync(workspace, result, ct);
        }
    }

    private sealed class Model(string root) : ILlmClient
    {
        private int calls;
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            var turn = ++calls;
            await AppendDurableAsync(ProviderCallsPath(root), request.RunId.Value + ":" + turn, ct);
            yield return new UsageDelta(7, 3, 0, 0);
            if (turn == 1) yield return new ToolCallDelta("write-output", "write_file", JsonSerializer.SerializeToElement(new { path = "result.txt", content = "durable controlled candidate" }));
            else yield return new AssistantTextDelta("The controlled candidate is ready for host verification.");
            yield return new CompletedDelta();
        }
    }

    private static async Task AppendDurableAsync(string path, string line, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        await file.WriteAsync(Encoding.UTF8.GetBytes(line + Environment.NewLine), ct);
        file.Flush(flushToDisk: true);
    }

    private static async Task GitAsync(string path, string[] arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-C"); info.ArgumentList.Add(path);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the controlled Git fixture.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await output;
        if (process.ExitCode != 0) throw new InvalidOperationException("Git fixture failed: " + await error);
        await error;
    }
}
