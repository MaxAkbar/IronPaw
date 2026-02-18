# IronPaw Code Review

> Reviewed: February 2026
> Scope: Full codebase — all layers from Abstractions through Hosts
> Framework: .NET 10.0

---

## Summary

IronPaw is a well-structured, OpenClaw-inspired agent orchestration framework. The layered architecture, interface-first design, and safety-first approach to transcript and file management are all genuinely strong. The codebase is clearly the work of someone who has thought carefully about correctness and extensibility.

The notes below are meant to surface real issues — bugs, design gaps, and potential gotchas — that would surface in production or scale. They are grouped from most to least impactful.

---

## Strengths

Before the issues: a few things that are done particularly well and worth preserving.

- **Clean layer boundaries.** `Abstractions` has zero dependencies. `Core`, `Tools`, `Persistence`, and `Providers` each depend only on `Abstractions`. Hosts depend on everything but nothing depends on them. This is the right shape.
- **Transcript safety pipeline.** Pairing `TranscriptSanitizer` with `ToolResultTruncator` is a good pattern. Running both before and after each tool round is correct.
- **Atomic file writes.** Both `FileSessionStore` and `JsonTranscriptStore` write to a temp file then use `File.Replace`/`File.Move`. This prevents corrupt state on crash or power loss.
- **Path containment checks.** `WorkspacePathResolver` and `JsonTranscriptStore.ResolvePath` both validate that resolved paths stay within their expected roots. This is easy to forget and usually only caught in security review.
- **`NullAgentEventSink` and `NullToolHookRunner`.** Proper null-object implementations rather than null checks scattered through calling code.
- **`SessionLaneScheduler`.** Per-session serialization is the right model for an agent that maintains conversation state. Concurrent runs against the same session would corrupt transcripts.

---

## Issues

### 1. `ToolExecutor` and `ToolSchemaNormalizer` Are Injected as Concrete Types

