# IronPaw.AgentCore.Abstractions

`IronPaw.AgentCore.Abstractions` contains the shared contracts used by every runtime project in IronPaw.

## Purpose

- Define stable, implementation-free interfaces and DTOs for:
  - orchestration
  - events
  - tools
  - skills/add-ins
  - LLM streaming
  - session/transcript persistence
- Provide a single source of truth for cross-project data models.

## Main file

- `Contracts.cs`

All abstractions currently live in this file.

## Key contract groups

- **Identity and events**
  - `SessionKey`, `RunId`
  - `AgentEvent`, `AgentEventStream`, `AgentEventSeverity`
  - `IAgentEventSink`
- **Tools**
  - `ITool`, `ToolDescriptor`, `ToolCall`, `ToolResult`
  - `IToolRegistry`, `IToolHookRunner`
  - `ToolSelectionPolicy`, `ToolProfile`
- **Skills / add-ins**
  - `ISkill`, `ISkillAddin`
  - `SkillDescriptor`, `SkillCall`, `SkillResult`
- **LLM provider interface**
  - `ILLMClient`
  - `LlmRequest`, `LlmToolDefinition`, `LlmDelta` (+ derived deltas)
- **Agent orchestration**
  - `IAgentOrchestrator`
  - `AgentRunRequest`, `AgentRunResult`
- **Persistence**
  - `ISessionStore`, `SessionEntry`
  - `ITranscriptStore`
- **Safety/runtime helpers**
  - `ISessionLaneScheduler`
  - `ITranscriptSanitizer`
  - `IToolResultTruncator`

## Dependency rules

- This project should not depend on concrete runtime implementations.
- Other projects (`AgentCore`, `Tools`, `Providers`, `Persistence`, `Cli`, `Desktop`, add-ins) depend on this project.

## Notes for contributors

- Keep contracts backward-compatible where possible.
- Avoid embedding implementation concerns into abstractions.
- If you add new contracts, group them with existing related sections in `Contracts.cs`.
