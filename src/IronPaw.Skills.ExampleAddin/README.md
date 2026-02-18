# IronPaw.Skills.ExampleAddin

`IronPaw.Skills.ExampleAddin` is a sample skill add-in assembly demonstrating the plugin contract.

## Purpose

- Show how to implement `ISkillAddin` and `ISkill`.
- Provide a working add-in that is automatically discovered by CLI/Desktop hosts.

## Implemented skills

- `text_stats`
  - input: `text`
  - output: JSON with `characters`, `words`, `lines`
- `slugify`
  - input: `text`, optional `separator`
  - output: URL-safe lowercase slug string

## Main file

- `ExampleSkillAddin.cs`

## Build behavior

After project build, the add-in DLL is copied to:
- `./addins/skills`

This is configured in `IronPaw.Skills.ExampleAddin.csproj` via `CopyBuiltAddin` target.

## Creating your own add-in

1. Create a class library.
2. Reference `IronPaw.AgentCore.Abstractions`.
3. Implement `ISkillAddin` and return one or more `ISkill` instances.
4. Place/copy resulting DLL into:
   - `./addins/skills` or
   - `%LOCALAPPDATA%/IronPaw/skills`
5. Run:
   - `dotnet run --project src/IronPaw.Cli -- --list-skills`

See `docs/skills-addins.md` for additional details.
