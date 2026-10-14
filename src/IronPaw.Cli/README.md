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
- Web browsing specialist agent (`web_browser`) for navigation and structured extraction

## Main files

- `Program.cs` — entry point, interactive REPL loop, single-run execution
- `CliOptions.cs` — argument parsing and validation (`Parse`, `TryValidate`, `ResolvePromptAsync`, `PrintHelp`)
- `AppBootstrapper.cs` — DI-based composition root wiring interfaces to runtime implementations (including `IAgentRegistry` and agent tools)
- `ConsoleUi.cs` — Spectre.Console rendering for banners, settings, diagnostics, results, memory, and run history
- `AnsiConsoleEventSink.cs` — `IAgentEventSink` that formats agent events with Spectre.Console markup (displays `@agentId` prefix for sub-agent events)
- `InteractiveCommandHandler.cs` — slash command dispatch (`/help`, `/settings`, `/provider`, `/model`, `/session`, `/remember`, `/forget`, `/memories`, `/scope-runs`, `/web-sessions`, `/tasks`, `/review`)
  - Includes scoped agent inbox inspection via `/agent-messages`
  - Includes web session inspection via `/web-sessions`
- `ProviderResolver.cs` — provider selection and API key prompting
- `CliDefaults.cs` — CLI-specific constants (delegates to `ProviderDefaults` for shared config)

## CLI options

- `-h`, `--help`
- `-p`, `--provider <name>`
- `-m`, `--model <id>`
- `-s`, `--session <key>`
- `--prompt <text>`
- `--prompt-file <path>`
- `--profile <Minimal|Coding|Messaging|Web|Full>`
- `--auto-create-skills` (requires `--profile Full`)
- `--list-skills`
- `--list-workflows` (saved states, child lineage, pending input/approval IDs, and uncertain tool outcomes)
- `--resume <run-id>`
- `--pause <run-id>` (resumable suspension)
- `--cancel <run-id>` (permanent cancellation)
- `--answer-request <request-id> --answer <text>` (with `--resume`)
- `--approve-request <request-id>` or `--deny-request <request-id>` (with `--resume`)
- `--require-approval <tool-name,tool-name>` (new runs only; default: none)
- `--max-total-tokens <n>` (positive 64-bit token limit shared by a new workflow and its descendants)
- `--max-execution-seconds <n>` (positive execution time limit for a new workflow)
- `--no-events`
- `--message-ttl-days <days>` (SQLite message retention window, default `30`)
- `--llm-context-window-tokens <n>` (OpenAI-compatible budget override)
- `--llm-safety-margin-tokens <n>` (OpenAI-compatible budget override)
- `--llm-min-output-tokens-per-request <n>` (OpenAI-compatible budget override)
- `--llm-max-auto-continuation-passes <n>` (OpenAI-compatible budget override)

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
| `/settings` | Display current provider, model, session, profile, and effective OpenAI-compatible token budget settings |
| `/provider` | Switch LLM provider interactively |
| `/model` | Change the model for the current provider |
| `/session` | Switch to a different session |
| `/remember` | Save a global memory |
| `/forget` | Remove a memory by ID |
| `/memories` | List all global and session memories |
| `/scope-runs` | List recent run IDs for current session (for message scope lookup) |
| `/agent-messages` | Inspect scoped agent messages: `/agent-messages <scope-run-id> [agent-id] [--max N] [--consume] [--sender id] [--correlation id]` |
| `/web-sessions` | Inspect web session snapshots (all) or detail by id: `/web-sessions [session-id]` |
| `/tasks` | Show recent run history |
| `/review` | View details of a specific past run |
| `quit` / `exit` / `/quit` / `:q` | Exit the REPL |

If required provider settings are missing (for example `OPENAI_API_KEY` or `ANTHROPIC_API_KEY`), the CLI prompts for them instead of failing immediately.

## Workflow recovery

CLI and Desktop persist workflow checkpoints in CSharpDB for every conversation
store selection. Run these commands from the original workspace:

```powershell
dotnet run --project src/IronPaw.Cli -- --list-workflows
dotnet run --project src/IronPaw.Cli -- --resume <run-id>
```

Listing, pausing, and cancelling require no provider credentials. Resume uses the saved provider, model,
session, permissions, responses, and receipts. The same workspace journal has
one owning host at a time, so close its Desktop or CLI host before opening another.

If an interrupted tool has an unknown outcome, establish its destination state
first. Save the established `ToolResult` in a JSON file, for example:

```json
{"IsError":false,"Content":"Confirmed the file contains the intended content","Trust":"Local"}
```

