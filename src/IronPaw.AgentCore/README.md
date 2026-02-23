# IronPaw.AgentCore

`IronPaw.AgentCore` is the headless orchestration engine.
It runs the agent loop, coordinates tools, persists state, and emits event streams.

## Responsibilities

- Orchestrate one run with a stable `RunId`
- Serialize work per session via lane scheduler
- Build system prompt (including persistent memory injection and agent delegation guidance)
- Execute LLM rounds and process deltas
- Execute tool calls (including recursive agent invocations via `invoke_agent`)
- Persist transcripts and session usage
- Record completed runs in run history
- Apply transcript repair and tool-result truncation safety steps
- Publish lifecycle/assistant/tool/diagnostic events with optional `AgentId` attribution

## Main files

- `AgentOrchestrator.cs`
  - main run loop and recovery boundaries
  - accepts optional `IMemoryStore` and `IRunHistoryStore`
- `SessionLaneScheduler.cs`
  - per-session serialization
- `DefaultSystemPromptBuilder.cs`
  - baseline system prompt generation with memory injection and agent delegation guidance
- `TranscriptSanitizer.cs`
  - tool-call/tool-result repair
- `ToolResultTruncator.cs`
  - context-budget truncation for oversized tool results

## Execution flow (simplified)

1. Receive `AgentRunRequest`
2. Acquire session lane
3. Load and sanitize transcript
4. Load global + session memories (skipped for sub-agent invocations where `ParentRunId` is set)
5. Build prompt and resolve tools
6. Stream LLM output deltas
7. Execute tool calls and append tool results
   - If the tool is `invoke_agent`, a child `AgentRunRequest` is constructed with an ephemeral `SessionKey` and incremented `SubagentDepth`, and the orchestrator is called recursively
8. Re-sanitize/truncate transcript and persist
9. Persist usage/session metadata
10. Record run summary to history (if `IRunHistoryStore` provided)
11. Return `AgentRunResult`

## Dependencies

- `IronPaw.AgentCore.Abstractions`
- `IronPaw.AgentCore.Tools` (tool execution/schema normalization)
- `IronPaw.AgentCore.Persistence`
- `IronPaw.AgentCore.Providers`

## Multi-agent support

The orchestrator is reused as the execution engine for multi-agent delegation. When the `invoke_agent` tool is called:

- A child `AgentRunRequest` is constructed with the specialist agent's system prompt, tool policy, and an ephemeral `SessionKey` for transcript isolation.
- `SubagentDepth` is incremented to prevent infinite recursion (max depth: 3).
- The child's `FinalAssistantMessage` is returned as a `ToolResult` to the parent agent.
- All events from sub-agents carry an `AgentId` field for attribution.
- Sub-agents skip memory loading (they operate statelessly by default).

## Extension points

- Swap `ILlmClient` with different provider routers/adapters.
- Extend tool set via `IToolRegistry`.
- Register specialist agents via `IAgentRegistry` for multi-agent delegation.
- Replace prompt builder/sanitizer/truncator with alternative implementations.
- Provide `IMemoryStore` / `IRunHistoryStore` for persistent memory and task review.
