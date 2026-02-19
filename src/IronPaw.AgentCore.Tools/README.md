# IronPaw.AgentCore.Tools

`IronPaw.AgentCore.Tools` provides the tool subsystem: built-in tools, tool policy filtering, schema normalization, execution middleware, and dynamic skill add-in loading.

## Responsibilities

- Define built-in tools used by the agent runtime
- Resolve effective tool list from policy/profile constraints
- Normalize tool schemas per provider
- Execute tools through hook-aware middleware
- Load skill add-ins (`ISkillAddin`) from external assemblies and expose them as tools

## Built-in tools

- `current_time`
- `list_files`
- `read_file`
- `write_file`

Key files:
- `CurrentTimeTool.cs`
- `ListFilesTool.cs`
- `ReadFileTool.cs`
- `WriteFileTool.cs`

## Tool policy and registration

- `InMemoryToolRegistry.cs`
  - stores and resolves tool set
- `ToolPolicyEvaluator.cs`
  - profile and allow/deny filtering
  - groups include:
    - `group:fs`
    - `group:runtime`
    - `group:messaging`
    - `group:system`
    - `group:skills`

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

## Factory

- `BuiltInToolFactory.cs`
  - creates standard tools and accepts optional additional tools (for add-ins)

## Tools vs Skills — Conceptual Guide

### What is a Tool?

A **Tool** is the fundamental execution unit that an agent can invoke. It implements `ITool` and is what the LLM directly sees and calls.

- Has a `ToolDescriptor` (name, description, JSON parameter schema, group, owner-only flag) and an `ExecuteAsync` method.
- Built-in examples: `read_file`, `write_file`, `current_time`, `list_files`.
- Grouped by category: `group:fs`, `group:system`, `group:messaging`, `group:runtime`, `group:skills`.
- Compiled into the application as part of the core.

### What is a Skill?

A **Skill** is an external, plugin-based extension mechanism. It implements `ISkill`, packaged inside an `ISkillAddin`.

- Has a simpler `SkillDescriptor` (name, description, input schema) and an `ExecuteAsync` method.
- Loaded dynamically at runtime from external `.dll` assemblies discovered in configurable directories (e.g. `./addins/skills`, `%LOCALAPPDATA%/IronPaw/skills`).
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
| **Deployment** | Compiled into the application | External DLLs, loaded at runtime |
| **Audience** | Core system developers | Third-party / end-user extensibility |
| **Access control** | Per-group + `OwnerOnly` flag | Collectively under `group:skills` |
| **Naming** | Direct (e.g. `read_file`) | Prefixed (e.g. `skill_slugify`) |
| **Descriptor** | `ToolDescriptor` (name, description, schema, group, owner-only) | `SkillDescriptor` (name, description, input schema) |

### Why This Design?

- **Separation of concerns**: Core tools handle essential operations (file I/O, time); skills add domain-specific functionality.
- **Extensibility**: New functionality can be added via external DLLs without recompiling the core.
- **Unified execution model**: The agent sees all tools uniformly, regardless of origin.
- **Policy-based access control**: Tools and skills are filtered by profile and policy under a single mechanism.
