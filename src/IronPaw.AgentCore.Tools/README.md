# IronPaw.AgentCore.Tools

`IronPaw.AgentCore.Tools` provides the tool subsystem: built-in tools, tool policy filtering, schema normalization, execution middleware, and dynamic skill add-in loading.

## Responsibilities

- Define built-in tools used by the agent runtime
- Resolve effective tool list from policy/profile constraints
- Normalize tool schemas per provider
- Execute tools through hook-aware middleware
- Load skill add-ins (`ISkillAddin`) from external assemblies and expose them as tools
- Compile and register skills at runtime via Roslyn

## Built-in tools

### File system (`group:fs`)

- `list_files` — list directory contents
- `read_file` — read file contents
- `write_file` — write files to disk

Key files: `ListFilesTool.cs`, `ReadFileTool.cs`, `WriteFileTool.cs`

### System (`group:system`)

- `current_time` — returns the current time
- `save_memory` — save a global or session-scoped memory
- `recall_memory` — search saved memories by keyword
- `forget_memory` — remove a memory by ID
- `review_runs` — list recent run summaries
- `review_run_detail` — get full details of a specific past run

Key files: `CurrentTimeTool.cs`, `SaveMemoryTool.cs`, `RecallMemoryTool.cs`, `ForgetMemoryTool.cs`, `ReviewRunsTool.cs`, `ReviewRunDetailTool.cs`

### Messaging (`group:messaging`)

- `send_agent_message` — send a scoped message to another registered agent
- `read_agent_messages` — read pending scoped messages addressed to the current agent

Key files: `SendAgentMessageTool.cs`, `ReadAgentMessagesTool.cs`

Messaging tools use `IAgentMessageBus` and enforce recipient authorization through `AgentDefinition.AllowedMessageSenders`.

### Runtime (`group:runtime`)

- `create_skill` — compile C# source code into a skill at runtime (Full profile only)

Key files: `CreateSkillTool.cs`, `RoslynSkillCompiler.cs`, `JsonSchemaBuilder.cs`

### Skills (`group:skills`)

All skill add-in tools are registered with the `skill_<name>` prefix and placed in this group.

### Agents (`group:agents`)

- `invoke_agent` — delegates a task to a specialist agent (Full profile only)
- `list_agents` — returns all registered agent definitions as JSON

Key files: `InvokeAgentTool.cs`, `ListAgentsTool.cs`, `AgentPrefixedEventSink.cs`, `InMemoryAgentRegistry.cs`

`InvokeAgentTool` constructs a child `AgentRunRequest` with an ephemeral `SessionKey` for transcript isolation, increments `SubagentDepth`, and recursively calls `IAgentOrchestrator.RunAsync`. The child agent's `FinalAssistantMessage` is returned as a `ToolResult` to the parent.

`AgentPrefixedEventSink` is a decorator that stamps `AgentId` on every event emitted by a sub-agent, enabling per-agent attribution in the event stream.

`InMemoryAgentRegistry` implements `IAgentRegistry` — a thread-safe, dictionary-backed store for `AgentDefinition` records used by both tools.

## Tool policy and registration

- `InMemoryToolRegistry.cs`
  - stores and resolves tool set; supports dynamic registration via `IToolRegistrar`
- `ToolPolicyEvaluator.cs`
  - profile and allow/deny filtering
  - profiles: `Minimal`, `Coding`, `Messaging`, `Full`
  - groups: `group:fs`, `group:system`, `group:messaging`, `group:runtime`, `group:skills`, `group:agents`

## Execution and schema

- `ToolExecutor.cs`
  - runs hooks and normalizes failures into `ToolResult`
- `ToolSchemaNormalizer.cs`
  - provider-aware schema shaping (`openai`, `gemini`, fallback)

## Skills as add-ins

- `SkillAddinLoader.cs`
  - loads `.dll` files and discovers `ISkillAddin` implementations
- `SkillToolAdapter.cs`
  - adapts each `ISkill` to an `ITool` (`skill_<name>`)
- `SkillAddinLoadResult.cs`
  - diagnostics + loaded skill metadata

Default add-in discovery paths are controlled by host projects (CLI/Desktop), currently:
- `./addins/skills`
- `%LOCALAPPDATA%/IronPaw/skills`

## Utility

- `BuiltInToolFactory.cs`
  - creates standard tool set; accepts optional `IMemoryStore`, `IRunHistoryStore`, and additional tools
- `WorkspacePathResolver.cs`
  - resolves and validates paths relative to the workspace root

## Tools vs Skills — Conceptual Guide

### What is a Tool?

A **Tool** is the fundamental execution unit that an agent can invoke. It implements `ITool` and is what the LLM directly sees and calls.

- Has a `ToolDescriptor` (name, description, JSON parameter schema, group, owner-only flag) and an `ExecuteAsync` method.
- Built-in examples: `read_file`, `write_file`, `current_time`, `list_files`, `save_memory`, `review_runs`.
- Grouped by category: `group:fs`, `group:system`, `group:messaging`, `group:runtime`, `group:skills`.
- Compiled into the application as part of the core.

### What is a Skill?

A **Skill** is an external, plugin-based extension mechanism. It implements `ISkill`, packaged inside an `ISkillAddin`.

- Has a simpler `SkillDescriptor` (name, description, input schema) and an `ExecuteAsync` method.
- Loaded dynamically at runtime from external `.dll` assemblies discovered in configurable directories (e.g. `./addins/skills`, `%LOCALAPPDATA%/IronPaw/skills`).
- Can also be created at runtime via the `create_skill` tool (Full profile, Roslyn compilation).
- Example: `ExampleSkillAddin` provides `TextStatsSkill` and `SlugifySkill`.

### The Adapter Pattern

Skills never interact directly with the agent. `SkillToolAdapter` wraps each `ISkill` into an `ITool`:

```
ISkill  →  SkillToolAdapter (implements ITool)  →  Agent sees it as a regular tool
```

- Skill names are prefixed: `skill_text_stats`, `skill_slugify`.
- All adapted skills are placed in `group:skills`.
- The `AgentOrchestrator` treats them identically to built-in tools — same resolution, policy filtering, and execution path.

### Comparison

| Aspect | Tool | Skill |
|---|---|---|
| **Deployment** | Compiled into the application | External DLLs or runtime-compiled |
| **Audience** | Core system developers | Third-party / end-user extensibility |
| **Access control** | Per-group + `OwnerOnly` flag | Collectively under `group:skills` |
| **Naming** | Direct (e.g. `read_file`) | Prefixed (e.g. `skill_slugify`) |
| **Descriptor** | `ToolDescriptor` (name, description, schema, group, owner-only) | `SkillDescriptor` (name, description, input schema) |

### Why This Design?

- **Separation of concerns**: Core tools handle essential operations (file I/O, time, memory); skills add domain-specific functionality.
- **Extensibility**: New functionality can be added via external DLLs or runtime compilation without recompiling the core.
- **Unified execution model**: The agent sees all tools uniformly, regardless of origin.
- **Policy-based access control**: Tools and skills are filtered by profile and policy under a single mechanism.
