using System.Collections.Immutable;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

public sealed class AgentTaskBindingValidatorTests
{
    private static readonly CallerIdentity Owner = new("owner", "Owner", true);
    private static WorkflowTaskGraph Graph(WorkflowTaskDefinition? task = null) =>
        new("graph", "Implement the graph objective", [task ?? new("task", "Implement the task")]);
    private static TaskGraphAgentBinding Binding(WorkflowTaskGraph? graph = null) => new(
        AgentTaskIdentity.RootRunId((graph ?? Graph()).GraphId).Value,
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ironpaw-binding-source")), new string('A', 40),
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ironpaw-binding-managed")),
        "provider", "model", new(ToolProfile.Coding), new(12, 24, 40_000, TimeSpan.FromMinutes(5)),
        "graph-agent", "Frozen host prompt", 6, 1024);

    [Fact]
    public void Validate_NormalizesAndFreezesHostPolicyWithoutFilesystemAccess()
    {
        var allow = new List<string> { "read_file", "write_file" };
        var deny = new List<string> { "invoke_agent" };
        var binding = Binding() with
        {
            SourceWorkspaceRoot = Path.Combine(Binding().SourceWorkspaceRoot, "folder", ".."),
            RootToolPolicy = new(ToolProfile.Coding, AllowList: allow,
                InheritedPolicy: new(ToolProfile.Coding, DenyList: deny))
        };

        var accepted = AgentTaskBindingValidator.Validate(Graph(), Owner, binding);
        allow.Clear();
        deny.Clear();

        Assert.Equal(new string('a', 40), accepted.BaseCommit);
        Assert.Equal(Binding().SourceWorkspaceRoot, accepted.SourceWorkspaceRoot);
        Assert.True(accepted.RootToolPolicy.SenderIsOwner);
        Assert.Equal(new[] { "read_file", "write_file" }, accepted.RootToolPolicy.AllowList);
        Assert.Equal(new[] { "invoke_agent" }, accepted.RootToolPolicy.InheritedPolicy!.DenyList);
    }

    [Fact]
    public void RootAndChildRequests_UseAcceptedConfigurationAndStableIndependentIdentities()
    {
        var graph = Graph();
        var binding = Binding();
        var root = AgentTaskBindingValidator.CreateRootRequest(graph, Owner, binding);
        var child = AgentTaskBindingValidator.CreateChildRequest(graph, Owner, binding, graph.Tasks[0], "attempt-1");
        var replay = AgentTaskBindingValidator.CreateChildRequest(graph, Owner, binding, graph.Tasks[0], "attempt-1");
        var retry = AgentTaskBindingValidator.CreateChildRequest(graph, Owner, binding, graph.Tasks[0], "attempt-2");

        Assert.Equal(AgentTaskIdentity.RootRunId(graph.GraphId), root.RequestedRunId);
        Assert.Equal(AgentTaskIdentity.RootSession(graph.GraphId), root.SessionKey);
        Assert.Equal(graph.Objective, root.UserInput);
        Assert.Equal(binding.SharedBudget, root.Budget);
        Assert.Null(root.ParentRunId);
        Assert.Null(root.RootRunId);
        Assert.Equal(root.RequestedRunId, child.ParentRunId);
        Assert.Equal(root.RequestedRunId, child.RootRunId);
        Assert.Equal(root.SessionKey, child.ParentSessionKey);
        Assert.Equal(child.RequestedRunId, replay.RequestedRunId);
        Assert.Equal(child.SessionKey, replay.SessionKey);
        Assert.NotEqual(child.RequestedRunId, retry.RequestedRunId);
        Assert.NotEqual(root.SessionKey, child.SessionKey);
        Assert.Equal(binding.Provider, child.Provider);
        Assert.Equal(binding.Model, child.Model);
        Assert.Equal(binding.AgentId, child.AgentId);
        Assert.Equal(binding.SystemPrompt, child.SystemPromptOverride);
        Assert.Equal(binding.MaxRounds, child.MaxRounds);
        Assert.Equal(binding.MaxOutputTokens, child.MaxOutputTokens);
        Assert.Equal(new WorkflowBudget(12, 24), child.Budget);
        Assert.True(child.ToolPolicy.SenderIsOwner);
        Assert.True(child.ToolPolicy.SubagentDepth > 0);
    }

