using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;

namespace IronPaw.Workflow.CrashHarness;

/// <summary>Real adapter boundaries after durable child completion and after candidate persistence.</summary>
public static class AgentTaskGraphCrashFixture
{
    public const string TaskId = "produce-file";
    public const string OutputName = "result.txt";
    public const string OutputContent = "confirmed isolated write";
    public static CallerIdentity Caller { get; } = new("agent-graph-crash-owner", "Agent graph crash owner", true);
    public static string ProviderCallsPath(string root) => Path.Combine(root, "provider-calls.txt");
    public static string SourcePath(string root) => Path.Combine(root, "source");
    public static string WorkspacesPath(string root) => Path.Combine(root, "task-workspaces");

    public static async Task RunAsync(string root, CSharpDbWorkflowStore store, CrashPoint point, CancellationToken ct)
    {
        var source = SourcePath(root);
        Directory.CreateDirectory(source);
        await GitAsync(source, ["init"], ct);
        await GitAsync(source, ["config", "user.name", "IronPaw Crash Tests"], ct);
        await GitAsync(source, ["config", "user.email", "tests@example.invalid"], ct);
        await File.WriteAllTextAsync(Path.Combine(source, "source.txt"), "committed source", ct);
        await GitAsync(source, ["add", "source.txt"], ct);
        await GitAsync(source, ["commit", "-m", "Accepted task base"], ct);
        var graph = new WorkflowTaskGraph("agent-graph-crash-" + Guid.NewGuid().ToString("n"), "Produce and seal an isolated file",
        [
            new WorkflowTaskDefinition(TaskId, "Write the declared task output")
            {
                ToolPolicy = new(ToolProfile.Coding),
                Resources = new() { ReadPaths = ["source.txt"], WritePaths = [OutputName] },
                ExpectedArtifacts = [new("output", "The isolated output file", TaskArtifactKind.File, OutputName)],
                AcceptanceCriteria = [new("intact", "The declared output exists in the sealed revision") { ArtifactIds = ["output"] }]
            }
        ]);
        var options = new TaskGraphAgentOptions(source, WorkspacesPath(root), "HEAD", "mock", "crash-model",
            new(ToolProfile.Coding), new(10, 10, 100_000, TimeSpan.FromMinutes(2)), MaxOutputTokens: 256)
        { ArtifactVerificationRules = [new(TaskId, "intact")] };
        await new AgentTaskGraphRunner(store, store, new BlockingWorkspaces(root, store, point), new Model(root))
            .StartAsync(graph, Caller, 1, options, ct);
        throw new InvalidOperationException("The requested adapter crash boundary was not reached.");
    }

    private sealed class BlockingWorkspaces(string root, CSharpDbWorkflowStore store, CrashPoint point) : ITaskWorkspaceManager
    {
        private readonly GitTaskWorkspaceManager inner = new();
        public Task<string> ResolveCommitAsync(string sourceWorkspaceRoot, string revision, CancellationToken ct) =>
            inner.ResolveCommitAsync(sourceWorkspaceRoot, revision, ct);
        public Task<TaskWorkspace> OpenOrCreateAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct) =>
            inner.OpenOrCreateAsync(binding, claim, ct);
        public Task<TaskWorkspace> OpenExistingAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct) =>
            inner.OpenExistingAsync(binding, claim, ct);
        public Task<TaskWorkspace> PrepareAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim,
            ImmutableArray<TaskDependencyInput> dependencies, CancellationToken ct) => inner.PrepareAsync(binding, claim, dependencies, ct);
        public Task ValidateWorkingStateAsync(TaskWorkspace workspace, ImmutableArray<WorkflowToolExecution> tools, CancellationToken ct) => inner.ValidateWorkingStateAsync(workspace, tools, ct);
        public async Task<TaskWorkspaceCapture> CaptureAsync(TaskWorkspace workspace,
            ImmutableArray<TaskArtifactRequirement> requirements, CancellationToken ct)
        {
            if (point == CrashPoint.AfterAgentChildSuccess)
            {
                var childId = AgentTaskIdentity.ChildRunId(workspace.GraphFingerprint, workspace.TaskId, workspace.AttemptId);
                var child = await store.GetAsync(childId, ct);
                var graph = await store.GetTaskGraphAsync(workspace.GraphId, ct);
                if (child?.Result?.Status != AgentRunStatus.Succeeded || child.LeaseOwner is not null ||
                    graph?.Tasks.Single().Status != TaskGraphTaskStatus.Running || graph.Tasks.Single().Result is not null)
                    throw new InvalidOperationException("The child-success-before-capture boundary was not durably established.");
                await CrashFixture.SignalAsync(root, new RunId(workspace.GraphId), ct);
            }
            return await inner.CaptureAsync(workspace, requirements, ct);
        }
        public async Task<bool> VerifyAsync(TaskWorkspace workspace, WorkflowTaskResult result, CancellationToken ct)
        {
            if (point == CrashPoint.AfterAgentCandidate)
            {
                var graph = await store.GetTaskGraphAsync(workspace.GraphId, ct);
                var state = graph?.Tasks.Single();
                if (state?.Status != TaskGraphTaskStatus.AwaitingAcceptance || state.Acceptance is not null ||
                    JsonSerializer.Serialize(state.Result) != JsonSerializer.Serialize(result))
                    throw new InvalidOperationException("The candidate-before-verification boundary was not durably established.");
                await CrashFixture.SignalAsync(root, new RunId(workspace.GraphId), ct);
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
            await using (var receipt = new FileStream(ProviderCallsPath(root), FileMode.Append, FileAccess.Write,
                FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                await receipt.WriteAsync(Encoding.UTF8.GetBytes(request.RunId.Value + ":" + turn + Environment.NewLine), ct);
                receipt.Flush(flushToDisk: true);
            }
            yield return new UsageDelta(7, 3, 0, 0);
            if (turn == 1)
                yield return new ToolCallDelta("write-output", "write_file", JsonSerializer.SerializeToElement(new { path = OutputName, content = OutputContent }));
            else yield return new AssistantTextDelta("The isolated output is complete.");
            yield return new CompletedDelta();
        }
    }

    private static async Task GitAsync(string root, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(root);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated Git fixture command.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await output;
        if (process.ExitCode != 0) throw new InvalidOperationException("Git fixture failed: " + await error);
        await error;
    }
}
