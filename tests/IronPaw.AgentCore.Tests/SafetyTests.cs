using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

public sealed class SafetyTests
{
    [Fact]
    public void TranscriptSanitizer_RemovesOrphans_AndAddsSyntheticMissingResults()
    {
        var sanitizer = new TranscriptSanitizer();
        var transcript = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "{\"path\":\"Plan.md\"}", ToolCallId: "c1", ToolName: "write_file"),
            new(ChatRole.Tool, "ok", ToolCallId: "c1", ToolName: "write_file"),
            new(ChatRole.Tool, "duplicate", ToolCallId: "c1", ToolName: "write_file"),
            new(ChatRole.Assistant, "{\"text\":\"hello\"}", ToolCallId: "c2", ToolName: "slugify"),
            new(ChatRole.Tool, "orphan", ToolCallId: "missing", ToolName: "slugify"),
            new(ChatRole.Tool, "empty-id", ToolCallId: null, ToolName: "slugify")
        };

        var repaired = sanitizer.Repair(transcript);

        Assert.Equal(4, repaired.Count);
        Assert.Contains(repaired, message => message.Role == ChatRole.Tool && message.ToolCallId == "c1");
        Assert.DoesNotContain(repaired, message => message.Content == "duplicate");
        Assert.DoesNotContain(repaired, message => message.Content == "orphan");

        var synthetic = Assert.Single(repaired, message => message.Role == ChatRole.Tool && message.ToolCallId == "c2");
        Assert.Contains("Synthetic tool error", synthetic.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolResultTruncator_TruncatesOnlyToolMessages()
    {
        var truncator = new ToolResultTruncator(maxToolResultShare: 0.2);
        var longText = new string('x', 500);
        var transcript = new List<ChatMessage>
        {
            new(ChatRole.User, "hello"),
            new(ChatRole.Tool, longText, ToolCallId: "tool-1", ToolName: "read_file")
        };

        var result = truncator.Truncate(transcript, contextWindowChars: 1000);
        var user = result[0];
        var tool = result[1];

        Assert.Equal("hello", user.Content);
        Assert.StartsWith(new string('x', 256), tool.Content, StringComparison.Ordinal);
        Assert.Contains("[tool result truncated to 256 chars]", tool.Content, StringComparison.Ordinal);
    }
}
