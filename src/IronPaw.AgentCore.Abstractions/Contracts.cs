using System.Collections.ObjectModel;
using System.Text.Json;

namespace IronPaw.AgentCore.Abstractions;

public readonly record struct SessionKey
{
    public SessionKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Session key cannot be null or empty.", nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public static SessionKey New() => new(Guid.NewGuid().ToString("n"));

    public override string ToString() => Value;
}

public readonly record struct RunId
{
    public RunId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Run id cannot be null or empty.", nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public static RunId New() => new(Guid.NewGuid().ToString("n"));

    public override string ToString() => Value;
}

public sealed record CallerIdentity(
    string Id,
    string DisplayName,
    bool IsOwner)
{
    public static CallerIdentity LocalOwner() =>
        new(Environment.UserName, Environment.UserName, IsOwner: true);

    public static CallerIdentity Authenticated(string id, string displayName) =>
        new(id, displayName, IsOwner: false);

    public static CallerIdentity Anonymous() =>
        new("anonymous", "Anonymous", IsOwner: false);
}

public enum AgentEventStream
{
    Lifecycle,
    Assistant,
    Reasoning,
    Tool,
    Compaction,
    Diagnostics
}

public enum AgentEventSeverity
{
    Info,
    Warning,
    Error
}

public sealed record AgentEvent(
    RunId RunId,
    SessionKey SessionKey,
    AgentEventStream Stream,
    string Type,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, object?> Data,
    AgentEventSeverity Severity = AgentEventSeverity.Info,
    string? AgentId = null);

public interface IAgentEventSink
{
    ValueTask PublishAsync(AgentEvent evt, CancellationToken ct);
}

public sealed class NullAgentEventSink : IAgentEventSink
{
    public static readonly NullAgentEventSink Instance = new();

    private NullAgentEventSink()
    {
    }

    public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct) => ValueTask.CompletedTask;
}

public enum ToolProfile
{
    Minimal,
    Coding,
    Messaging,
    Web,
    Full
}

public sealed record ToolSelectionPolicy(
    ToolProfile Profile,
    bool SenderIsOwner = false,
    IReadOnlyCollection<string>? AllowList = null,
    IReadOnlyCollection<string>? DenyList = null,
    IReadOnlyCollection<string>? AllowedGroups = null,
    int SubagentDepth = 0,
    ToolSelectionPolicy? InheritedPolicy = null);

public sealed record ToolDescriptor(
    string Name,
    string Description,
    JsonElement ParametersSchema,
    string Group,
    bool OwnerOnly = false);

public sealed record ToolCall(
    string ToolCallId,
    string ToolName,
    JsonElement Arguments);

public sealed record ToolExecutionContext(
    RunId RunId,
    SessionKey SessionKey,
    string Provider,
    string Model,
    CallerIdentity Caller,
    int SubagentDepth = 0,
    string? AgentId = null,
    RunId? ParentRunId = null,
    SessionKey? ParentSessionKey = null,
    ToolSelectionPolicy? ToolPolicy = null,
    IAgentEventSink? EventSink = null)
{
    public RunId ScopeRunId => ParentRunId ?? RunId;

    public SessionKey ScopeSessionKey => ParentSessionKey ?? SessionKey;
}

public enum ContentTrust
{
    Trusted,
    Local,
    Untrusted
}

public sealed record ToolResult(
    bool IsError,
    string Content,
    ContentTrust Trust = ContentTrust.Trusted,
    IReadOnlyDictionary<string, object?>? Details = null)
{
    public static ToolResult Success(
        string content,
        ContentTrust trust = ContentTrust.Trusted,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        return new ToolResult(false, content, trust, details);
    }

    public static ToolResult Error(string message, IReadOnlyDictionary<string, object?>? details = null)
    {
        return new ToolResult(true, message, ContentTrust.Trusted, details);
    }
}

public interface ITool
{
    ToolDescriptor Descriptor { get; }

    Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct);
}

public interface IToolRegistry
{
    IReadOnlyList<ITool> ResolveTools(ToolSelectionPolicy policy);
}

public interface IToolRegistrar
{
    void RegisterTools(IEnumerable<ITool> tools);
}

public interface IToolExecutor
{
    Task<ToolResult> ExecuteAsync(ITool tool, ToolCall call, ToolExecutionContext context, CancellationToken ct);
}

public interface IToolSchemaNormalizer
{
    LlmToolDefinition ToDefinition(ITool tool, string provider);
}

public sealed record SkillDescriptor(
    string Name,
    string Description,
    JsonElement InputSchema);

public sealed record SkillCall(
    string SkillCallId,
    string SkillName,
    JsonElement Arguments);

public sealed record SkillExecutionContext(
    RunId RunId,
    SessionKey SessionKey,
    string Provider,
    string Model);

