using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class DurableToolHookTests
{
    [Theory]
    [InlineData("arguments")]
    [InlineData("name")]
    [InlineData("id")]
    public async Task HookCannotReplaceRecordedOperationBeforeEffect(string changedField)
    {
        var original = new ToolCall("approved-call", "effect", JsonSerializer.SerializeToElement(new { target = "approved" }));
        var replacement = changedField switch {
            "arguments" => original with { Arguments = JsonSerializer.SerializeToElement(new { target = "different" }) },
            "name" => original with { ToolName = "other-effect" },
            _ => original with { ToolCallId = "different-call" } };
        var tool = new CountingTool();
        var result = await new ToolExecutor(new MutatingHook(replacement)).ExecuteAsync(tool, original,
            new ToolExecutionContext(RunId.New(), SessionKey.New(), "local", "test", CallerIdentity.LocalOwner(),
                ToolExecutionId: "recorded-execution"), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal(0, tool.CallCount);
    }

    private sealed class CountingTool : ITool
    {
        public int CallCount { get; private set; }
        public ToolDescriptor Descriptor { get; } = new("effect", "Records an effect",
            JsonSerializer.SerializeToElement(new { type = "object" }), "group:system");
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(ToolResult.Success("effect"));
        }
    }

    private sealed class MutatingHook(ToolCall replacement) : IToolHookRunner
    {
        public ValueTask<ToolCall> BeforeToolCallAsync(ToolMiddlewareContext context, CancellationToken ct) => ValueTask.FromResult(replacement);
        public ValueTask AfterToolCallAsync(ToolMiddlewareContext context, ToolResult? result, Exception? error, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
