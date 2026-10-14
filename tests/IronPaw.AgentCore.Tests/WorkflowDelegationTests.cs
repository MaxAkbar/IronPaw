using System.Runtime.CompilerServices;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;

namespace IronPaw.AgentCore.Tests;

public sealed class WorkflowDelegationTests
{
    [Theory]
    [InlineData(7, true)]
    [InlineData(6, false)]
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
                Assert.Equal(AgentRunStatus.Succeeded, result.Status);
                Assert.Equal(7, ((WorkflowUsage)result.Metadata["rootWorkflowUsage"]!).ModelCalls);
            }
            else
                await Assert.ThrowsAsync<WorkflowBudgetExceededException>(() => runtime.RunAsync(request,
                    NullAgentEventSink.Instance, CancellationToken.None));

            var checkpoints = await store.ListAsync(CancellationToken.None);
            Assert.Equal(4, checkpoints.Count);
            var root = Assert.Single(checkpoints, cp => cp.ParentRunId is null);
            var level1 = Assert.Single(checkpoints, cp => cp.Request.UserInput == "level1");
            var level2 = Assert.Single(checkpoints, cp => cp.Request.UserInput == "level2");
            var level3 = Assert.Single(checkpoints, cp => cp.Request.UserInput == "level3");
            Assert.All(checkpoints, cp => Assert.Equal(root.RunId, cp.RootRunId));
            Assert.Equal(root.RunId, level1.ParentRunId);
            Assert.Equal(level1.RunId, level2.ParentRunId);
            Assert.Equal(level2.RunId, level3.ParentRunId);
            Assert.Equal(limit, model.CallCount);
            var ledger = await ((IWorkflowStore)store).GetUsageAsync(new RunId(root.RunId), CancellationToken.None);
            Assert.Equal(limit, ledger.ModelCalls);
            Assert.Equal(4, ledger.ToolCalls);
            Assert.Equal(succeeds ? WorkflowStatus.Succeeded : WorkflowStatus.BudgetExhausted, root.Status);
            Assert.Null(root.LeaseOwner);
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
