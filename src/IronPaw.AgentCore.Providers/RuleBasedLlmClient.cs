using System.Text.Json;
using System.Text.RegularExpressions;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Providers;

public sealed class RuleBasedLlmClient : ILlmClient
{
    private static readonly Regex MarkdownFileRegex = new(
        pattern: @"\b([A-Za-z0-9._-]+\.md)\b",
        options: RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async IAsyncEnumerable<LlmDelta> StreamAsync(
        LlmRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var lastMessage = request.Messages.LastOrDefault();
        if (lastMessage is null)
        {
            yield return new AssistantTextDelta("No messages were provided.");
            yield return new CompletedDelta();
            yield break;
        }

        if (lastMessage.Role is ChatRole.Tool)
        {
            yield return new AssistantTextDelta(BuildToolSummary(lastMessage));
            yield return new UsageDelta(InputTokens: 120, OutputTokens: 65, CacheReadTokens: 0, CacheWriteTokens: 0);
            yield return new CompletedDelta();
            yield break;
        }

        if (lastMessage.Role is ChatRole.User && ShouldWritePlan(lastMessage.Content) && HasTool(request.Tools, "write_file"))
        {
            var targetFile = ExtractTargetMarkdownFile(lastMessage.Content) ?? "Plan.md";
            var callId = Guid.NewGuid().ToString("n");
            var args = JsonSerializer.SerializeToElement(
                new
                {
                    path = targetFile,
                    content = BuildPlanTemplate(lastMessage.Content)
                });

            yield return new AssistantTextDelta("I will create the plan file now.");
            yield return new ToolCallDelta(callId, "write_file", args);
            yield return new UsageDelta(InputTokens: 140, OutputTokens: 30, CacheReadTokens: 0, CacheWriteTokens: 0);
            yield return new CompletedDelta();
            yield break;
        }

        yield return new AssistantTextDelta(BuildDirectResponse(lastMessage.Content));
        yield return new UsageDelta(InputTokens: 80, OutputTokens: 50, CacheReadTokens: 0, CacheWriteTokens: 0);
        yield return new CompletedDelta();

        await Task.CompletedTask;
    }

    private static bool HasTool(IReadOnlyList<LlmToolDefinition> tools, string toolName)
    {
        return tools.Any(tool => tool.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldWritePlan(string userInput)
    {
        return userInput.Contains("plan", StringComparison.OrdinalIgnoreCase) &&
               (userInput.Contains("write", StringComparison.OrdinalIgnoreCase) ||
                userInput.Contains("create", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractTargetMarkdownFile(string text)
    {
        var match = MarkdownFileRegex.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string BuildToolSummary(ChatMessage toolMessage)
    {
        return $"Tool execution completed. Result: {toolMessage.Content}\n\nI can run another step if you want.";
    }

    private static string BuildDirectResponse(string userInput)
    {
        return "I can help with that. Ask me to create or update a specific file and I will use tools to do it.";
    }

    private static string BuildPlanTemplate(string originalPrompt)
    {
        var now = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        return $"""
# Feature Plan

Created: {now}

## Goal
{originalPrompt}

## Milestones
1. Define scope and acceptance criteria.
2. Implement core architecture and project layout.
3. Add persistence, tools, and orchestration tests.
4. Integrate desktop UX and collect run diagnostics.

## Risks
- Provider compatibility differences in tool schemas.
- Large tool outputs increasing context size.
- Session corruption without transcript repair.

## Next Actions
1. Confirm provider strategy and secrets management.
2. Add integration tests for tool-call and transcript flows.
3. Enable telemetry and debug bundle persistence.
""";
    }
}
