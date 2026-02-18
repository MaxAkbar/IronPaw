# IronPaw.AgentCore

`IronPaw.AgentCore` is the headless orchestration engine.  
It runs the agent loop, coordinates tools, persists state, and emits event streams.

## Responsibilities

- Orchestrate one run with a stable `RunId`
- Serialize work per session via lane scheduler
- Build system prompt
- Execute LLM rounds and process deltas
- Execute tool calls
- Persist transcripts and session usage
- Apply transcript repair and tool-result truncation safety steps
- Publish lifecycle/assistant/tool/diagnostic events

## Main files

- `AgentOrchestrator.cs`
  - main run loop and recovery boundaries
- `SessionLaneScheduler.cs`
  - per-session serialization
- `DefaultSystemPromptBuilder.cs`
  - baseline system prompt generation
- `TranscriptSanitizer.cs`
  - tool-call/tool-result repair
- `ToolResultTruncator.cs`
  - context-budget truncation for oversized tool results

## Execution flow (simplified)

1. Receive `AgentRunRequest`
2. Acquire session lane
3. Load and sanitize transcript
4. Build prompt and resolve tools
5. Stream LLM output deltas
6. Execute tool calls and append tool results
7. Re-sanitize/truncate transcript and persist
8. Persist usage/session metadata
9. Return `AgentRunResult`

## Dependencies

- `IronPaw.AgentCore.Abstractions`
- `IronPaw.AgentCore.Tools` (tool execution/schema normalization)
- `IronPaw.AgentCore.Persistence`
- `IronPaw.AgentCore.Providers`

## Extension points

- Swap `ILLMClient` with different provider routers/adapters.
- Extend tool set via `IToolRegistry`.
- Replace prompt builder/sanitizer/truncator with alternative implementations.