    [Fact]
    public void ChildRequest_PreservesAllTaskAndRootAncestorsAndFreezesMutableLists()
    {
        var taskDeny = new List<string> { "write_file" };
        var rootDeny = new List<string> { "read_file" };
        var task = new WorkflowTaskDefinition("task", "Implement")
        {
            ToolPolicy = new(ToolProfile.Coding, DenyList: taskDeny,
                InheritedPolicy: new(ToolProfile.Minimal, DenyList: ["forget_memory"])),
            Budget = new(2, 3)
        };
        var graph = Graph(task);
        var binding = Binding(graph) with { RootToolPolicy = new(ToolProfile.Coding, DenyList: rootDeny,
            InheritedPolicy: new(ToolProfile.Full, DenyList: ["web_navigate"])) };

        var child = AgentTaskBindingValidator.CreateChildRequest(graph, CallerIdentity.Authenticated("caller", "Caller"), binding, task, "attempt");
        taskDeny.Clear();
        rootDeny.Clear();

        Assert.False(child.ToolPolicy.SenderIsOwner);
        Assert.Equal(new[] { "write_file" }, child.ToolPolicy.DenyList);
        var ancestor = child.ToolPolicy.InheritedPolicy!;
        Assert.Equal(new[] { "forget_memory" }, ancestor.DenyList);
        Assert.Equal(new[] { "read_file" }, ancestor.InheritedPolicy!.DenyList);
        Assert.Equal(new[] { "web_navigate" }, ancestor.InheritedPolicy.InheritedPolicy!.DenyList);
        Assert.Null(ancestor.InheritedPolicy.InheritedPolicy.InheritedPolicy);
        Assert.Equal(new WorkflowBudget(2, 3), child.Budget);
    }

    [Fact]
    public void ChildRequest_RejectsDefinitionDriftAndGraphChangesChangeIdentity()
    {
        var graph = Graph();
        var changedTask = graph.Tasks[0] with { Objective = "A changed objective" };
        Assert.Throws<ArgumentException>(() => AgentTaskBindingValidator.CreateChildRequest(graph, Owner, Binding(), changedTask, "attempt"));
        var original = AgentTaskBindingValidator.CreateChildRequest(graph, Owner, Binding(), graph.Tasks[0], "attempt");
        var changed = graph with { Tasks = [changedTask] };
        var changedChild = AgentTaskBindingValidator.CreateChildRequest(changed, Owner, Binding(), changedTask, "attempt");
        Assert.NotEqual(original.RequestedRunId, changedChild.RequestedRunId);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("/rooted")]
    [InlineData("\\rooted")]
    [InlineData("C:\\rooted")]
    [InlineData("a/.git/config")]
    [InlineData("a\\.GIT\\config")]
    [InlineData("file:stream")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("file.")]
    [InlineData("file ")]
    [InlineData("file\nname")]
    [InlineData("file?name")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("folder/LPT0.log")]
    [InlineData("folder/com9")]
    public void Validate_RejectsUnsafeRelativeResourcePaths(string path)
    {
        var graph = Graph(new("task", "Implement") { Resources = new() { ReadPaths = [path] } });
        Assert.Throws<ArgumentException>(() => AgentTaskBindingValidator.Validate(graph, Owner, Binding()));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("src")]
    [InlineData("src/file.cs")]
    [InlineData("src\\file.cs")]
    public void Validate_AcceptsSupportedRelativeResourcePaths(string path)
    {
        var graph = Graph(new("task", "Implement") { Resources = new() { ReadPaths = [path], WritePaths = [path] } });
        AgentTaskBindingValidator.Validate(graph, Owner, Binding());
    }

