# Tutorial: Creating a Skill Add-in

Skill add-ins let you extend IronPaw with custom tools that the agent can call. This tutorial walks through creating one from scratch.

## What is a skill add-in?

A skill add-in is a .NET class library that:
1. References `IronPaw.AgentCore.Abstractions`
2. Exports a class implementing `ISkillAddin`
3. Provides one or more `ISkill` implementations
4. Gets discovered at runtime and its skills become callable tools

At runtime, each skill is registered as a tool named `skill_<skill_name>` in the `group:skills` group.

## Step 1: Create the project

```powershell
mkdir src/IronPaw.Skills.MyAddin
cd src/IronPaw.Skills.MyAddin

dotnet new classlib -n IronPaw.Skills.MyAddin --framework net10.0
```

Edit the `.csproj` to reference Abstractions and auto-copy the DLL to the add-ins directory:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\IronPaw.AgentCore.Abstractions\IronPaw.AgentCore.Abstractions.csproj" />
  </ItemGroup>

  <!-- Auto-copy to addins directory after build -->
  <Target Name="CopyToAddins" AfterTargets="Build">
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(SolutionDir)addins\skills" SkipUnchangedFiles="true" />
  </Target>

</Project>
```

## Step 2: Implement the add-in entry point

Create a file `MyAddin.cs`:

```csharp
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Skills.MyAddin;

public sealed class MyAddin : ISkillAddin
{
    public string AddinId => "my.custom.addin";

    public string Description => "My custom skill pack.";

    public IReadOnlyList<ISkill> CreateSkills()
    {
        return [new ReverseTextSkill()];
    }
}
```

The `AddinId` is a unique identifier for your add-in. `CreateSkills()` returns all skills it contributes.

## Step 3: Implement a skill

Add a `ReverseTextSkill.cs`:

```csharp
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Skills.MyAddin;

internal sealed class ReverseTextSkill : ISkill
{
    public SkillDescriptor Descriptor { get; } = new(
        Name: "reverse_text",
        Description: "Reverses the characters in the given text.",
        InputSchema: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                text = new
                {
                    type = "string",
                    description = "The text to reverse."
                }
            },
            required = new[] { "text" }
        }));

    public Task<SkillResult> ExecuteAsync(
        SkillCall call,
        SkillExecutionContext context,
        CancellationToken ct)
    {
        if (!call.Arguments.TryGetProperty("text", out var textElement) ||
            textElement.ValueKind is not JsonValueKind.String)
        {
            return Task.FromResult(SkillResult.Error("Missing required string argument: text"));
        }

        var text = textElement.GetString() ?? string.Empty;
        var reversed = new string(text.Reverse().ToArray());

        return Task.FromResult(SkillResult.Success(reversed));
    }
}
```

Key points:
- **`SkillDescriptor`**: Declares the skill name, description, and JSON Schema for its parameters. This is what the LLM sees when deciding which tools to call.
- **`ExecuteAsync`**: Receives the parsed arguments in `call.Arguments` (a `JsonElement`). Returns `SkillResult.Success(content)` or `SkillResult.Error(message)`.
- **Name mapping**: This skill will be registered as tool `skill_reverse_text`.

## Step 4: Define the input schema

The `InputSchema` is a standard JSON Schema object. The LLM uses it to understand what arguments your skill expects. Common patterns:

```csharp
// Single required string parameter
InputSchema: JsonSerializer.SerializeToElement(new
{
    type = "object",
    properties = new
    {
        text = new { type = "string", description = "Input text." }
    },
    required = new[] { "text" }
})

// Multiple parameters, some optional
InputSchema: JsonSerializer.SerializeToElement(new
{
    type = "object",
    properties = new
    {
        url = new { type = "string", description = "The URL to fetch." },
        timeout = new { type = "integer", description = "Timeout in seconds. Defaults to 30." }
    },
    required = new[] { "url" }
})

// Enum parameter
InputSchema: JsonSerializer.SerializeToElement(new
{
    type = "object",
    properties = new
    {
        format = new
        {
            type = "string",
            description = "Output format.",
            @enum = new[] { "json", "csv", "xml" }
        }
    },
    required = new[] { "format" }
})
```

## Step 5: Build and test

```powershell
dotnet build src/IronPaw.Skills.MyAddin
```

Verify the DLL was copied to the add-ins directory:

```powershell
ls addins/skills/
```

List discovered skills:

```powershell
dotnet run --project src/IronPaw.Cli -- --list-skills
```

You should see your `skill_reverse_text` in the output.

## Step 6: Use it

Start an interactive session and ask the agent to use your skill:

```powershell
dotnet run --project src/IronPaw.Cli -- --provider openai
```

```
> Reverse the text "Hello World"
```

The agent will call `skill_reverse_text` and return `"dlroW olleH"`.

## Adding multiple skills to one add-in

Simply return multiple skills from `CreateSkills()`:

```csharp
public IReadOnlyList<ISkill> CreateSkills()
{
    return
    [
        new ReverseTextSkill(),
        new Base64EncodeSkill(),
        new WordCountSkill()
    ];
}
```

## Discovery paths

IronPaw scans for skill DLLs in two directories:
1. `./addins/skills` — relative to the working directory
2. `%LOCALAPPDATA%\IronPaw\skills` — user-level installation

You can drop DLLs into either location. No registration or configuration file is needed.

## Dynamic skill creation (runtime)

With the `Full` profile, the agent can also create skills at runtime using the `create_skill` tool. It compiles C# source code via Roslyn and registers the resulting skills immediately — no restart needed. See the [system prompt guidance](../../src/IronPaw.AgentCore/DefaultSystemPromptBuilder.cs) for the template the agent follows.

## Reference

See the [ExampleAddin source](../../src/IronPaw.Skills.ExampleAddin/ExampleSkillAddin.cs) for a complete working example with two skills (`text_stats` and `slugify`).

### ISkill interface

```csharp
public interface ISkill
{
    SkillDescriptor Descriptor { get; }
    Task<SkillResult> ExecuteAsync(SkillCall call, SkillExecutionContext context, CancellationToken ct);
}
```

### SkillDescriptor

```csharp
public sealed record SkillDescriptor(
    string Name,           // tool name suffix (prefixed with "skill_")
    string Description,    // shown to the LLM
    JsonElement InputSchema);  // JSON Schema for parameters
```

### SkillResult

```csharp
SkillResult.Success(string content)   // tool result returned to the LLM
SkillResult.Error(string message)     // error result returned to the LLM
```
