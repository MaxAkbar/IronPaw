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
