# IronPaw.Cli

`IronPaw.Cli` is the terminal host for IronPaw.
It provides a Spectre.Console command-center UI on top of the same headless orchestration stack used by Desktop.

## Features

- ANSI-rendered command center with:
  - banner
  - run configuration panel
  - colorized event stream
  - final result panel
- Provider selection (`local`, `openai`, `ollama`, `lmstudio`, `anthropic`)
- Session/model/profile selection
- Prompt via argument, file, stdin, or interactive prompt
- Interactive REPL with slash commands for memory, sessions, and task review
- Runtime skill add-in discovery and dynamic skill creation
- Skill catalog listing (`--list-skills`)
- Multi-agent delegation support (Coding/Full profiles) with `@agentId` event attribution

## Main files

- `Program.cs` — entry point, interactive REPL loop, single-run execution
- `CliOptions.cs` — argument parsing and validation (`Parse`, `TryValidate`, `ResolvePromptAsync`, `PrintHelp`)
- `AppBootstrapper.cs` — composition root wiring all dependencies (including `IAgentRegistry` and agent tools)
- `ConsoleUi.cs` — Spectre.Console rendering for banners, settings, diagnostics, results, memory, and run history
- `AnsiConsoleEventSink.cs` — `IAgentEventSink` that formats agent events with Spectre.Console markup (displays `@agentId` prefix for sub-agent events)
- `InteractiveCommandHandler.cs` — slash command dispatch (`/help`, `/settings`, `/provider`, `/model`, `/session`, `/remember`, `/forget`, `/memories`, `/tasks`, `/review`)
- `ProviderResolver.cs` — provider selection and API key prompting
- `CliDefaults.cs` — CLI-specific constants (delegates to `ProviderDefaults` for shared config)

## CLI options

- `-h`, `--help`
- `-p`, `--provider <name>`
- `-m`, `--model <id>`
- `-s`, `--session <key>`
- `--prompt <text>`
- `--prompt-file <path>`
- `--profile <Minimal|Coding|Messaging|Full>`
- `--auto-create-skills` (requires `--profile Full`)
- `--list-skills`
- `--no-events`

## Usage

Run with local provider:

```powershell
dotnet run --project src/IronPaw.Cli -- --provider local --prompt "Create a plan and write it to Plan.md"
```

List discovered skills:

```powershell
dotnet run --project src/IronPaw.Cli -- --list-skills
```

Live provider examples:

```powershell
dotnet run --project src/IronPaw.Cli -- --provider openai --model gpt-4o-mini --prompt "Summarize current project status"
dotnet run --project src/IronPaw.Cli -- --provider anthropic --model claude-3-5-haiku-latest --prompt "Summarize current project status"
dotnet run --project src/IronPaw.Cli -- --provider ollama --model llama3.2 --prompt "Summarize current project status"
dotnet run --project src/IronPaw.Cli -- --provider lmstudio --model local-model --prompt "Summarize current project status"
```

## Interactive mode

Launch without `--prompt` and `--prompt-file` to keep the CLI open and run multiple requests until you quit.

Commands:

| Command | Description |
|---------|-------------|
| `/help` | Show the command list |
| `/settings` | Display current provider, model, session, and profile |
| `/provider` | Switch LLM provider interactively |
| `/model` | Change the model for the current provider |
| `/session` | Switch to a different session |
| `/remember` | Save a global memory |
| `/forget` | Remove a memory by ID |
| `/memories` | List all global and session memories |
| `/tasks` | Show recent run history |
| `/review` | View details of a specific past run |
| `quit` / `exit` / `/quit` / `:q` | Exit the REPL |

If required provider settings are missing (for example `OPENAI_API_KEY` or `ANTHROPIC_API_KEY`), the CLI prompts for them instead of failing immediately.

## Add-in skill discovery paths

- `./addins/skills`
- `%LOCALAPPDATA%/IronPaw/skills`

## Environment variables

- `OPENAI_API_KEY`
- `ANTHROPIC_API_KEY`
- `OLLAMA_BASE_URL` (default: `http://localhost:11434`)
- `OLLAMA_API_KEY` (optional; default `ollama`)
- `LMSTUDIO_BASE_URL` (default: `http://localhost:1234`)
- `LMSTUDIO_API_KEY` (optional; default `lm-studio`)
- `IRONPAW_CODEGEN_PROVIDER` (optional; override provider for the `codegen` specialist)
- `IRONPAW_CODEGEN_MODEL` (optional; override model for the `codegen` specialist)
- `IRONPAW_REVIEWER_PROVIDER` (optional; override provider for the `reviewer` specialist)
- `IRONPAW_REVIEWER_MODEL` (optional; override model for the `reviewer` specialist)

Example split-model setup (PowerShell):

```powershell
$env:IRONPAW_CODEGEN_PROVIDER="openai"
$env:IRONPAW_CODEGEN_MODEL="gpt-4.1"
$env:IRONPAW_REVIEWER_PROVIDER="anthropic"
$env:IRONPAW_REVIEWER_MODEL="claude-3-5-sonnet-latest"
dotnet run --project src/IronPaw.Cli -- --provider openai --model gpt-4o-mini --prompt "Implement and review a new parser"
```
