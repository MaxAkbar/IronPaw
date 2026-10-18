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
- `--validate-evaluation-config <path>` (standalone local validation; no provider, state store, or evaluation run is initialized)
- `--run-evaluation <path>` (run the configured paired comparison in a new isolated campaign directory)
- `--resume-evaluation <campaign-id>` (continue saved trials with frozen configuration and prices)
- `--show-evaluation <campaign-id>` (inspect saved accounting and comparison evidence without credentials)
- `--reconcile-evaluation <campaign-id> --request <request-id> --result-file <receipt.json> --reconcile-note <evidence>` (record externally verified billing without executing a model or resuming trials)
- `--reconcile-evaluation-command <campaign-id> --evaluation-graph <graph-id> --task <task-id> --invocation <id> --result-file <receipt.json> --reconcile-note <evidence>` (settle an uncertain build/test in the isolated campaign journal)
- `--reconcile-evaluation-task <campaign-id> --evaluation-graph <graph-id> --task <task-id> --result-file <result.json> --reconcile-note <evidence>` (settle an uncertain task result)
- `--reconcile-evaluation-tool <campaign-id> --evaluation-graph <graph-id> --task <task-id> --tool-execution <id> --result-file <result.json> --reconcile-note <evidence>` (settle an uncertain task tool)
- `--evaluation-output <absolute-directory>` (required campaign directory for all evaluation commands)
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

Configure a live comparison by copying [`docs/examples/live-evaluation.example.json`](../../docs/examples/live-evaluation.example.json)
to a local settings file and editing its provider, exact model ID, maximum total spend, and reviewed USD price schedule.
Supply the input/output token ceilings, input framing allowance, price source, and UTC review timestamp explicitly. The example
defaults to disabled and does not select a provider, model, cap, or prices. Validate without loading credentials or creating app state:

```powershell
dotnet run --project src/IronPaw.Cli -- --validate-evaluation-config docs/examples/live-evaluation.example.json
```

This command exits `0` for a valid configuration (including a disabled, incomplete template) and `2` for invalid settings or JSON.
It accepts no prompt, workflow, graph command, or runtime override flags. Enabling the configuration requires all live values.
Validation never starts a comparison. Execution is a separate explicit command:

```powershell
dotnet run --project src/IronPaw.Cli -- --run-evaluation C:\Eval\settings.json --evaluation-output C:\Eval\campaign-001 --allow-local-verification
```

Use a new or empty campaign directory outside the current source checkout. Its `evaluation.cdb`, generated fixtures, isolated
workspaces, and report are separate from normal application state. The evaluation host does not load installed skills or add-ins.
`--allow-local-verification` grants the reviewed build/test profiles host OS authority; the local worker is not an OS sandbox.

Every provider request reserves its declared maximum spend in CSharpDB before dispatch. Known complete usage settles that reservation
using the frozen price schedule. Missing usage, interrupted requests, and uncertain outcomes retain reserved funds and block automatic
retries. The total cap covers both strategies and all trials. This admission accounting depends on the supplied prices and token bounds;
it does not guarantee the provider's final invoice. Reports retain the distinction between measured comparisons and live qualification.

```powershell
dotnet run --project src/IronPaw.Cli -- --show-evaluation <campaign-id> --evaluation-output C:\Eval\campaign-001
dotnet run --project src/IronPaw.Cli -- --resume-evaluation <campaign-id> --evaluation-output C:\Eval\campaign-001 --allow-local-verification
dotnet run --project src/IronPaw.Cli -- --reconcile-evaluation <campaign-id> --evaluation-output C:\Eval\campaign-001 --request <request-id> --result-file C:\Eval\receipt.json --reconcile-note "Verified provider usage and charge"
```

Resume uses the original provider, model, limits, prices, and workload binding. Inspection and reconciliation need no credentials and do
not run commands or models. Reconciliation uses a strict `LiveEvaluationReceipt` JSON document with `usageComplete`, `actualSpend`,
`inputTokens`, `outputTokens`, and `evidenceJson`; verify external billing before supplying it. Reconciliation never resumes automatically.
The graph reconciliation commands use strict `TaskVerificationCompletion`, `WorkflowTaskResult`, or `ToolResult` documents respectively,
and require a graph belonging to that campaign. Use the graph, task, invocation, and tool IDs printed by `--show-evaluation`.
They settle the supplied evidence and refresh the saved report; explicitly resume only after every uncertain effect is resolved.
The live client has no fallback provider, disables automatic continuation, and ignores provider-routing and LLM override environment
variables. OpenAI and Anthropic use official endpoints; Ollama and LM Studio use their fixed default loopback endpoints.

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

## Durable task graphs

Graph commands use the same workspace-specific CSharpDB ledger as workflow recovery,
regardless of the conversation `--store` selection. Start by displaying the configured
source and managed workspace paths:

```powershell
dotnet run --project src/IronPaw.Cli -- --list-graphs
```

