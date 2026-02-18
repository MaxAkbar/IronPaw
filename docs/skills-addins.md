# Skills Add-ins

IronPaw discovers skill add-ins from:
- `./addins/skills`
- `%LOCALAPPDATA%/IronPaw/skills`

Each add-in is a .NET class library that references `IronPaw.AgentCore.Abstractions` and exports one or more `ISkillAddin` types.

## Contract

```csharp
public sealed class MyAddin : ISkillAddin
{
    public string AddinId => "my.addin";
    public string Description => "My skill pack";

    public IReadOnlyList<ISkill> CreateSkills() => [ new MySkill() ];
}
```

`ISkill` exposes:
- `SkillDescriptor` (`Name`, `Description`, `InputSchema`)
- `ExecuteAsync(...)`

At runtime, each skill is adapted into a tool with name format `skill_<skill_name>`.

## Example

See:
- `src/IronPaw.Skills.ExampleAddin/ExampleSkillAddin.cs`

The example project auto-copies its output DLL to `addins/skills` after build.
