using System.Collections.Immutable;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

/// <summary>Validates the supported host boundary without touching the filesystem or contacting providers.</summary>
public static class AgentTaskBindingValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 256 };

    public static TaskGraphAgentBinding Validate(WorkflowTaskGraph graph, CallerIdentity caller,
        TaskGraphAgentBinding binding)
    {
        var definition = RequireGraph(graph);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(binding);
        if (!Enum.IsDefined(binding.DependencyMode))
            throw new ArgumentException("The dependency mode is unsupported.", nameof(binding));
        RequireName(caller.Id, nameof(caller));
        if (binding.RootRunId != AgentTaskIdentity.RootRunId(definition.Graph!.GraphId).Value)
            throw new ArgumentException("The execution binding must identify this graph's deterministic root workflow.", nameof(binding));
        RequireName(binding.Provider, nameof(binding.Provider));
        RequireName(binding.Model, nameof(binding.Model));
        RequireName(binding.AgentId, nameof(binding.AgentId));
        if (binding.MaxRounds < 1 || binding.MaxOutputTokens < 1)
            throw new ArgumentException("Agent round and output limits must be positive.", nameof(binding));
        if (binding.SharedBudget is not { } budget || budget.MaxModelCalls < 1 || budget.MaxToolCalls < 0 ||
            budget.MaxTotalTokens is <= 0 || budget.MaxExecutionTime is { } time && time <= TimeSpan.Zero)
            throw new ArgumentException("The shared budget must allow a model call and have nonnegative call and positive optional token/time limits.", nameof(binding));
        if (binding.BaseCommit is not { Length: 40 or 64 } commit ||
            !commit.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            throw new ArgumentException("The base revision must be a full hexadecimal Git commit identity.", nameof(binding));

        var source = CanonicalRoot(binding.SourceWorkspaceRoot, nameof(binding.SourceWorkspaceRoot));
        var workspaceStore = CanonicalRoot(binding.WorkspaceStoreRoot, nameof(binding.WorkspaceStoreRoot));
        if (Contains(source, workspaceStore) || Contains(workspaceStore, source))
            throw new ArgumentException("Source and managed workspace roots must be disjoint.", nameof(binding));
        var policyValidation = RequireGraph(new WorkflowTaskGraph("binding-policy", "Freeze the accepted host policy",
            [new WorkflowTaskDefinition("policy", "Freeze the accepted host policy") { ToolPolicy = binding.RootToolPolicy }]));
        var frozenPolicy = policyValidation.Graph!.Tasks[0].ToolPolicy with { SenderIsOwner = caller.IsOwner };

        foreach (var task in definition.Graph!.Tasks)
        {
            if (task.AgentId is not null && task.AgentId != binding.AgentId)
                throw new NotSupportedException("This host supports one configured agent per accepted graph.");
            if (task.Budget is { MaxTotalTokens: not null } or { MaxExecutionTime: not null })
                throw new NotSupportedException("Task-local token/time limits are unsupported; configure these limits on the shared root budget.");
            var resources = task.Resources;
            if (!resources.NetworkHosts.IsEmpty || resources.AllowProcessExecution || resources.AllowCredentialAccess || resources.AllowExternalMutations)
                throw new NotSupportedException("This host supports scoped file access only; network, processes, credentials and external mutations are unsupported.");
            foreach (var path in resources.ReadPaths.Concat(resources.WritePaths)) RequireRelativePath(path, allowRoot: true);
            foreach (var artifact in task.ExpectedArtifacts)
            {
                if (artifact.Kind is not (TaskArtifactKind.File or TaskArtifactKind.Report or TaskArtifactKind.Patch))
                    throw new NotSupportedException("Only file, report and patch artifacts are supported.");
                RequireRelativePath(artifact.RelativePath, allowRoot: false);
            }
        }
        if (binding.ArtifactVerificationRules.IsDefault)
            throw new ArgumentException("Artifact verification rules must be initialized.", nameof(binding));
        var seen = new HashSet<(string, string)>();
        foreach (var rule in binding.ArtifactVerificationRules)
        {
            if (rule is null || !seen.Add((rule.TaskId, rule.CriterionId)))
                throw new ArgumentException("Artifact verification mappings must be nonnull and unique by task and criterion.", nameof(binding));
            var task = definition.Graph.Tasks.SingleOrDefault(candidate => candidate.TaskId == rule.TaskId)
                ?? throw new ArgumentException("An artifact verification mapping names an unknown task.", nameof(binding));
            var criterion = task.AcceptanceCriteria.SingleOrDefault(candidate => candidate.CriterionId == rule.CriterionId)
                ?? throw new ArgumentException("An artifact verification mapping names an unknown criterion.", nameof(binding));
            if (criterion.ArtifactIds.IsDefaultOrEmpty || criterion.ArtifactIds.Any(id =>
                !task.ExpectedArtifacts.Any(artifact => artifact.ArtifactId == id && artifact.Required && artifact.RelativePath is not null)))
                throw new ArgumentException("An artifact-integrity criterion must identify declared, required file artifacts with paths.", nameof(binding));
        }
        return TaskVerificationValidator.Validate(definition.Graph!, caller, binding with { SourceWorkspaceRoot = source, WorkspaceStoreRoot = workspaceStore,
            BaseCommit = commit.ToLowerInvariant(), RootToolPolicy = frozenPolicy,
            ArtifactVerificationRules = binding.ArtifactVerificationRules.ToImmutableArray() });
    }

    public static AgentRunRequest CreateRootRequest(WorkflowTaskGraph graph, CallerIdentity caller,
        TaskGraphAgentBinding binding)
    {
        var accepted = Validate(graph, caller, binding);
        return new AgentRunRequest(AgentTaskIdentity.RootSession(graph.GraphId), graph.Objective,
            accepted.Provider, accepted.Model, accepted.RootToolPolicy, caller,
            SystemPromptOverride: accepted.SystemPrompt, MaxOutputTokens: accepted.MaxOutputTokens,
            MaxRounds: accepted.MaxRounds, AgentId: accepted.AgentId, Budget: accepted.SharedBudget,
            RequestedRunId: AgentTaskIdentity.RootRunId(graph.GraphId));
    }

    public static AgentRunRequest CreateChildRequest(WorkflowTaskGraph graph, CallerIdentity caller,
        TaskGraphAgentBinding binding, WorkflowTaskDefinition task, string attemptId, TaskAttemptInputs? inputs = null)
    {
        var accepted = Validate(graph, caller, binding);
        var definition = RequireGraph(graph);
        ArgumentNullException.ThrowIfNull(task);
        RequireName(attemptId, nameof(attemptId));
        var canonical = definition.Graph!.Tasks.SingleOrDefault(candidate => candidate.TaskId == task.TaskId);
        if (canonical is null || JsonSerializer.Serialize(canonical, JsonOptions) != JsonSerializer.Serialize(task, JsonOptions))
            throw new ArgumentException("The task must match the graph's accepted definition.", nameof(task));
        var objective = canonical.Objective;
        if (accepted.DependencyMode == TaskDependencyMode.AcceptedChanges)
        {
            if (inputs is null) throw new InvalidDataException("Accepted-change task execution requires frozen inputs.");
            TaskDependencyResolver.ValidateShape(inputs, definition.DefinitionFingerprint!, task.TaskId, attemptId, accepted.BaseCommit);
            objective += "\n\nHost-prepared task inputs:\n" + JsonSerializer.Serialize(new
            {
                inputs.Digest,
                Predecessors = inputs.Dependencies.Select(pin => new { pin.TaskId, pin.AttemptId, pin.ResultDigest, pin.Revision })
            }, JsonOptions);
        }
        else if (inputs is not null)
            throw new InvalidDataException("Legacy base-commit tasks cannot acquire predecessor inputs.");
        var policy = AppendPolicy(canonical.ToolPolicy, accepted.RootToolPolicy) with
        {
            SenderIsOwner = caller.IsOwner,
            SubagentDepth = Math.Max(1, canonical.ToolPolicy.SubagentDepth)
        };
        var localBudget = canonical.Budget ?? new WorkflowBudget(accepted.SharedBudget.MaxModelCalls, accepted.SharedBudget.MaxToolCalls);
        var rootId = AgentTaskIdentity.RootRunId(graph.GraphId);
        return new AgentRunRequest(AgentTaskIdentity.ChildSession(definition.DefinitionFingerprint!, task.TaskId, attemptId),
            objective, accepted.Provider, accepted.Model, policy, caller,
            SystemPromptOverride: accepted.SystemPrompt, MaxOutputTokens: accepted.MaxOutputTokens,
            MaxRounds: accepted.MaxRounds, AgentId: accepted.AgentId,
            ParentRunId: rootId, ParentSessionKey: AgentTaskIdentity.RootSession(graph.GraphId), RootRunId: rootId,
            Budget: new WorkflowBudget(localBudget.MaxModelCalls, localBudget.MaxToolCalls),
            RequestedRunId: AgentTaskIdentity.ChildRunId(definition.DefinitionFingerprint!, task.TaskId, attemptId));
    }

    private static ToolSelectionPolicy AppendPolicy(ToolSelectionPolicy task, ToolSelectionPolicy root) =>
        task with { InheritedPolicy = task.InheritedPolicy is null ? root : AppendPolicy(task.InheritedPolicy, root) };

    private static TaskGraphValidationResult RequireGraph(WorkflowTaskGraph graph)
    {
        var validation = TaskGraphValidator.Validate(graph);
        if (!validation.IsValid)
            throw new ArgumentException("The task graph or host policy is invalid: " + string.Join(" ", validation.Errors.Select(error => error.Message)), nameof(graph));
        return validation;
    }

    private static void RequireName(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("Identities must be nonblank, unpadded and contain no control characters.", name);
    }

    private static string CanonicalRoot(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || path != path.Trim() || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Workspace roots must be absolute paths without surrounding whitespace or control characters.", name);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool Contains(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return candidate.Equals(root, comparison) || candidate.StartsWith(prefix, comparison);
    }

    private static void RequireRelativePath(string? path, bool allowRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || path != path.Trim() || path.Any(char.IsControl) ||
            path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':') || path.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            throw new ArgumentException("Resource and artifact paths must be safe relative paths.");
        if (path == "." && allowRoot) return;
        var segments = path.Split(['/', '\\']);
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".." ||
            segment.Equals(".git", StringComparison.OrdinalIgnoreCase) || segment != segment.Trim() || segment.EndsWith('.') || IsReservedName(segment)))
            throw new ArgumentException("Relative paths cannot traverse parents, metadata, empty segments or ambiguous names.");
    }

    private static bool IsReservedName(string segment)
    {
        var stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            stem[3] is >= '0' and <= '9';
    }
}
