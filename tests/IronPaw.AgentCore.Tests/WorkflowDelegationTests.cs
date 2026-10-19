using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class WorkflowDelegationTests
{
    [TestMethod]
    [DataRow(7, true)]
    [DataRow(6, false)]
    public async Task ThreeDelegationLevels_RetainRootAndImmediateParent_AndShareAdmissionBudget(int limit, bool succeeds)
    {
        var path = Path.Combine(Path.GetTempPath(), "IronPawDelegationTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        try
        {
            await using var store = await CSharpDbWorkflowStore.OpenAsync(Path.Combine(path, "workflow.cdb"));
            using var scheduler = new SessionLaneScheduler();
            var registry = new InMemoryToolRegistry([]);
            var agents = new InMemoryAgentRegistry();
            agents.Register(new AgentDefinition("nested", "Nested", "Executes one delegated level", "Execute the delegated level",
                new ToolSelectionPolicy(ToolProfile.Full), MaxRounds: 3));
            var model = new NestedModel();
            var runtime = new AgentOrchestrator(scheduler,
                new FileSessionStore(new FileSessionStoreOptions(Path.Combine(path, "sessions.json"), TimeSpan.Zero)),
                new JsonTranscriptStore(Path.Combine(path, "transcripts")), registry, new ToolExecutor(),
                new ToolSchemaNormalizer(), model, new TranscriptSanitizer(), new ToolResultTruncator(),
                new DefaultSystemPromptBuilder(), workflowStore: store);
            registry.RegisterTools([new ListAgentsTool(agents), new InvokeAgentTool(agents, runtime)]);
            var request = new AgentRunRequest(new SessionKey("root-session"), "start", "local", "nested-model",
                new ToolSelectionPolicy(ToolProfile.Full), CallerIdentity.LocalOwner(), Budget: new WorkflowBudget(limit, 4));
            if (succeeds)
            {
                var result = await runtime.RunAsync(request, NullAgentEventSink.Instance, CancellationToken.None);
                Assert.AreEqual(AgentRunStatus.Succeeded, result.Status);
                Assert.AreEqual(7, ((WorkflowUsage)result.Metadata["rootWorkflowUsage"]!).ModelCalls);
            }
            else
                Assert.AreEqual(AgentRunStatus.BudgetExhausted, (await runtime.RunAsync(request,
                    NullAgentEventSink.Instance, CancellationToken.None)).Status);

            var checkpoints = await store.ListAsync(CancellationToken.None);
            Assert.AreEqual(4, checkpoints.Count);
            var root = Assert.ContainsSingle(cp => cp.ParentRunId is null, checkpoints);
            var level1 = Assert.ContainsSingle(cp => cp.Request.UserInput == "level1", checkpoints);
            var level2 = Assert.ContainsSingle(cp => cp.Request.UserInput == "level2", checkpoints);
            var level3 = Assert.ContainsSingle(cp => cp.Request.UserInput == "level3", checkpoints);
            foreach (var cp in checkpoints)
            {
                Assert.AreEqual(root.RunId, cp.RootRunId);
            }
            Assert.AreEqual(root.RunId, level1.ParentRunId);
            Assert.AreEqual(level1.RunId, level2.ParentRunId);
            Assert.AreEqual(level2.RunId, level3.ParentRunId);
            Assert.AreEqual(limit, model.CallCount);
            var ledger = await ((IWorkflowStore)store).GetUsageAsync(new RunId(root.RunId), CancellationToken.None);
            Assert.AreEqual(limit, ledger.ModelCalls);
            Assert.AreEqual(4, ledger.ToolCalls);
            Assert.AreEqual(succeeds ? WorkflowStatus.Succeeded : WorkflowStatus.BudgetExhausted, root.Status);
            Assert.IsNull(root.LeaseOwner);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    private sealed class NestedModel : ILlmClient
    {
        public int CallCount { get; private set; }
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            CallCount++;
            if (request.SystemPrompt.Contains("You are a routing planner", StringComparison.Ordinal))
                yield return new AssistantTextDelta("""{"needs_clarification":false,"clarification_questions":[],"delegations":[{"agent_id":"nested","task":"level1"}]}""");
            else if (!request.Messages.Any(message => message.Role == ChatRole.Tool) &&
                request.Messages.Last(message => message.Role == ChatRole.User).Content is "level1" or "level2")
            {
                var level = request.Messages.Last(message => message.Role == ChatRole.User).Content;
                yield return new ToolCallDelta("delegate-" + level, "invoke_agent",
                    JsonSerializer.SerializeToElement(new { agent_id = "nested", task = level == "level1" ? "level2" : "level3" }));
            }
            else yield return new AssistantTextDelta("completed level");
            yield return new CompletedDelta();
        }
    }
}
