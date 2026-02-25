using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

internal static class WebToolSessionId
{
    public static string ResolveOrCreate(string? sessionId, ToolExecutionContext context)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return sessionId.Trim();
        }

        var agentSegment = string.IsNullOrWhiteSpace(context.AgentId) ? "agent" : context.AgentId.Trim();
        return $"{context.ScopeSessionKey.Value}:{context.ScopeRunId.Value}:{agentSegment}";
    }
}