Then record that outcome and resume:

```powershell
dotnet run --project src/IronPaw.Cli -- --resume <run-id> --reconcile-tool <execution-id> --reconcile-result result.json --reconcile-note "Verified the destination file"
```

The listing shows unresolved execution IDs. Reconciliation records an outcome;
it does not run the action. Model/tool call budgets and known usage remain
consumed across recovery; incomplete provider billing can remain unknown.

## Persistent workflow budgets

Set token and execution time limits when starting a workflow:

```powershell
dotnet run --project src/IronPaw.Cli -- --prompt "Implement the change" --max-total-tokens 20000 --max-execution-seconds 300
```

These limits cover the root workflow and its descendants and survive restart,
pause, and resume. Paused workflows and waiting for human input or approval do
not consume execution time. Limits cannot be replaced by recovery commands.
Omitting a limit leaves that resource unlimited; the existing model/tool call
limits still apply.

Before calling a provider, IronPaw reserves tokens from the shared budget.
Reported usage settles that reservation; an interrupted attempt with unknown
usage retains a conservative charge. `--list-workflows` and final results show
charged and reserved tokens, remaining tokens, and consumed/remaining execution
time for the shared root. Provider billing and the conservative budget charge
can differ when usage is unavailable.

Exhausting a budget returns `BudgetExhausted` and CLI exit code `2`. It does not
reset the ledger or retry an uncertain tool action.

## Human input, approvals, and workflow controls

Use the request IDs shown by `--list-workflows` to answer a pending question or
decide an approval. Each response is saved before execution resumes:

```powershell
dotnet run --project src/IronPaw.Cli -- --resume <run-id> --answer-request <request-id> --answer "Use CSharpDB"
dotnet run --project src/IronPaw.Cli -- --resume <run-id> --approve-request <request-id>
dotnet run --project src/IronPaw.Cli -- --resume <run-id> --deny-request <request-id>
```

An approval applies to the exact saved operation. It does not broaden the workflow's
tool permissions. Supply one answer, approval decision, or reconciliation per command.
For a request owned by a child run, respond using that child's run ID, then resume
its parent to continue the delegated workflow. Resume with an unanswered request
shows its saved suspension without asking for provider credentials.

Opt into approvals when starting a new workflow by listing the tool names:

```powershell
dotnet run --project src/IronPaw.Cli -- --prompt "Update the project" --require-approval write_file,save_memory
```

Pause a workflow for later continuation, or permanently cancel it:

```powershell
dotnet run --project src/IronPaw.Cli -- --pause <run-id>
dotnet run --project src/IronPaw.Cli -- --resume <run-id>
dotnet run --project src/IronPaw.Cli -- --cancel <run-id>
```

Controls are durable requests. They take effect at safe execution boundaries; an
already-started action may require outcome reconciliation. Root cancellation applies
to its descendants. Controls require access to the workspace journal, which remains
limited to one owning host at a time.

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
- `IRONPAW_LLM_CONTEXT_WINDOW_TOKENS` (optional; OpenAI-compatible budget override)
- `IRONPAW_LLM_SAFETY_MARGIN_TOKENS` (optional; OpenAI-compatible budget override)
- `IRONPAW_LLM_MIN_OUTPUT_TOKENS_PER_REQUEST` (optional; OpenAI-compatible budget override)
- `IRONPAW_LLM_MAX_AUTO_CONTINUATION_PASSES` (optional; OpenAI-compatible budget override)
- `IRONPAW_CODEGEN_PROVIDER` (optional; override provider for the `codegen` specialist)
- `IRONPAW_CODEGEN_MODEL` (optional; override model for the `codegen` specialist)
- `IRONPAW_REVIEWER_PROVIDER` (optional; override provider for the `reviewer` specialist)
- `IRONPAW_REVIEWER_MODEL` (optional; override model for the `reviewer` specialist)
- `IRONPAW_WEB_BROWSER_PROVIDER` (optional; override provider for the `web_browser` specialist)
- `IRONPAW_WEB_BROWSER_MODEL` (optional; override model for the `web_browser` specialist)

Example split-model setup (PowerShell):

```powershell
$env:IRONPAW_CODEGEN_PROVIDER="openai"
$env:IRONPAW_CODEGEN_MODEL="gpt-4.1"
$env:IRONPAW_REVIEWER_PROVIDER="anthropic"
$env:IRONPAW_REVIEWER_MODEL="claude-3-5-sonnet-latest"
dotnet run --project src/IronPaw.Cli -- --provider openai --model gpt-4o-mini --prompt "Implement and review a new parser"
```