Copy those absolute paths into a reviewed JSON `TaskGraphLaunchDocument`. The managed
workspace must remain outside the source checkout. For example, replace both paths in:

```json
{
  "schemaVersion": 1,
  "maxConcurrency": 2,
  "graph": {
    "graphId": "add-summary-report",
    "objective": "Produce a summary report from the committed source",
    "tasks": [
      {
        "taskId": "report",
        "objective": "Read README.md and write reports/summary.md",
        "toolPolicy": { "profile": "Coding" },
        "resources": { "readPaths": ["README.md"], "writePaths": ["reports/summary.md"] },
        "expectedArtifacts": [
          { "artifactId": "summary", "description": "Summary report", "kind": "Report", "relativePath": "reports/summary.md" }
        ],
        "acceptanceCriteria": [
          { "criterionId": "integrity", "description": "The declared report is captured intact", "artifactIds": ["summary"] }
        ]
      }
    ]
  },
  "options": {
    "sourceWorkspaceRoot": "C:\\path\\to\\source",
    "workspaceStoreRoot": "C:\\path\\from\\list-graphs\\task-workspaces",
    "baseRevision": "HEAD",
    "provider": "openai",
    "model": "your-configured-model",
    "rootToolPolicy": { "profile": "Coding" },
    "sharedBudget": { "maxModelCalls": 12, "maxToolCalls": 24, "maxTotalTokens": 100000, "maxExecutionTime": "00:05:00" },
    "dependencyMode": "AcceptedChanges",
    "artifactVerificationRules": [ { "taskId": "report", "criterionId": "integrity" } ]
  }
}
```

Artifact integrity establishes the captured file's presence and content identity. Add
separate trusted command criteria when acceptance needs a successful build or tests.
Task dependencies use IDs in a task's `dependencies` array; accepted predecessor changes
are composed before a dependent task runs. Model file tools retain their declared scopes.

```powershell
dotnet run --project src/IronPaw.Cli -- --start-graph graph.json
dotnet run --project src/IronPaw.Cli -- --show-graph add-summary-report
dotnet run --project src/IronPaw.Cli -- --pause-graph add-summary-report
dotnet run --project src/IronPaw.Cli -- --resume-graph add-summary-report
dotnet run --project src/IronPaw.Cli -- --cancel-graph add-summary-report
```

Inspection includes task states, dependency blockers, deterministic child run IDs,
artifact locations and digests, acceptance receipts, command outcomes, bounded logs,
shared budget usage, and the graph journal. Listing, inspection, pause, cancel, and
reconciliation need no provider credentials. Resume requests configuration for the saved
provider only when new model work can run. Provider, model, policy, session, and budget
flags cannot override the accepted graph binding. Legacy workflow commands omit graph
runs and direct their IDs to graph controls.

A command manifest cannot grant itself process execution. After reviewing executable
paths, executable SHA-256 identities, literal arguments, working directories, timeouts,
and test report paths, explicitly enable the trusted local worker:

```powershell
dotnet run --project src/IronPaw.Cli -- --start-graph graph-with-checks.json --allow-local-verification
dotnet run --project src/IronPaw.Cli -- --resume-graph add-summary-report --allow-local-verification
```

This worker executes repository code with the host user's OS authority; it is not an OS
sandbox. Each command runs against a disposable copy of the sealed candidate. Completed
receipts are reused without running commands again. Resuming without the flag can inspect
or accept completed receipts, while commands without receipts remain awaiting acceptance.
The flag may accompany start, resume, or explicit task recovery only.

Interrupted tasks require explicit recovery. Unknown tool or command effects must first
be established externally and reconciled; reconciliation saves evidence and does not
execute or resume work:

```powershell
dotnet run --project src/IronPaw.Cli -- --recover-graph add-summary-report --task report
dotnet run --project src/IronPaw.Cli -- --reconcile-graph-tool add-summary-report --task report --execution <tool-execution-id> --result-file tool-result.json --reconcile-note "Verified the saved destination"
dotnet run --project src/IronPaw.Cli -- --reconcile-graph-task add-summary-report --task report --result-file task-result.json --reconcile-note "Established the preserved attempt outcome"
dotnet run --project src/IronPaw.Cli -- --reconcile-graph-command add-summary-report --task report --invocation <invocation-id> --result-file command-result.json --reconcile-note "Established process exit and inspected output"
```

The result files contain a `ToolResult`, `WorkflowTaskResult`, or
`TaskVerificationCompletion` respectively. Use the saved graph fingerprint, task,
revision, artifact identities, command invocation ID, and observed outcome from inspection;
reconciliation validates them against the durable record. A command completion includes
`outcome`, `workerId`, `explanation`, `exitCode`, and `standardOutput`/`standardError`
records containing `text`, SHA-256 `digest`, `byteCount`, and `truncated`; test commands
also require consistent positive test counts. Keep unresolved effects uncertain instead
of submitting a successful outcome without evidence.

Graph input files use strict JSON with string enum names. Unknown properties, duplicate
property names, null documents, and files larger than 4 MiB are rejected.

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