    [Fact]
    public void Validate_RejectsOverlappingAndRelativeWorkspaceRootsButAllowsSiblingPrefixes()
    {
        var binding = Binding();
        Assert.Throws<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph(), Owner, binding with { SourceWorkspaceRoot = "relative" }));
        Assert.Throws<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph(), Owner, binding with { WorkspaceStoreRoot = binding.SourceWorkspaceRoot }));
        Assert.Throws<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph(), Owner, binding with { WorkspaceStoreRoot = Path.Combine(binding.SourceWorkspaceRoot, "managed") }));
        Assert.Throws<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph(), Owner, binding with { SourceWorkspaceRoot = Path.Combine(binding.WorkspaceStoreRoot, "source") }));
        AgentTaskBindingValidator.Validate(Graph(), Owner, binding with { WorkspaceStoreRoot = binding.SourceWorkspaceRoot + "-managed" });
    }

    [Theory]
    [InlineData("network")]
    [InlineData("process")]
    [InlineData("credentials")]
    [InlineData("mutations")]
    public void Validate_RejectsUnsupportedCapabilities(string capability)
    {
        var scope = capability switch
        {
            "network" => new TaskResourceScope { NetworkHosts = ["example.test"] },
            "process" => new TaskResourceScope { AllowProcessExecution = true },
            "credentials" => new TaskResourceScope { AllowCredentialAccess = true },
            _ => new TaskResourceScope { AllowExternalMutations = true }
        };
        var graph = Graph(new("task", "Implement") { Resources = scope });
        Assert.Throws<NotSupportedException>(() => AgentTaskBindingValidator.Validate(graph, Owner, Binding()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_RejectsUnenforcedTaskLocalTokenAndTimeBudgets(bool tokenLimit)
    {
        var budget = tokenLimit ? new WorkflowBudget(MaxTotalTokens: 100) : new WorkflowBudget(MaxExecutionTime: TimeSpan.FromMinutes(1));
        var graph = Graph(new("task", "Implement") { Budget = budget });
        Assert.Throws<NotSupportedException>(() => AgentTaskBindingValidator.Validate(graph, Owner, Binding()));
    }

    [Fact]
    public void Validate_RejectsAgentOverrideAndInvalidHostConfiguration()
    {
        var binding = Binding();
        var graph = Graph(new("task", "Implement") { AgentId = "other-agent" });
        Assert.Throws<NotSupportedException>(() => AgentTaskBindingValidator.Validate(graph, Owner, binding));
        foreach (var invalid in new[]
        {
            binding with { RootRunId = "different-root" }, binding with { Provider = " provider" },
            binding with { Model = "" }, binding with { AgentId = "agent\n" }, binding with { BaseCommit = "main" },
            binding with { BaseCommit = new string('z', 40) }, binding with { MaxRounds = 0 },
            binding with { MaxOutputTokens = 0 }, binding with { SharedBudget = new(0) },
            binding with { SharedBudget = new(MaxToolCalls: -1) }, binding with { SharedBudget = new(MaxTotalTokens: 0) },
            binding with { SharedBudget = new(MaxExecutionTime: TimeSpan.Zero) }
        })
            Assert.Throws<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph(), Owner, invalid));
        Assert.Throws<ArgumentException>(() => AgentTaskBindingValidator.CreateChildRequest(Graph(), Owner, binding, Graph().Tasks[0], " attempt"));
    }

    [Fact]
    public void Validate_ArtifactMappingsAreExplicitAndOnlyReferenceRequiredFileOutputs()
    {
        var task = new WorkflowTaskDefinition("task", "Implement")
        {
            ExpectedArtifacts = [new("report", "Report", TaskArtifactKind.Report, "reports/result.md")],
            AcceptanceCriteria = [new("integrity", "Host explicitly maps this criterion") { ArtifactIds = ["report"] }, new("semantic", "Review the work")]
        };
        var graph = Graph(task);
        var binding = Binding() with { ArtifactVerificationRules = [new("task", "integrity")] };
        Assert.Single(AgentTaskBindingValidator.Validate(graph, Owner, binding).ArtifactVerificationRules);
        Assert.Empty(AgentTaskBindingValidator.Validate(graph, Owner, Binding()).ArtifactVerificationRules);
        foreach (var rules in new ImmutableArray<TaskArtifactVerificationRule>[]
        {
            [new("task", "integrity"), new("task", "integrity")], [new("unknown", "integrity")],
            [new("task", "unknown")], [new("task", "semantic")], default
        })
            Assert.Throws<ArgumentException>(() => AgentTaskBindingValidator.Validate(graph, Owner, binding with { ArtifactVerificationRules = rules }));
        var optionalGraph = Graph(task with { ExpectedArtifacts = [task.ExpectedArtifacts[0] with { Required = false }] });
        Assert.Throws<ArgumentException>(() => AgentTaskBindingValidator.Validate(optionalGraph, Owner, binding));
    }

    [Theory]
    [InlineData(TaskArtifactKind.File)]
    [InlineData(TaskArtifactKind.Report)]
    [InlineData(TaskArtifactKind.Patch)]
    public void Validate_AcceptsFileArtifactsWithSafeNonRootPaths(TaskArtifactKind kind)
    {
        var task = new WorkflowTaskDefinition("task", "Implement") { ExpectedArtifacts = [new("output", "Output", kind, "out/result.txt")] };
        AgentTaskBindingValidator.Validate(Graph(task), Owner, Binding());
        Assert.Throws<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph(task with { ExpectedArtifacts = [task.ExpectedArtifacts[0] with { RelativePath = "." }] }), Owner, Binding()));
        Assert.Throws<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph(task with { ExpectedArtifacts = [task.ExpectedArtifacts[0] with { RelativePath = null }] }), Owner, Binding()));
    }

    [Theory]
    [InlineData(TaskArtifactKind.Directory)]
    [InlineData(TaskArtifactKind.Other)]
    public void Validate_RejectsUnsupportedArtifactKinds(TaskArtifactKind kind)
    {
        var graph = Graph(new("task", "Implement") { ExpectedArtifacts = [new("output", "Output", kind, "out")] });
        Assert.Throws<NotSupportedException>(() => AgentTaskBindingValidator.Validate(graph, Owner, Binding()));
    }
}
