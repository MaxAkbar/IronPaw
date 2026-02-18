using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class ToolExecutor : IToolExecutor
{
    private readonly IToolHookRunner _hooks;

    public ToolExecutor(IToolHookRunner? hooks = null)
    {
        _hooks = hooks ?? NullToolHookRunner.Instance;
    }

    public async Task<ToolResult> ExecuteAsync(
        ITool tool,
        ToolCall call,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var middlewareContext = new ToolMiddlewareContext(call, context, tool);
        var effectiveCall = await _hooks.BeforeToolCallAsync(middlewareContext, ct);
        var effectiveContext = middlewareContext with { Call = effectiveCall };

        try
        {
            var result = await tool.ExecuteAsync(effectiveCall, context, ct);
            await _hooks.AfterToolCallAsync(effectiveContext, result, error: null, ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            await _hooks.AfterToolCallAsync(effectiveContext, result: null, new OperationCanceledException(), ct);
            throw;
        }
        catch (Exception ex)
        {
            var errorResult = ToolResult.Error(
                $"Tool '{tool.Descriptor.Name}' failed: {ex.Message}",
                new Dictionary<string, object?>
                {
                    ["tool"] = tool.Descriptor.Name,
                    ["error"] = ex.ToString()
                });

            await _hooks.AfterToolCallAsync(effectiveContext, errorResult, ex, ct);
            return errorResult;
        }
    }
}