public sealed record SkillResult(
    bool IsError,
    string Content,
    IReadOnlyDictionary<string, object?>? Details = null)
{
    public static SkillResult Success(string content, IReadOnlyDictionary<string, object?>? details = null)
    {
        return new SkillResult(false, content, details);
    }

    public static SkillResult Error(string message, IReadOnlyDictionary<string, object?>? details = null)
    {
        return new SkillResult(true, message, details);
    }
}

public interface ISkill
{
    SkillDescriptor Descriptor { get; }

    Task<SkillResult> ExecuteAsync(SkillCall call, SkillExecutionContext context, CancellationToken ct);
}

public interface ISkillAddin
{
    string AddinId { get; }

    string Description { get; }

    IReadOnlyList<ISkill> CreateSkills();
}

public sealed record ToolMiddlewareContext(
    ToolCall Call,
    ToolExecutionContext ExecutionContext,
    ITool Tool);

public interface IToolHookRunner
{
    ValueTask<ToolCall> BeforeToolCallAsync(ToolMiddlewareContext context, CancellationToken ct);

    ValueTask AfterToolCallAsync(
        ToolMiddlewareContext context,
        ToolResult? result,
        Exception? error,
        CancellationToken ct);
}

public sealed class NullToolHookRunner : IToolHookRunner
{
    public static readonly NullToolHookRunner Instance = new();

    private NullToolHookRunner()
    {
    }

    public ValueTask<ToolCall> BeforeToolCallAsync(ToolMiddlewareContext context, CancellationToken ct)
    {
        return ValueTask.FromResult(context.Call);
    }

    public ValueTask AfterToolCallAsync(
        ToolMiddlewareContext context,
        ToolResult? result,
        Exception? error,
        CancellationToken ct)
    {
        return ValueTask.CompletedTask;
    }
}

public sealed record ChatMessage(
    ChatRole Role,
    string Content,
    string? ToolCallId = null,
    string? ToolName = null);

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed record LlmToolDefinition(
    string Name,
    string Description,
    JsonElement ParametersSchema);

public sealed record LlmRequest(
    RunId RunId,
    SessionKey SessionKey,
    string Provider,
    string Model,
    string SystemPrompt,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<LlmToolDefinition> Tools,
    TimeSpan Timeout,
    int MaxOutputTokens = 4096);

public abstract record LlmDelta;

public sealed record AssistantTextDelta(string Text) : LlmDelta;

public sealed record ToolCallDelta(
    string ToolCallId,
    string ToolName,
    JsonElement Arguments) : LlmDelta;

public sealed record UsageDelta(
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens) : LlmDelta;

public sealed record CompletedDelta : LlmDelta;

public sealed record LlmErrorDelta(string Error) : LlmDelta;

public interface ILlmClient
{
    IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, CancellationToken ct);
}

public interface ISystemPromptBuilder
{
    string BuildPrompt(AgentRunRequest request, IReadOnlyList<ITool> tools);
}

public interface ISessionLaneScheduler
{
    Task<T> EnqueueAsync<T>(SessionKey sessionKey, Func<CancellationToken, Task<T>> work, CancellationToken ct);
}

public interface ITranscriptSanitizer
{
    IReadOnlyList<ChatMessage> Repair(IReadOnlyList<ChatMessage> messages);
}

public interface IToolResultTruncator
{
    IReadOnlyList<ChatMessage> Truncate(IReadOnlyList<ChatMessage> messages, int contextWindowChars);
}

public sealed record SessionEntry(
    string SessionId,
    DateTimeOffset UpdatedAt,
    string? TranscriptPath = null,
    string? ProviderOverride = null,
    string? ModelOverride = null,
    int CompactionCount = 0,
    long InputTokens = 0,
    long OutputTokens = 0,
    long TotalTokens = 0,
    bool TotalTokensFresh = true,
    IReadOnlyDictionary<string, object?>? Diagnostics = null);

public interface ISessionStore
{
    Task<SessionEntry?> GetAsync(SessionKey key, CancellationToken ct);

    Task UpsertAsync(SessionKey key, Func<SessionEntry?, SessionEntry> mutate, CancellationToken ct);

    Task<IReadOnlyDictionary<SessionKey, SessionEntry>> LoadAllAsync(CancellationToken ct);
}

public interface ITranscriptStore
{
    Task<IReadOnlyList<ChatMessage>> LoadAsync(SessionKey key, CancellationToken ct);

    Task AppendAsync(SessionKey key, IEnumerable<ChatMessage> messages, CancellationToken ct);

    Task ReplaceAsync(SessionKey key, IEnumerable<ChatMessage> messages, CancellationToken ct);
}

public sealed record AgentRunRequest(
    SessionKey SessionKey,
    string UserInput,
    string Provider,
    string Model,
    ToolSelectionPolicy ToolPolicy,
    CallerIdentity Caller,
    bool AutoCreateSkills = false,
    int ContextWindowChars = 32_768,
    string? SystemPromptOverride = null,
    int MaxOutputTokens = 4096,
    int MaxRounds = 6,
    IReadOnlyList<MemoryEntry>? ActiveMemories = null,
    string? AgentId = null,
    RunId? ParentRunId = null,
    SessionKey? ParentSessionKey = null);