**File:** [AgentOrchestrator.cs](src/IronPaw.AgentCore/AgentOrchestrator.cs#L13-L14)

```csharp
private readonly ToolExecutor _toolExecutor;
private readonly ToolSchemaNormalizer _schemaNormalizer;
```

Every other dependency on `AgentOrchestrator` is an interface. These two are concrete. This breaks the testability of the orchestrator — you cannot substitute them with fakes or mocks without also instantiating the real implementations. Both classes have clear behavioral contracts that would be well-served by interfaces (`IToolExecutor`, `IToolSchemaNormalizer`).

---

### 2. The LLM Clients Do Not Actually Stream

**Files:** [OpenAiLlmClient.cs](src/IronPaw.AgentCore.Providers/OpenAiLlmClient.cs#L38-L39), [AnthropicLlmClient.cs](src/IronPaw.AgentCore.Providers/AnthropicLlmClient.cs#L42-L43)

Both clients use `HttpCompletionOption.ResponseHeadersRead` (good) but then immediately call `ReadAsStringAsync` (not streaming):

```csharp
using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
var rawBody = await response.Content.ReadAsStringAsync(ct);
```

The interface is `IAsyncEnumerable<LlmDelta>` which implies streaming, and the orchestrator publishes `assistant_delta` events as if content arrives incrementally — but in practice the full response is buffered first. This means:

- Users see no output until the full LLM response has been received, then everything appears at once.
- Large responses hold the entire body in memory before any processing begins.
- The UX implication is significant: a 10-second LLM response shows nothing for 10 seconds, then flushes all at once.

True SSE streaming would require reading `text/event-stream` line by line using `GetContentStream()` + `StreamReader`.

---

### 3. `max_tokens` Is Hard-Coded in `AnthropicLlmClient`

**File:** [AnthropicLlmClient.cs](src/IronPaw.AgentCore.Providers/AnthropicLlmClient.cs#L90)

```csharp
["max_tokens"] = 2_048,
```

`2048` output tokens is conservative. Claude 3.5 Haiku (the default model) supports 8192. Long tasks — file generation, detailed analysis — will be cut short silently. This value is not configurable from `LlmRequest` or `AgentRunRequest`. It should be exposed as a parameter.

---

### 4. Tool Call and Text Messages Can Create Consecutive Assistant Messages

**File:** [AgentOrchestrator.cs](src/IronPaw.AgentCore/AgentOrchestrator.cs#L146-L184)

When a `ToolCallDelta` arrives it is immediately appended to the transcript:

```csharp
case ToolCallDelta toolCall:
    // ...
    transcript.Add(new ChatMessage(ChatRole.Assistant, toolCall.Arguments.GetRawText(), toolCall.ToolCallId, toolCall.ToolName));
```

Text deltas are buffered and appended later:

```csharp
var assistantText = assistantBuffer.ToString().Trim();
if (!string.IsNullOrWhiteSpace(assistantText))
{
    transcript.Add(new ChatMessage(ChatRole.Assistant, assistantText));
}
```

If a round produces both text and a tool call, the transcript ends up with two consecutive `Assistant` messages: one with the tool call, then one with the text. Many providers (OpenAI in particular) will reject this or produce unexpected behavior. The text message should be inserted *before* the tool call messages, or merged into a single assistant turn.

---

### 5. `TranscriptSanitizer` Appends Synthetic Results at the Wrong Position

**File:** [TranscriptSanitizer.cs](src/IronPaw.AgentCore/TranscriptSanitizer.cs#L40-L47)

```csharp
foreach (var missingCallId in pendingToolCalls)
{
    sanitized.Add(new ChatMessage(
        Role: ChatRole.Tool,
        Content: "Synthetic tool error: missing tool result was repaired.",
        ToolCallId: missingCallId,
        ToolName: "unknown"));
}
```

Synthetic results are appended at the very end of the list. If any non-tool messages (e.g., a subsequent user or assistant message) follow the orphaned call, the synthetic result will be placed *after* those messages, creating an invalid sequence. The synthetic result should be inserted immediately after the orphaned tool-call message, not at the end of the entire list.

---

### 6. `SessionLaneScheduler` Leaks `SemaphoreSlim` Objects

**File:** [SessionLaneScheduler.cs](src/IronPaw.AgentCore/SessionLaneScheduler.cs)

The `ConcurrentDictionary<string, SemaphoreSlim>` accumulates one semaphore per unique session key and never evicts or disposes them. For long-running processes where users create many distinct sessions, this is a steady memory leak. `SemaphoreSlim` implements `IDisposable`, and held instances that are no longer needed should be removed and disposed.

A reference-counted or time-expiring eviction strategy would address this.

---

### 7. `JsonTranscriptStore` Uses a Single Gate for All Sessions

**File:** [JsonTranscriptStore.cs](src/IronPaw.AgentCore.Persistence/JsonTranscriptStore.cs#L10)

```csharp
private readonly SemaphoreSlim _gate = new(1, 1);
```

One semaphore serializes all transcript I/O across every session. An append for session A will block a read for session B. In the Desktop host, where multiple sessions might be active simultaneously, this creates unnecessary contention. A per-session gate (a `ConcurrentDictionary<string, SemaphoreSlim>`) would allow fully parallel I/O for independent sessions.

---

### 8. `lastCacheRead`/`lastCacheWrite` Accumulate Incorrectly

**File:** [AgentOrchestrator.cs](src/IronPaw.AgentCore/AgentOrchestrator.cs#L165-L173)

```csharp
if (usage.CacheReadTokens.HasValue)
    lastCacheRead = usage.CacheReadTokens.Value;

if (usage.CacheWriteTokens.HasValue)
    lastCacheWrite = usage.CacheWriteTokens.Value;
```

Cache token counts are *replaced*, not *accumulated*. If there are multiple rounds, only the last round's cache usage is counted. `aggregatedInput` and `aggregatedOutput` are accumulated (`+=`) correctly — cache tokens should be too.

---

### 9. `maxRounds` Is a Hard-Coded Magic Number

**File:** [AgentOrchestrator.cs](src/IronPaw.AgentCore/AgentOrchestrator.cs#L95)

```csharp
const int maxRounds = 6;
```

Six rounds cannot be changed without recompiling. Some tasks legitimately need more, and some low-trust contexts might want fewer. This should be a field on `AgentRunRequest` (with a sensible default) so callers can control it.

---

### 10. `ToolResultTruncator` Truncates Per-Message, Not Per-Total

**File:** [ToolResultTruncator.cs](src/IronPaw.AgentCore/ToolResultTruncator.cs)

Each tool result is truncated independently to 35% of the context window. If a round produces five tool calls each with results just under the individual limit, the total tool content could consume 175% of the budget. A budget-aware approach — tracking cumulative tool content across all messages and truncating when the budget is exhausted — would be more robust.

---

### 11. `ILLMClient` Violates .NET Naming Conventions

**File:** [Contracts.cs](src/IronPaw.AgentCore.Abstractions/Contracts.cs#L283)

```csharp
public interface ILLMClient
```

Per .NET naming guidelines (CA1715), acronyms of three or more letters should be Pascal-cased. The interface should be `ILlmClient`. This also affects `ILLMClient` usages across the codebase. A consistent rename is easy and makes the API more idiomatic.

---

### 12. `AgentOrchestrator` Calls `ReplaceAsync` Twice Per Run

**File:** [AgentOrchestrator.cs](src/IronPaw.AgentCore/AgentOrchestrator.cs#L228-L240)

```csharp
// Inside the loop, after each tool-call round:
await _transcriptStore.ReplaceAsync(request.SessionKey, transcript, ct);

// After the loop, always:
await _transcriptStore.ReplaceAsync(request.SessionKey, transcript, ct);
```

When the loop exits because there were no tool calls (a plain text response), the in-loop `ReplaceAsync` never runs, so the post-loop write is the only one — correct. But when tool calls occur, the in-loop write is correct and the post-loop write is a redundant second write. The post-loop write exists to persist the final assistant message, but that message was already added to `transcript` before the loop exited. The logic should be unified.

---

### 13. Help Table Uses Backtick Markdown in Spectre Markup Context

**File:** [Program.cs](src/IronPaw.Cli/Program.cs#L851-L858)

```csharp
table.AddRow("`-h, --help`", "Show help");
table.AddRow("`-p, --provider <name>`", "...");
```

Spectre.Console does not parse markdown backticks. These will render literally as `` `-h, --help` `` in the terminal — the backticks appear as characters. Either use Spectre markup tags (e.g., `[grey]-h, --help[/]`) or use a `Markup` object with escaped content.

---

### 14. `AnthropicLlmClient` Drops the Anthropic `stop_reason` and `stop_sequence`

**File:** [AnthropicLlmClient.cs](src/IronPaw.AgentCore.Providers/AnthropicLlmClient.cs)

The client never reads `stop_reason` from the response. When Anthropic stops generation due to `tool_use`, the `stop_reason` field is `"tool_use"`, not `"end_turn"`. Ignoring this means the client has no way to distinguish a tool-stopped response from a naturally-completed one, which can matter for implementing streaming or future features like extended thinking.

---

### 15. No Structured Logging — Diagnostics Events Only

The project uses `IAgentEventSink` for diagnostics, which is appropriate for the agent's own events. However, there is no structured logging (e.g., `Microsoft.Extensions.Logging`) available to library code. Implementors of tools and add-ins have no way to emit diagnostic output without taking a dependency on `IAgentEventSink`, which is a higher-level concern. Adding an `ILogger` or `ILoggerFactory` parameter option to `ToolExecutionContext` (or via a separate logging abstraction) would make the library more production-friendly.

---

## Minor Observations

These are small things that don't constitute bugs but are worth noting.

**`AgentRunRequest.ContextWindowChars` is character-based, not token-based.** The default of `32_768` chars is a rough approximation. Different models and languages have very different char-to-token ratios. The name could better signal this is a character budget, and the documentation should note it's an approximation.

**`CliOptions.ResolvePromptAsync` mixes concerns.** Parsing, validation, and prompt resolution are all on `CliOptions`. `ResolvePromptAsync` in particular interacts with `Console.In` and the file system. Splitting this into a `PromptResolver` would make both easier to test.

**`BuildOrchestrator` in `Program.cs` is manual DI.** This is pragmatic for a CLI and not a problem per se, but the same wiring for a future ASP.NET or Worker Service host would need to be duplicated. A `IronPawBuilder` helper or `AddIronPaw()` extension for `IServiceCollection` would reduce duplication if more hosts are added.

**`RuleBasedLlmClient` couples to specific tool names.** The local/test provider's pattern matching is hardcoded to specific tool behaviors. This is fine for a dev-mode stub but means adding new built-in tools requires updating the stub too.

**`SkillAddinLoader` uses reflection without a timeout.** Assembly loading and `ISkillAddin` discovery via reflection is synchronous and has no timeout. A malformed or adversarial add-in assembly could hang the startup path.

---

## Summary Table

| # | Severity | Area | Issue |
|---|----------|------|-------|
| 1 | Medium | Orchestrator | `ToolExecutor`/`ToolSchemaNormalizer` are concrete dependencies |
| 2 | High | Providers | LLM clients buffer full response, no real streaming |
| 3 | High | Providers | Anthropic `max_tokens` hard-coded to 2048 |
| 4 | High | Orchestrator | Consecutive assistant messages when text + tool call in same round |
| 5 | Medium | Orchestrator | `TranscriptSanitizer` appends synthetic results at wrong position |
| 6 | Medium | Orchestrator | `SessionLaneScheduler` leaks `SemaphoreSlim` objects |
| 7 | Medium | Persistence | `JsonTranscriptStore` single gate serializes all sessions |
| 8 | Low | Orchestrator | Cache token counts replaced instead of accumulated |
| 9 | Low | Orchestrator | `maxRounds` is a hard-coded magic number |
| 10 | Low | Orchestrator | `ToolResultTruncator` per-message, not per-total budget |
| 11 | Low | Abstractions | `ILLMClient` should be `ILlmClient` (CA1715) |
| 12 | Low | Orchestrator | `ReplaceAsync` called twice per run unnecessarily |
| 13 | Low | CLI | Backtick markdown in Spectre table renders literally |
| 14 | Low | Providers | `stop_reason` from Anthropic is ignored |
| 15 | Low | General | No `ILogger` integration for library-level diagnostics |
