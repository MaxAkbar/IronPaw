using System.Collections.Immutable;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

[TestClass]
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

    [TestMethod]
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

        Assert.AreEqual(new string('a', 40), accepted.BaseCommit);
        Assert.AreEqual(Binding().SourceWorkspaceRoot, accepted.SourceWorkspaceRoot);
        Assert.IsTrue(accepted.RootToolPolicy.SenderIsOwner);
        Assert.AreSequenceEqual(new[] { "read_file", "write_file" }, accepted.RootToolPolicy.AllowList);
        Assert.AreSequenceEqual(new[] { "invoke_agent" }, accepted.RootToolPolicy.InheritedPolicy!.DenyList);
    }

    [TestMethod]
    public void RootAndChildRequests_UseAcceptedConfigurationAndStableIndependentIdentities()
    {
        var graph = Graph();
        var binding = Binding();
        var root = AgentTaskBindingValidator.CreateRootRequest(graph, Owner, binding);
        var child = AgentTaskBindingValidator.CreateChildRequest(graph, Owner, binding, graph.Tasks[0], "attempt-1");
        var replay = AgentTaskBindingValidator.CreateChildRequest(graph, Owner, binding, graph.Tasks[0], "attempt-1");
        var retry = AgentTaskBindingValidator.CreateChildRequest(graph, Owner, binding, graph.Tasks[0], "attempt-2");

        Assert.AreEqual(AgentTaskIdentity.RootRunId(graph.GraphId), root.RequestedRunId);
        Assert.AreEqual(AgentTaskIdentity.RootSession(graph.GraphId), root.SessionKey);
        Assert.AreEqual(graph.Objective, root.UserInput);
        Assert.AreEqual(binding.SharedBudget, root.Budget);
        Assert.IsNull(root.ParentRunId);
        Assert.IsNull(root.RootRunId);
        Assert.AreEqual(root.RequestedRunId, child.ParentRunId);
        Assert.AreEqual(root.RequestedRunId, child.RootRunId);
        Assert.AreEqual(root.SessionKey, child.ParentSessionKey);
        Assert.AreEqual(child.RequestedRunId, replay.RequestedRunId);
        Assert.AreEqual(child.SessionKey, replay.SessionKey);
        Assert.AreNotEqual(child.RequestedRunId, retry.RequestedRunId);
        Assert.AreNotEqual(root.SessionKey, child.SessionKey);
        Assert.AreEqual(binding.Provider, child.Provider);
        Assert.AreEqual(binding.Model, child.Model);
        Assert.AreEqual(binding.AgentId, child.AgentId);
        Assert.AreEqual(binding.SystemPrompt, child.SystemPromptOverride);
        Assert.AreEqual(binding.MaxRounds, child.MaxRounds);
        Assert.AreEqual(binding.MaxOutputTokens, child.MaxOutputTokens);
        Assert.AreEqual(new WorkflowBudget(12, 24), child.Budget);
        Assert.IsTrue(child.ToolPolicy.SenderIsOwner);
        Assert.IsTrue(child.ToolPolicy.SubagentDepth > 0);
    }

    [TestMethod]
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
        var binding = Binding(graph) with
        {
            RootToolPolicy = new(ToolProfile.Coding, DenyList: rootDeny,
            InheritedPolicy: new(ToolProfile.Full, DenyList: ["web_navigate"]))
        };

        var child = AgentTaskBindingValidator.CreateChildRequest(graph, CallerIdentity.Authenticated("caller", "Caller"), binding, task, "attempt");
        taskDeny.Clear();
        rootDeny.Clear();

        Assert.IsFalse(child.ToolPolicy.SenderIsOwner);
        Assert.AreSequenceEqual(new[] { "write_file" }, child.ToolPolicy.DenyList);
        var ancestor = child.ToolPolicy.InheritedPolicy!;
        Assert.AreSequenceEqual(new[] { "forget_memory" }, ancestor.DenyList);
        Assert.AreSequenceEqual(new[] { "read_file" }, ancestor.InheritedPolicy!.DenyList);
        Assert.AreSequenceEqual(new[] { "web_navigate" }, ancestor.InheritedPolicy.InheritedPolicy!.DenyList);
        Assert.IsNull(ancestor.InheritedPolicy.InheritedPolicy.InheritedPolicy);
        Assert.AreEqual(new WorkflowBudget(2, 3), child.Budget);
    }

    [TestMethod]
    public void ChildRequest_RejectsDefinitionDriftAndGraphChangesChangeIdentity()
    {
        var graph = Graph();
        var changedTask = graph.Tasks[0] with { Objective = "A changed objective" };
        Assert.ThrowsExactly<ArgumentException>(() => AgentTaskBindingValidator.CreateChildRequest(graph, Owner, Binding(), changedTask, "attempt"));
        var original = AgentTaskBindingValidator.CreateChildRequest(graph, Owner, Binding(), graph.Tasks[0], "attempt");
        var changed = graph with { Tasks = [changedTask] };
        var changedChild = AgentTaskBindingValidator.CreateChildRequest(changed, Owner, Binding(), changedTask, "attempt");
        Assert.AreNotEqual(original.RequestedRunId, changedChild.RequestedRunId);
    }

    [TestMethod]
    [DataRow("../escape")]
    [DataRow("..\\escape")]
    [DataRow("/rooted")]
    [DataRow("\\rooted")]
    [DataRow("C:\\rooted")]
    [DataRow("a/.git/config")]
    [DataRow("a\\.GIT\\config")]
    [DataRow("file:stream")]
    [DataRow("a//b")]
    [DataRow("a/./b")]
    [DataRow("file.")]
    [DataRow("file ")]
    [DataRow("file\nname")]
    [DataRow("file?name")]
    [DataRow("CON")]
    [DataRow("con.txt")]
    [DataRow("folder/LPT0.log")]
    [DataRow("folder/com9")]
    public void Validate_RejectsUnsafeRelativeResourcePaths(string path)
    {
        var graph = Graph(new("task", "Implement") { Resources = new() { ReadPaths = [path] } });
        Assert.ThrowsExactly<ArgumentException>(() => AgentTaskBindingValidator.Validate(graph, Owner, Binding()));
    }

    [TestMethod]
    [DataRow(".")]
    [DataRow("src")]
    [DataRow("src/file.cs")]
    [DataRow("src\\file.cs")]
    public void Validate_AcceptsSupportedRelativeResourcePaths(string path)
    {
        var graph = Graph(new("task", "Implement") { Resources = new() { ReadPaths = [path], WritePaths = [path] } });
        AgentTaskBindingValidator.Validate(graph, Owner, Binding());
    }

    [TestMethod]
    public void Validate_RejectsOverlappingAndRelativeWorkspaceRootsButAllowsSiblingPrefixes()
    {
        var binding = Binding();
        Assert.ThrowsExactly<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph(), Owner, binding with { SourceWorkspaceRoot = "relative" }));
        Assert.ThrowsExactly<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph(), Owner, binding with { WorkspaceStoreRoot = binding.SourceWorkspaceRoot }));
        Assert.ThrowsExactly<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph(), Owner, binding with { WorkspaceStoreRoot = Path.Combine(binding.SourceWorkspaceRoot, "managed") }));
        Assert.ThrowsExactly<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph(), Owner, binding with { SourceWorkspaceRoot = Path.Combine(binding.WorkspaceStoreRoot, "source") }));
        AgentTaskBindingValidator.Validate(Graph(), Owner, binding with { WorkspaceStoreRoot = binding.SourceWorkspaceRoot + "-managed" });
    }

    [TestMethod]
    [DataRow("network")]
    [DataRow("process")]
    [DataRow("credentials")]
    [DataRow("mutations")]
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
        Assert.ThrowsExactly<NotSupportedException>(() => AgentTaskBindingValidator.Validate(graph, Owner, Binding()));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Validate_RejectsUnenforcedTaskLocalTokenAndTimeBudgets(bool tokenLimit)
    {
        var budget = tokenLimit ? new WorkflowBudget(MaxTotalTokens: 100) : new WorkflowBudget(MaxExecutionTime: TimeSpan.FromMinutes(1));
        var graph = Graph(new("task", "Implement") { Budget = budget });
        Assert.ThrowsExactly<NotSupportedException>(() => AgentTaskBindingValidator.Validate(graph, Owner, Binding()));
    }

    [TestMethod]
    public void Validate_RejectsAgentOverrideAndInvalidHostConfiguration()
    {
        var binding = Binding();
        var graph = Graph(new("task", "Implement") { AgentId = "other-agent" });
        Assert.ThrowsExactly<NotSupportedException>(() => AgentTaskBindingValidator.Validate(graph, Owner, binding));
        foreach (var invalid in new[]
        {
            binding with { RootRunId = "different-root" }, binding with { Provider = " provider" },
            binding with { Model = "" }, binding with { AgentId = "agent\n" }, binding with { BaseCommit = "main" },
            binding with { BaseCommit = new string('z', 40) }, binding with { MaxRounds = 0 },
            binding with { MaxOutputTokens = 0 }, binding with { SharedBudget = new(0) },
            binding with { SharedBudget = new(MaxToolCalls: -1) }, binding with { SharedBudget = new(MaxTotalTokens: 0) },
            binding with { SharedBudget = new(MaxExecutionTime: TimeSpan.Zero) }
        })
            Assert.ThrowsExactly<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph(), Owner, invalid));
        Assert.ThrowsExactly<ArgumentException>(() => AgentTaskBindingValidator.CreateChildRequest(Graph(), Owner, binding, Graph().Tasks[0], " attempt"));
    }

    [TestMethod]
    public void Validate_ArtifactMappingsAreExplicitAndOnlyReferenceRequiredFileOutputs()
    {
        var task = new WorkflowTaskDefinition("task", "Implement")
        {
            ExpectedArtifacts = [new("report", "Report", TaskArtifactKind.Report, "reports/result.md")],
            AcceptanceCriteria = [new("integrity", "Host explicitly maps this criterion") { ArtifactIds = ["report"] }, new("semantic", "Review the work")]
        };
        var graph = Graph(task);
        var binding = Binding() with { ArtifactVerificationRules = [new("task", "integrity")] };
        Assert.ContainsSingle(AgentTaskBindingValidator.Validate(graph, Owner, binding).ArtifactVerificationRules);
        Assert.IsEmpty(AgentTaskBindingValidator.Validate(graph, Owner, Binding()).ArtifactVerificationRules);
        foreach (var rules in new ImmutableArray<TaskArtifactVerificationRule>[]
        {
            [new("task", "integrity"), new("task", "integrity")], [new("unknown", "integrity")],
            [new("task", "unknown")], [new("task", "semantic")], default
        })
            Assert.ThrowsExactly<ArgumentException>(() => AgentTaskBindingValidator.Validate(graph, Owner, binding with { ArtifactVerificationRules = rules }));
        var optionalGraph = Graph(task with { ExpectedArtifacts = [task.ExpectedArtifacts[0] with { Required = false }] });
        Assert.ThrowsExactly<ArgumentException>(() => AgentTaskBindingValidator.Validate(optionalGraph, Owner, binding));
    }

    [TestMethod]
    [DataRow(TaskArtifactKind.File)]
    [DataRow(TaskArtifactKind.Report)]
    [DataRow(TaskArtifactKind.Patch)]
    public void Validate_AcceptsFileArtifactsWithSafeNonRootPaths(TaskArtifactKind kind)
    {
        var task = new WorkflowTaskDefinition("task", "Implement") { ExpectedArtifacts = [new("output", "Output", kind, "out/result.txt")] };
        AgentTaskBindingValidator.Validate(Graph(task), Owner, Binding());
        Assert.ThrowsExactly<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph(task with { ExpectedArtifacts = [task.ExpectedArtifacts[0] with { RelativePath = "." }] }), Owner, Binding()));
        Assert.ThrowsExactly<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph(task with { ExpectedArtifacts = [task.ExpectedArtifacts[0] with { RelativePath = null }] }), Owner, Binding()));
    }

    [TestMethod]
    [DataRow(TaskArtifactKind.Directory)]
    [DataRow(TaskArtifactKind.Other)]
    public void Validate_RejectsUnsupportedArtifactKinds(TaskArtifactKind kind)
    {
        var graph = Graph(new("task", "Implement") { ExpectedArtifacts = [new("output", "Output", kind, "out")] });
        Assert.ThrowsExactly<NotSupportedException>(() => AgentTaskBindingValidator.Validate(graph, Owner, Binding()));
    }
}