/// <summary>Tracks completion of the execution loop. Artifact verification is separate.</summary>
public enum AgentRunStatus
{
    Succeeded,
    Failed,
    Cancelled,
    RoundLimitReached,
    AwaitingInput
}

public sealed record AgentRunResult(
    RunId RunId,
    SessionKey SessionKey,
    string FinalAssistantMessage,
    IReadOnlyList<ChatMessage> Transcript,
    IReadOnlyDictionary<string, object?> Metadata,
    AgentRunStatus Status = AgentRunStatus.Succeeded);

public interface IAgentOrchestrator
{
    Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentEventSink sink, CancellationToken ct);
}

public enum MemoryScope
{
    Global,
    Session
}

public sealed record MemoryEntry(
    string Id,
    string Content,
    MemoryScope Scope,
    DateTimeOffset CreatedAt,
    string? SessionId = null,
    string? Category = null,
    string? Source = null);

public interface IMemoryStore
{
    Task<IReadOnlyList<MemoryEntry>> LoadGlobalAsync(CancellationToken ct);

    Task<IReadOnlyList<MemoryEntry>> LoadSessionAsync(SessionKey sessionKey, CancellationToken ct);

    Task<MemoryEntry> AddAsync(MemoryEntry entry, CancellationToken ct);

    Task<bool> RemoveAsync(string memoryId, CancellationToken ct);

    Task<IReadOnlyList<MemoryEntry>> SearchAsync(
        string query,
        MemoryScope? scope,
        SessionKey? sessionKey,
        CancellationToken ct);
}

public sealed record RunSummary(
    string RunId,
    string SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string UserPrompt,
    string FinalAssistantMessage,
    int Rounds,
    long TotalTokens,
    IReadOnlyList<string> ToolsUsed,
    AgentRunStatus Status = AgentRunStatus.Succeeded,
    string? Error = null);

public interface IRunHistoryStore
{
    Task RecordAsync(RunSummary summary, CancellationToken ct);

    Task<IReadOnlyList<RunSummary>> LoadAllAsync(CancellationToken ct);

    Task<IReadOnlyList<RunSummary>> LoadBySessionAsync(SessionKey sessionKey, CancellationToken ct);

    Task<RunSummary?> GetAsync(string runId, CancellationToken ct);
}

public enum StoreBackend
{
    Json,
    Sqlite
}

public sealed record AgentDefinition(
    string AgentId,
    string DisplayName,
    string Description,
    string SystemPrompt,
    ToolSelectionPolicy ToolPolicy,
    string? Provider = null,
    string? Model = null,
    int MaxRounds = 6,
    int MaxOutputTokens = 4096,
    IReadOnlyCollection<string>? AllowedMessageSenders = null)
{
    public bool CanReceiveMessagesFrom(string senderAgentId)
    {
        if (string.IsNullOrWhiteSpace(senderAgentId))
        {
            return false;
        }

        if (string.Equals(AgentId, senderAgentId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (AllowedMessageSenders is null || AllowedMessageSenders.Count == 0)
        {
            return false;
        }

        foreach (var allowedSender in AllowedMessageSenders)
        {
            if (string.IsNullOrWhiteSpace(allowedSender))
            {
                continue;
            }

            if (string.Equals(allowedSender.Trim(), senderAgentId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record AgentMessage(
    string MessageId,
    SessionKey SessionKey,
    RunId ScopeRunId,
    string SenderAgentId,
    string RecipientAgentId,
    string Content,
    DateTimeOffset SentAt,
    string? CorrelationId = null);

public sealed record AgentMessageSendRequest(
    SessionKey SessionKey,
    RunId ScopeRunId,
    string SenderAgentId,
    string RecipientAgentId,
    string Content,
    string? CorrelationId = null);

public sealed record AgentMessageReadRequest(
    SessionKey SessionKey,
    RunId ScopeRunId,
    string RecipientAgentId,
    int MaxMessages = 20,
    bool MarkAsRead = true,
    string? SenderAgentId = null,
    string? CorrelationId = null);

public interface IAgentMessageBus
{
    Task<AgentMessage> SendAsync(AgentMessageSendRequest request, CancellationToken ct);

    Task<IReadOnlyList<AgentMessage>> ReadAsync(AgentMessageReadRequest request, CancellationToken ct);
}

public interface IAgentRegistry
{
    IReadOnlyList<AgentDefinition> GetAll();

    AgentDefinition? Get(string agentId);

    void Register(AgentDefinition definition);
}

public static class EventData
{
    public static IReadOnlyDictionary<string, object?> Of(params (string Key, object? Value)[] values)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            dict[key] = value;
        }

        return new ReadOnlyDictionary<string, object?>(dict);
    }
}
