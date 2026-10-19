# Getting Started with IronPaw

This guide walks you through building, running, and understanding your first IronPaw interaction.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- A terminal (PowerShell, Bash, or Windows Terminal)
- **Optional**: An API key for [OpenAI](https://platform.openai.com/), [Anthropic](https://console.anthropic.com/), or a local model runner ([Ollama](https://ollama.com/), [LM Studio](https://lmstudio.ai/))

## Build the solution

```powershell
git clone <your-repo-url>
cd IronPaw
dotnet build IronPaw.sln
```

## Your first run (no API key required)

IronPaw ships with a `local` provider — a deterministic rule-based engine that exercises the full pipeline without any network calls. This is perfect for exploring the framework.

```powershell
dotnet run --project src/IronPaw.Cli -- --provider local --prompt "Create a project plan and write it to Plan.md"
```

You will see:
1. **Lifecycle events** — `run_started`, `llm_round_started`, `run_completed`
2. **Tool calls** — the local provider detects the "write" intent and emits a `write_file` tool call
3. **Tool results** — the file is written to disk
4. **Final response** — a summary panel rendered in the terminal

Check the output file:

```powershell
cat Plan.md
```

## What just happened?

```
User prompt
    ↓
AgentOrchestrator.RunAsync()
    ↓
Session lane acquired (one run per session at a time)
    ↓
System prompt built (identity, tools list, memory)
    ↓
LLM round 1 → provider returns tool call (write_file)
    ↓
ToolExecutor runs the tool → file written
    ↓
LLM round 2 → provider returns final text
    ↓
Transcript + session persisted to disk
    ↓
Result rendered to terminal
```

Application data is saved under `%LOCALAPPDATA%\IronPaw\state\application.cdb`: session metadata,
ordered transcripts, global/session memories, run summaries and agent messages. Workflow journals
remain separate under `state/workflows/<workspace-id>/workflows.cdb`.

Legacy JSON imports once on first application access, preserving the originals. Close older
IronPaw instances before migrating. SQLite data is preserved but not imported. See
[storage and migration](application-storage.md).

## Running with a live LLM

Set your API key and switch the provider:

```powershell
# OpenAI
$env:OPENAI_API_KEY = "sk-..."
dotnet run --project src/IronPaw.Cli -- --provider openai --prompt "Summarize what IronPaw is"

# Anthropic
$env:ANTHROPIC_API_KEY = "sk-ant-..."
dotnet run --project src/IronPaw.Cli -- --provider anthropic --prompt "Summarize what IronPaw is"

# Ollama (must be running locally on port 11434)
dotnet run --project src/IronPaw.Cli -- --provider ollama --model llama3.2 --prompt "Hello"

# LM Studio (must be running locally on port 1234)
dotnet run --project src/IronPaw.Cli -- --provider lmstudio --prompt "Hello"
```

## Interactive mode

Start an interactive session by omitting the `--prompt` flag:

```powershell
dotnet run --project src/IronPaw.Cli -- --provider openai
```

You get a REPL where you can chat, use slash commands (`/help`, `/memories`, `/tasks`), and switch sessions. See the [Interactive Mode tutorial](tutorials/interactive-mode.md) for details.

## Running the Desktop app

```powershell
dotnet run --project src/IronPaw.Desktop
```

The WPF app provides a visual interface with provider selection, real-time event streaming, and response display. Windows only.

## Dynamic skill creation (auto-create-skills)

With the `Full` profile and `--auto-create-skills`, the agent can create new tools at runtime by compiling C# via Roslyn. Created skills are persisted to `%LOCALAPPDATA%\IronPaw\skills` and are available across future sessions — no restart needed.

```powershell
# One-shot: ask the agent to build a tool for you
dotnet run --project src/IronPaw.Cli -- --provider openai --profile Full --auto-create-skills --prompt "Create a skill that converts Markdown to plain text"

# Interactive: the agent can create tools on demand as you work
dotnet run --project src/IronPaw.Cli -- --provider openai --profile Full --auto-create-skills
```

After creation, verify the skill was registered:

```powershell
dotnet run --project src/IronPaw.Cli -- --list-skills
```

The persisted skill DLLs live in `%LOCALAPPDATA%\IronPaw\skills` and are loaded automatically on every subsequent launch.

## Listing available skills

```powershell
dotnet run --project src/IronPaw.Cli -- --list-skills
```

This shows all discovered skill add-ins and their tools.

## Running tests

```powershell
dotnet test IronPaw.sln
```

## Next steps

| Goal | Guide |
|------|-------|
| Master the interactive CLI | [Interactive Mode](tutorials/interactive-mode.md) |
| Set up LLM providers | [Using Providers](tutorials/using-providers.md) |
| Use memory and task review | [Memory & Review](tutorials/memory-and-review.md) |
| Create your own skill add-in | [Creating a Skill Add-in](tutorials/creating-a-skill-addin.md) |
| Embed IronPaw in your own app | [Embedding IronPaw](tutorials/embedding-ironpaw.md) |
| Full worked example | [Weather Skill Example](examples/weather-skill.md) |
