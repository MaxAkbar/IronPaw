using System.Collections.Immutable;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

/// <summary>Freezes accepted ancestor identities before an agent is admitted to a prepared workspace.</summary>
public static class TaskDependencyResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 256 };

    public static string ResultDigest(WorkflowTaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return TaskInputIdentity.ResultDigest(result);
    }

    public static TaskAttemptInputs CreateInputs(TaskGraphCheckpoint graph, string taskId, string attemptId)
    {
        ArgumentNullException.ThrowIfNull(graph);
        RequireIdentity(attemptId);
        var binding = graph.AgentBinding ?? throw new InvalidDataException("Task inputs require an accepted agent binding.");
        if (binding.DependencyMode != TaskDependencyMode.AcceptedChanges)
            throw new InvalidDataException("Legacy base-commit tasks cannot acquire predecessor inputs.");
        var definition = TaskGraphValidator.Validate(graph.Graph);
        if (!definition.IsValid || definition.DefinitionFingerprint != graph.DefinitionFingerprint)
            throw new InvalidDataException("The task graph definition does not match its fingerprint.");
        if (graph.Tasks.IsDefault || graph.Tasks.Length != definition.Graph!.Tasks.Length ||
            graph.Tasks.Any(state => state is null) || graph.Tasks.Select(state => state.TaskId).Distinct(StringComparer.Ordinal).Count() != graph.Tasks.Length)
            throw new InvalidDataException("Task input resolution requires complete, unique task states.");
        var task = definition.Graph!.Tasks.SingleOrDefault(item => item.TaskId == taskId)
            ?? throw new InvalidDataException("The consumer task is not declared in this graph.");
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(task.Dependencies);
        while (pending.TryPop(out var id))
            if (ancestors.Add(id))
                foreach (var dependency in definition.Graph.Tasks.Single(item => item.TaskId == id).Dependencies)
                    pending.Push(dependency);
        var pins = ImmutableArray.CreateBuilder<TaskDependencyReceipt>(ancestors.Count);
        foreach (var id in definition.TopologicalTaskIds.Where(ancestors.Contains))
        {
            var state = graph.Tasks.SingleOrDefault(item => item.TaskId == id)
                ?? throw new InvalidDataException("An ancestor task state is missing.");
            if (state.Status != TaskGraphTaskStatus.Succeeded || state.Result is not { } result ||
                result.Status is not (WorkflowTaskStatus.Succeeded or WorkflowTaskStatus.AwaitingVerification) ||
                state.Acceptance is not { Outcome: TaskAcceptanceOutcome.Accepted } receipt)
                throw new InvalidDataException("Only accepted successful ancestors can supply task inputs.");
            RequireIdentity(state.AttemptId);
            var digest = ResultDigest(result);
            if (result.GraphId != graph.Graph.GraphId || result.GraphFingerprint != graph.DefinitionFingerprint || result.TaskId != id ||
                receipt.GraphFingerprint != graph.DefinitionFingerprint || receipt.TaskId != id || receipt.AttemptId != state.AttemptId ||
                receipt.Revision != result.Revision || receipt.ResultDigest != digest)
                throw new InvalidDataException("An ancestor acceptance does not identify its exact established result.");
            pins.Add(new(id, state.AttemptId!, digest, result.Revision, receipt));
        }
        var inputs = new TaskAttemptInputs(graph.DefinitionFingerprint, taskId, attemptId, binding.BaseCommit, pins.ToImmutable(), "");
        return inputs with { Digest = InputDigest(inputs) };
    }

    public static ImmutableArray<TaskDependencyInput> Resolve(TaskGraphCheckpoint graph, TaskGraphClaim claim)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.GraphId != graph.Graph.GraphId || claim.GraphFingerprint != graph.DefinitionFingerprint ||
            !graph.Graph.Tasks.Any(task => Same(task, claim.Task)))
            throw new InvalidDataException("The task claim does not identify the accepted graph definition.");
        var expected = CreateInputs(graph, claim.Task.TaskId, claim.AttemptId);
        if (claim.Inputs is null || !Same(expected, claim.Inputs))
            throw new InvalidDataException("The task claim inputs differ from its frozen accepted ancestors.");
        var result = ImmutableArray.CreateBuilder<TaskDependencyInput>(expected.Dependencies.Length);
        foreach (var pin in expected.Dependencies)
        {
            var task = graph.Graph.Tasks.Single(item => item.TaskId == pin.TaskId);
            var state = graph.Tasks.Single(item => item.TaskId == pin.TaskId);
            var sourceInputs = CreateInputs(graph, pin.TaskId, pin.AttemptId);
            if (state.Inputs is null || !Same(sourceInputs, state.Inputs))
                throw new InvalidDataException("An accepted ancestor has no matching frozen input baseline.");
            result.Add(new(task, pin.AttemptId, state.Result!, state.Acceptance!, state.Inputs));
        }
        return result.ToImmutable();
    }

    /// <summary>Checks the self-contained receipt before it is embedded in a durable child request.</summary>
    public static void ValidateShape(TaskAttemptInputs inputs, string graphFingerprint, string taskId,
        string attemptId, string baseCommit)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.GraphFingerprint != graphFingerprint || inputs.TaskId != taskId || inputs.AttemptId != attemptId ||
            inputs.BaseCommit != baseCommit || inputs.Dependencies.IsDefault || inputs.Digest != InputDigest(inputs))
            throw new InvalidDataException("Task input identity or digest is invalid.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in inputs.Dependencies)
        {
            if (pin is null || !seen.Add(pin.TaskId)) throw new InvalidDataException("Task input pins must be nonnull and unique.");
            RequireIdentity(pin.TaskId);
            RequireIdentity(pin.AttemptId);
            RequireIdentity(pin.Revision);
            if (!IsDigest(pin.ResultDigest) || pin.Acceptance is not { Outcome: TaskAcceptanceOutcome.Accepted } receipt ||
                receipt.GraphFingerprint != graphFingerprint || receipt.TaskId != pin.TaskId || receipt.AttemptId != pin.AttemptId ||
                receipt.Revision != pin.Revision || receipt.ResultDigest != pin.ResultDigest)
                throw new InvalidDataException("A task input pin lacks an exact accepted result receipt.");
        }
    }

    public static bool SameInputs(TaskAttemptInputs? left, TaskAttemptInputs? right) => Same(left, right);

    private static bool Same<T>(T left, T right) => JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);
    private static string InputDigest(TaskAttemptInputs inputs) => TaskInputIdentity.Digest(inputs);
    private static bool IsDigest(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;
    private static void RequireIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl))
            throw new InvalidDataException("Task input identities must be nonblank, unpadded and contain no control characters.");
    }
}
