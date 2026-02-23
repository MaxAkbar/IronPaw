# IronPaw.AgentCore

`IronPaw.AgentCore` is the headless orchestration engine.
It runs the agent loop, coordinates tools, persists state, and emits event streams.

## Responsibilities

- Orchestrate one run with a stable `RunId`
- Serialize work per session via lane scheduler
- Build system prompt (including persistent memory injection)
- Execute LLM rounds and process deltas
- Execute tool calls
- Persist transcripts and session usage
- Record completed runs in run history
- Apply transcript repair and tool-result truncation safety steps
- Publish lifecycle/assistant/tool/diagnostic events

## Main files

- `AgentOrchestrator.cs`
  - main run loop and recovery boundaries
  - accepts optional `IMemoryStore` and `IRunHistoryStore`
- `SessionLaneScheduler.cs`
  - per-session serialization
- `DefaultSystemPromptBuilder.cs`
  - baseline system prompt generation with memory injection
- `TranscriptSanitizer.cs`
  - tool-call/tool-result repair
- `ToolResultTruncator.cs`
  - context-budget truncation for oversized tool results

## Execution flow (simplified)

1. Receive `AgentRunRequest`
2. Acquire session lane
3. Load and sanitize transcript
4. Load global + session memories (if `IMemoryStore` provided)
5. Build prompt and resolve tools
6. Stream LLM output deltas
7. Execute tool calls and append tool results
8. Re-sanitize/truncate transcript and persist
9. Persist usage/session metadata
10. Record run summary to history (if `IRunHistoryStore` provided)
11. Return `AgentRunResult`

## Dependencies

- `IronPaw.AgentCore.Abstractions`
- `IronPaw.AgentCore.Tools` (tool execution/schema normalization)
- `IronPaw.AgentCore.Persistence`
- `IronPaw.AgentCore.Providers`

## Extension points

- Swap `ILlmClient` with different provider routers/adapters.
- Extend tool set via `IToolRegistry`.
- Replace prompt builder/sanitizer/truncator with alternative implementations.
- Provide `IMemoryStore` / `IRunHistoryStore` for persistent memory and task review.
