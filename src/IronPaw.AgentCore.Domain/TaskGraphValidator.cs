using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed record TaskGraphValidationError(string Code, string? TaskId,
    string? RelatedTaskId, string Message);

public sealed record TaskGraphValidationResult(bool IsValid,
    ImmutableArray<TaskGraphValidationError> Errors, WorkflowTaskGraph? Graph,
    ImmutableArray<string> TopologicalTaskIds, string? DefinitionFingerprint = null);

/// <summary>
/// Checks declarations and freezes mutable policy collections. Validation does not authorize
/// resource access, execute tasks, or establish the truth of verification evidence.
/// </summary>
public static class TaskGraphValidator
{
    private const int MaximumPolicyDepth = 64;

    public static TaskGraphValidationResult Validate(WorkflowTaskGraph? graph)
    {
        var errors = ImmutableArray.CreateBuilder<TaskGraphValidationError>();
        void Error(string code, string? taskId, string? relatedTaskId, string message) =>
            errors.Add(new(code, taskId, relatedTaskId, message));

        if (graph is null)
        {
            Error("GraphRequired", null, null, "A task graph is required.");
            return new(false, errors.ToImmutable(), null, []);
        }
        if (!IsIdentity(graph.GraphId)) Error("InvalidGraphId", null, null, "Graph ID must be nonblank and have no surrounding whitespace.");
        if (string.IsNullOrWhiteSpace(graph.Objective)) Error("InvalidObjective", null, null, "The graph objective is required.");
        if (graph.SchemaVersion != 1) Error("UnsupportedSchemaVersion", null, null, "Only task graph schema version 1 is supported.");
        if (graph.Tasks.IsDefaultOrEmpty)
        {
            Error("TasksRequired", null, null, "A task graph must contain at least one task.");
            return new(false, errors.ToImmutable(), null, []);
        }

        var tasks = ImmutableArray.CreateBuilder<WorkflowTaskDefinition>(graph.Tasks.Length);
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < graph.Tasks.Length; index++)
        {
            var task = graph.Tasks[index];
            if (task is null)
            {
                Error("TaskRequired", null, null, $"Task at index {index} is null.");
                continue;
            }
            if (!IsIdentity(task.TaskId)) Error("InvalidTaskId", task.TaskId, null, "Task ID must be nonblank and have no surrounding whitespace.");
            else if (!indices.TryAdd(task.TaskId, index)) Error("DuplicateTaskId", task.TaskId, null, "Task ID is declared more than once.");
            if (string.IsNullOrWhiteSpace(task.Objective)) Error("InvalidObjective", task.TaskId, null, "The task objective is required.");
            if (task.AgentId is not null && !IsIdentity(task.AgentId)) Error("InvalidAgentId", task.TaskId, null, "Agent ID must be nonblank and have no surrounding whitespace.");
            if (task.Dependencies.IsDefault) Error("InvalidCollection", task.TaskId, null, "Dependencies must be initialized.");
            ValidateBudget(task, Error);
            var policy = SnapshotPolicy(task.ToolPolicy, task.TaskId, Error);
            ValidateResources(task.Resources, task.TaskId, Error);
            ValidateArtifactsAndCriteria(task, Error);
            tasks.Add(task with { ToolPolicy = policy ?? new(ToolProfile.Minimal) });
        }

