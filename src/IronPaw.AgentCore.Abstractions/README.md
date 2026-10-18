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
  - memory management
  - run history
- Provide a single source of truth for cross-project data models.

## Main files

- `Contracts.cs`
- `Workflows.cs`: durable invocation checkpoints, human decisions, controls, and budgets.
- `TaskGraphs.cs`: proposed task graphs, resource declarations, artifact requirements,
  acceptance criteria, and structured task results with revision-bound evidence.
- `TaskGraphExecution.cs`: accepted graph checkpoints, fenced claims, trusted host execution
  and verification interfaces, acceptance receipts, and atomic graph store commands.
- `AgentTaskExecution.cs`: immutable agent bindings, shared-budget root/child identities,
  pinned predecessor receipts, workspace preparation/capture interfaces and safe task recovery commands.

Task graph definitions use immutable collections. Validate and snapshot proposals with
`TaskGraphValidator` from `IronPaw.AgentCore.Domain` before accepting them; the validator also copies the mutable collection
interfaces inherited from `ToolSelectionPolicy`. Task identities remain separate from run IDs,
so several invocations can eventually execute or retry the same logical task.

Task permissions and local budgets declare restrictions. They cannot grant authority beyond
the caller/root or replace the durable shared budget ledger. The host-provided graph executor
must enforce these declarations. Core's `AgentTaskGraphRunner` supplies a restricted scoped-file
adapter using the same CSharpDB ledger for graph and invocation state.
`WorkflowTaskResult` is separate from `AgentRunResult`; an execution loop finishing
does not establish task acceptance. Results bind their graph ID, accepted-definition fingerprint
and task ID. Evidence records describe claims for an exact revision,
which a trusted host must still verify against the actual artifacts and verification run.
Only a separate `ITaskAcceptanceVerifier` decision, persisted against the exact task attempt,
result digest and revision, lets the graph store mark a task accepted and unlock dependencies.
`AwaitingVerification` describes a candidate without fabricated passing evidence.
`TaskArtifactVerificationRule` explicitly maps named criteria to file presence/integrity checks;
semantic correctness and build/test outcomes require separate trusted host verification.
`TaskAttemptInputs` pins the consumer, base and exact accepted ancestor receipts before provider
admission. `AcceptedChanges` graphs retain those pins across recovery, compose sealed predecessor
changes and bind the resulting baseline to each captured revision. `BaseCommitOnly` retains legacy
independent snapshots; an accepted graph cannot change its dependency mode.

## Key contract groups

- **Identity and events**
  - `SessionKey`, `RunId`
  - `AgentEvent`, `AgentEventStream`, `AgentEventSeverity`
  - `IAgentEventSink`, `NullAgentEventSink`
- **Tools**
  - `ITool`, `ToolDescriptor`, `ToolCall`, `ToolResult`, `ToolExecutionContext`
  - `IToolRegistry`, `IToolRegistrar`, `IToolExecutor`, `IToolSchemaNormalizer`
  - `IToolHookRunner`, `ToolMiddlewareContext`, `NullToolHookRunner`
  - `ToolSelectionPolicy`, `ToolProfile`
- **Skills / add-ins**
  - `ISkill`, `ISkillAddin`
  - `SkillDescriptor`, `SkillCall`, `SkillResult`, `SkillExecutionContext`
- **LLM provider interface**
  - `ILlmClient`
  - `LlmRequest`, `LlmToolDefinition`, `LlmDelta` (+ derived deltas)
- **Agent orchestration**
  - `IAgentOrchestrator`
  - `AgentRunRequest`, `AgentRunResult`
- **Multi-agent**
  - `AgentDefinition`, `IAgentRegistry`
- **Persistence**
  - `ISessionStore`, `SessionEntry`
  - `ITranscriptStore`
- **Memory**
  - `IMemoryStore`, `MemoryEntry`, `MemoryScope`
- **Run history**
  - `IRunHistoryStore`, `RunSummary`
- **Safety/runtime helpers**
  - `ISessionLaneScheduler`
  - `ITranscriptSanitizer`
  - `IToolResultTruncator`
  - `ISystemPromptBuilder`
- **Messaging**
  - `ChatMessage`, `ChatRole`
- **Utility**
  - `EventData`

## Dependency rules

- This project should not depend on concrete runtime implementations.
- Other projects (`AgentCore`, `Tools`, `Providers`, `Persistence`, `Cli`, `Desktop`, add-ins) depend on this project.

## Notes for contributors

- Keep contracts backward-compatible where possible.
- Avoid embedding implementation concerns into abstractions.
- If you add new contracts, group them with existing related sections in `Contracts.cs`.
