using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Skills.ExampleAddin;

public sealed class ExampleSkillAddin : ISkillAddin
{
    public string AddinId => "example.skills";

    public string Description => "Example add-in skills for text processing.";

    public IReadOnlyList<ISkill> CreateSkills()
    {
        return
        [
            new TextStatsSkill(),
            new SlugifySkill()
        ];
    }
}

internal sealed class TextStatsSkill : ISkill
{
    public SkillDescriptor Descriptor { get; } = new(
        Name: "text_stats",
        Description: "Calculates text statistics such as line, word, and character counts.",
        InputSchema: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                text = new
                {
                    type = "string",
                    description = "The input text to analyze."
                }
            },
            required = new[] { "text" }
        }));

    public Task<SkillResult> ExecuteAsync(SkillCall call, SkillExecutionContext context, CancellationToken ct)
    {
        if (!call.Arguments.TryGetProperty("text", out var textElement) ||
            textElement.ValueKind is not JsonValueKind.String)
        {
            return Task.FromResult(SkillResult.Error("Missing required string argument: text"));
        }

        var text = textElement.GetString() ?? string.Empty;
        var words = text
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Length;
        var lines = text.Split('\n').Length;

        var payload = JsonSerializer.Serialize(new
        {
            characters = text.Length,
            words,
            lines
        });

        return Task.FromResult(SkillResult.Success(payload));
    }
}

internal sealed class SlugifySkill : ISkill
{
    public SkillDescriptor Descriptor { get; } = new(
        Name: "slugify",
        Description: "Converts input text into a lowercase URL slug.",
        InputSchema: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                text = new
                {
                    type = "string",
                    description = "Input text."
                },
                separator = new
                {
                    type = "string",
                    description = "Optional separator character. Defaults to '-'."
                }
            },
            required = new[] { "text" }
        }));

    public Task<SkillResult> ExecuteAsync(SkillCall call, SkillExecutionContext context, CancellationToken ct)
    {
        if (!call.Arguments.TryGetProperty("text", out var textElement) ||
            textElement.ValueKind is not JsonValueKind.String)
        {
            return Task.FromResult(SkillResult.Error("Missing required string argument: text"));
        }

        var separator = "-";
        if (call.Arguments.TryGetProperty("separator", out var separatorElement) &&
            separatorElement.ValueKind is JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(separatorElement.GetString()))
        {
            separator = separatorElement.GetString()!;
        }

        var input = textElement.GetString() ?? string.Empty;
        var normalized = new string(
            input
                .ToLowerInvariant()
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
                .ToArray());

        var slug = string.Join(
            separator,
            normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return Task.FromResult(SkillResult.Success(slug));
    }
}