        var indegrees = new int[graph.Tasks.Length];
        var dependents = new List<int>[graph.Tasks.Length];
        for (var index = 0; index < graph.Tasks.Length; index++) dependents[index] = [];
        for (var index = 0; index < graph.Tasks.Length; index++)
        {
            var task = graph.Tasks[index];
            if (task is null || task.Dependencies.IsDefault) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in task.Dependencies)
            {
                if (!IsIdentity(dependency))
                {
                    Error("InvalidDependencyId", task.TaskId, dependency, "Dependency ID must be nonblank and have no surrounding whitespace.");
                    continue;
                }
                if (!seen.Add(dependency))
                {
                    Error("DuplicateDependency", task.TaskId, dependency, "A dependency is declared more than once.");
                    continue;
                }
                if (string.Equals(task.TaskId, dependency, StringComparison.Ordinal))
                {
                    Error("SelfDependency", task.TaskId, dependency, "A task cannot depend on itself.");
                    continue;
                }
                if (!indices.TryGetValue(dependency, out var dependencyIndex))
                {
                    Error("UnknownDependency", task.TaskId, dependency, "The dependency is not declared in this graph.");
                    continue;
                }
                indegrees[index]++;
                dependents[dependencyIndex].Add(index);
            }
        }

        // Reconsider all ready nodes by declaration order, including newly unblocked nodes.
        var ready = new PriorityQueue<int, int>();
        for (var index = 0; index < graph.Tasks.Length; index++)
            if (graph.Tasks[index] is not null && indegrees[index] == 0) ready.Enqueue(index, index);
        var order = ImmutableArray.CreateBuilder<string>(graph.Tasks.Length);
        var visited = new bool[graph.Tasks.Length];
        while (ready.TryDequeue(out var index, out _))
        {
            visited[index] = true;
            order.Add(graph.Tasks[index].TaskId);
            foreach (var dependent in dependents[index])
                if (--indegrees[dependent] == 0) ready.Enqueue(dependent, dependent);
        }
        for (var index = 0; index < graph.Tasks.Length; index++)
            if (graph.Tasks[index] is { } task && !visited[index])
                Error("DependencyCycle", task.TaskId, null, "The task is part of, or blocked by, a dependency cycle.");

        if (errors.Count != 0) return new(false, errors.ToImmutable(), null, []);
        var snapshot = graph with { Tasks = tasks.ToImmutable() };
        // Explicit depth accommodates the bounded policy chain plus graph/task wrapper objects.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions { MaxDepth = 128 });
        var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new(true, [], snapshot, order.ToImmutable(), fingerprint);
    }

    private static bool IsIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static void ValidateBudget(WorkflowTaskDefinition task,
        Action<string, string?, string?, string> error)
    {
        if (task.Budget is { } budget && (budget.MaxModelCalls < 1 || budget.MaxToolCalls < 0 ||
            budget.MaxTotalTokens is <= 0 || budget.MaxExecutionTime is { } time && time <= TimeSpan.Zero))
            error("InvalidBudget", task.TaskId, null, "Task budgets require at least one model call, nonnegative tool calls, and positive optional token and time limits.");
    }

    private static ToolSelectionPolicy? SnapshotPolicy(ToolSelectionPolicy? policy, string? taskId,
        Action<string, string?, string?, string> error)
    {
        if (policy is null)
        {
            error("InvalidToolPolicy", taskId, null, "Tool policy is required.");
            return null;
        }
        var ancestors = new List<ToolSelectionPolicy>();
        var seen = new HashSet<ToolSelectionPolicy>(ReferenceEqualityComparer.Instance);
        for (var current = policy; current is not null; current = current.InheritedPolicy)
        {
            if (!seen.Add(current))
            {
                error("PolicyCycle", taskId, null, "Inherited tool policies contain a reference cycle.");
                return null;
            }
            if (ancestors.Count >= MaximumPolicyDepth)
            {
                error("PolicyDepthExceeded", taskId, null, $"Inherited tool policies exceed the maximum depth of {MaximumPolicyDepth}.");
                return null;
            }
            if (!Enum.IsDefined(current.Profile) || current.SubagentDepth < 0)
                error("InvalidToolPolicy", taskId, null, "Tool profile must be known and subagent depth must be nonnegative.");
            // Freeze caller-owned collections once, then validate precisely the values we retain.
            var frozen = current with
            {
                AllowList = SnapshotNames(current.AllowList, "allow list", taskId, error),
                DenyList = SnapshotNames(current.DenyList, "deny list", taskId, error),
                AllowedGroups = SnapshotNames(current.AllowedGroups, "allowed groups", taskId, error),
                InheritedPolicy = null
            };
            ValidatePolicyNames(frozen.AllowList, "allow list", taskId, error);
            ValidatePolicyNames(frozen.DenyList, "deny list", taskId, error);
            ValidatePolicyNames(frozen.AllowedGroups, "allowed groups", taskId, error);
            ancestors.Add(frozen);
        }
        ToolSelectionPolicy? snapshot = null;
        for (var index = ancestors.Count - 1; index >= 0; index--)
        {
            var current = ancestors[index];
            snapshot = current with { InheritedPolicy = snapshot };
        }
        return snapshot;
    }

    private static void ValidatePolicyNames(IReadOnlyCollection<string>? values, string name, string? taskId,
        Action<string, string?, string?, string> error)
    {
        if (values is null) return;
        if (values is ImmutableArray<string> { IsDefault: true })
        {
            error("InvalidCollection", taskId, null, $"The tool policy {name} must be initialized.");
            return;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
            if (!IsIdentity(value) || !seen.Add(value))
                error("InvalidToolPolicy", taskId, null, $"The tool policy {name} contains a blank, whitespace-padded, or duplicate entry.");
    }

    private static IReadOnlyCollection<string>? SnapshotNames(IReadOnlyCollection<string>? values,
        string name, string? taskId, Action<string, string?, string?, string> error)
    {
        if (values is null) return null;
        if (values is ImmutableArray<string> { IsDefault: true })
        {
            error("InvalidCollection", taskId, null, $"The tool policy {name} must be initialized.");
            return [];
        }
        try { return values.ToImmutableArray(); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            error("InvalidToolPolicy", taskId, null, $"The tool policy {name} could not be copied to an immutable snapshot.");
            return [];
        }
    }

    private static void ValidateResources(TaskResourceScope? resources, string? taskId,
        Action<string, string?, string?, string> error)
    {
        if (resources is null)
        {
            error("InvalidResource", taskId, null, "Resource declarations are required.");
            return;
        }
        ValidateNames(resources.ReadPaths, "Readable paths", taskId, "InvalidResource", "DuplicateResource", error);
        ValidateNames(resources.WritePaths, "Writable paths", taskId, "InvalidResource", "DuplicateResource", error);
        ValidateNames(resources.NetworkHosts, "Network hosts", taskId, "InvalidResource", "DuplicateResource", error);
    }

    private static void ValidateNames(ImmutableArray<string> values, string name, string? taskId,
        string invalidCode, string duplicateCode, Action<string, string?, string?, string> error)
    {
        if (values.IsDefault)
        {
            error("InvalidCollection", taskId, null, $"{name} must be initialized.");
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!IsIdentity(value)) error(invalidCode, taskId, null, $"{name} contains a blank or whitespace-padded entry.");
            else if (!seen.Add(value)) error(duplicateCode, taskId, null, $"{name} contains a duplicate entry.");
        }
    }

    private static void ValidateArtifactsAndCriteria(WorkflowTaskDefinition task,
        Action<string, string?, string?, string> error)
    {
        var artifactIds = new HashSet<string>(StringComparer.Ordinal);
        if (task.ExpectedArtifacts.IsDefault) error("InvalidCollection", task.TaskId, null, "Expected artifacts must be initialized.");
        else foreach (var artifact in task.ExpectedArtifacts)
        {
            if (artifact is null)
            {
                error("InvalidArtifact", task.TaskId, null, "Artifact declarations must not be null.");
                continue;
            }
            if (!IsIdentity(artifact.ArtifactId) || string.IsNullOrWhiteSpace(artifact.Description) ||
                !Enum.IsDefined(artifact.Kind) || artifact.RelativePath is not null && !IsRelativeArtifactPath(artifact.RelativePath))
                error("InvalidArtifact", task.TaskId, null, "An artifact requires an ID, description, known kind, and a safe relative path if supplied.");
            if (IsIdentity(artifact.ArtifactId) && !artifactIds.Add(artifact.ArtifactId))
                error("DuplicateArtifactId", task.TaskId, null, "Artifact ID is declared more than once within the task.");
        }
        var criterionIds = new HashSet<string>(StringComparer.Ordinal);
        if (task.AcceptanceCriteria.IsDefault) error("InvalidCollection", task.TaskId, null, "Acceptance criteria must be initialized.");
        else foreach (var criterion in task.AcceptanceCriteria)
        {
            if (criterion is null)
            {
                error("InvalidCriterion", task.TaskId, null, "Acceptance criteria must not be null.");
                continue;
            }
            if (!IsIdentity(criterion.CriterionId) || string.IsNullOrWhiteSpace(criterion.Description))
                error("InvalidCriterion", task.TaskId, null, "A criterion requires an ID and description.");
            if (IsIdentity(criterion.CriterionId) && !criterionIds.Add(criterion.CriterionId))
                error("DuplicateCriterionId", task.TaskId, null, "Criterion ID is declared more than once within the task.");
            ValidateNames(criterion.ArtifactIds, "Criterion artifact references", task.TaskId,
                "InvalidCriterion", "DuplicateArtifactReference", error);
            if (!criterion.ArtifactIds.IsDefault)
                foreach (var artifactId in criterion.ArtifactIds)
                    if (IsIdentity(artifactId) && !artifactIds.Contains(artifactId))
                        error("UnknownArtifactReference", task.TaskId, null, "A criterion refers to an artifact not declared by this task.");
        }
    }

    private static bool IsRelativeArtifactPath(string value)
    {
        if (!IsIdentity(value) || value[0] is '/' or '\\' || value.Any(char.IsControl) || value.Contains(':')) return false;
        // Check both separators on every OS; no filesystem or host-specific path resolution occurs here.
        var segments = value.Split(['/', '\\']);
        return segments.All(segment => segment.Length > 0 && segment is not "." and not "..");
    }
}
