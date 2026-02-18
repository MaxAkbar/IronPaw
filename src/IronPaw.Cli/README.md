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
- Runtime skill add-in discovery
- Skill catalog listing (`--list-skills`)

## Main file

- `Program.cs`

## CLI options

- `-h`, `--help`
- `-p`, `--provider <name>`
- `-m`, `--model <id>`
- `-s`, `--session <key>`
- `--prompt <text>`
- `--prompt-file <path>`
- `--profile <Minimal|Coding|Messaging|Full>`
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

Live provider example:

```powershell
dotnet run --project src/IronPaw.Cli -- --provider openai --model gpt-4o-mini --prompt "Summarize current project status"
dotnet run --project src/IronPaw.Cli -- --provider ollama --model llama3.2 --prompt "Summarize current project status"
dotnet run --project src/IronPaw.Cli -- --provider lmstudio --model local-model --prompt "Summarize current project status"
```

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

## Interactive mode

Launch without `--prompt` and `--prompt-file` to keep the CLI open and run multiple requests until you quit.

Commands:

- `quit` / `exit` / `/quit`
- `/help`
- `/settings`
- `/provider`
- `/model`
- `/session`

If required provider settings are missing (for example `OPENAI_API_KEY` or `ANTHROPIC_API_KEY`), the CLI prompts for them instead of failing immediately.
