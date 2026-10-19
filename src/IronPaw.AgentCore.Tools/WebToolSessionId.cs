using IronPaw.AgentCore.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IronPaw.AgentCore.Tools;

internal static class WebToolSessionId
{
    public static string ResolveOrCreate(string? sessionId, ToolExecutionContext context)
    {
        var scope = JsonSerializer.Serialize(new[] { context.Caller.Id, context.ScopeSessionKey.Value, context.ScopeRunId.Value, context.AgentId ?? "main" });
        var prefix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..32] + "_";
        var alias = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId.Trim();
        // A returned ID round-trips only within the same authenticated root/agent scope.
        if (alias.Length == 65 && alias.StartsWith(prefix, StringComparison.Ordinal) && alias.AsSpan(33).ToString().All(char.IsAsciiHexDigit)) return alias;
        return prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(alias)))[..32];
    }
}
