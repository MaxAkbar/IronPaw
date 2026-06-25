using System.Text.Json;

namespace IronPaw.AgentCore.Providers;

public sealed record OpenAiClientOptions(
    int ContextWindowTokens = 128_000,
    int SafetyMarginTokens = 2_048,
    int MinimumOutputTokensPerRequest = 256,
    int MaxAutoContinuationPasses = 2)
{
    internal OpenAiClientOptions Normalize()
    {
        var normalizedContextWindow = Math.Max(1_024, ContextWindowTokens);
        var normalizedSafetyMargin = Math.Clamp(
            SafetyMarginTokens,
            min: 0,
            max: normalizedContextWindow - 1);
        var normalizedMinimumOutput = Math.Clamp(
            MinimumOutputTokensPerRequest,
            min: 1,
            max: normalizedContextWindow - normalizedSafetyMargin);
        var normalizedAutoContinuationPasses = Math.Clamp(MaxAutoContinuationPasses, min: 0, max: 8);

        return this with
        {
            ContextWindowTokens = normalizedContextWindow,
            SafetyMarginTokens = normalizedSafetyMargin,
            MinimumOutputTokensPerRequest = normalizedMinimumOutput,
            MaxAutoContinuationPasses = normalizedAutoContinuationPasses
        };
    }
}

internal static class OpenAiOutputTokenBudgetPlanner
{
    private const double AverageCharsPerToken = 4.0;
    private const int RequestOverheadTokens = 32;
    private const int MessageOverheadTokens = 6;
    private const int ToolOverheadTokens = 12;

    public static OpenAiOutputTokenBudgetPlan Plan(
        OpenAiClientOptions options,
        IReadOnlyList<object> messages,
        IReadOnlyList<object> tools,
        int requestedMaxOutputTokens,
        long consumedOutputTokens)
    {
        var normalizedOptions = options.Normalize();
        var requestedRemaining = Math.Max(
            0,
            requestedMaxOutputTokens - (int)Math.Clamp(consumedOutputTokens, 0, int.MaxValue));
        var estimatedInputTokens = EstimateInputTokens(messages, tools);
        var availableContextTokens = Math.Max(
            0,
            normalizedOptions.ContextWindowTokens - estimatedInputTokens - normalizedOptions.SafetyMarginTokens);

        var maxTokens = Math.Min(requestedRemaining, availableContextTokens);
        if (maxTokens > 0 &&
            maxTokens < normalizedOptions.MinimumOutputTokensPerRequest &&
            requestedRemaining >= normalizedOptions.MinimumOutputTokensPerRequest &&
            availableContextTokens >= normalizedOptions.MinimumOutputTokensPerRequest)
        {
            maxTokens = normalizedOptions.MinimumOutputTokensPerRequest;
        }

        return new OpenAiOutputTokenBudgetPlan(
            MaxTokens: Math.Max(0, maxTokens),
            EstimatedInputTokens: estimatedInputTokens,
            AvailableContextTokens: availableContextTokens,
            RemainingRequestedOutputTokens: requestedRemaining);
    }

    public static int EstimateTokensFromCharacters(int characterCount)
    {
        if (characterCount <= 0)
        {
            return 0;
        }

        return (int)Math.Ceiling(characterCount / AverageCharsPerToken);
    }

    private static int EstimateInputTokens(
        IReadOnlyList<object> messages,
        IReadOnlyList<object> tools)
    {
        var chars =
            JsonSerializer.Serialize(messages).Length +
            JsonSerializer.Serialize(tools).Length;
        var charBasedTokens = EstimateTokensFromCharacters(chars);
        var structuralOverhead = RequestOverheadTokens + (messages.Count * MessageOverheadTokens) + (tools.Count * ToolOverheadTokens);

        return charBasedTokens + structuralOverhead;
    }
}

internal sealed record OpenAiOutputTokenBudgetPlan(
    int MaxTokens,
    int EstimatedInputTokens,
    int AvailableContextTokens,
    int RemainingRequestedOutputTokens);
