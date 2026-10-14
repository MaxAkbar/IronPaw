# IronPaw.Desktop

`IronPaw.Desktop` is the WPF UI host for IronPaw.

## Purpose

- Provide an interactive Windows interface over the headless agent core.
- Display real-time event stream from orchestrator runs.
- Support provider/model selection and session-based execution.
- Browse durable workflows and recover paused or interrupted work from CSharpDB.

## Main files

- `MainWindow.xaml`
  - command-center style interface:
    - session key input
    - provider/model controls
    - prompt input
    - assistant output
    - event stream list
- `MainWindow.xaml.cs`
  - consumes DI bootstrap output (`DesktopBootstrapper`) and runs requests
  - add-in skill loading and diagnostics
- `UiEventSink.cs`
  - `IAgentEventSink` implementation that dispatches events to the WPF UI thread
- `App.xaml` / `App.xaml.cs`
  - WPF application initialization

## Run

```powershell
dotnet run --project src/IronPaw.Desktop
```

## Runtime configuration

- Providers:
  - `local`
  - `openai`
  - `ollama`
  - `lmstudio`
  - `anthropic`
- Provider defaults and model names are sourced from `ProviderDefaults` (shared with CLI)
- API keys read from environment variables:
  - `OPENAI_API_KEY`
  - `ANTHROPIC_API_KEY`
  - `OLLAMA_BASE_URL` (default `http://localhost:11434`)
  - `OLLAMA_API_KEY` (optional; default `ollama`)
  - `LMSTUDIO_BASE_URL` (default `http://localhost:1234`)
  - `LMSTUDIO_API_KEY` (optional; default `lm-studio`)

## Add-in skill loading

On startup, the desktop host loads skills from:
- `./addins/skills`
- `%LOCALAPPDATA%/IronPaw/skills`

Skill load diagnostics are written to the event panel.

## Notes

- The Desktop project is a host/composition layer; core runtime behavior remains in `IronPaw.AgentCore`.
- Provider wiring is handled in the DI composition root (`DesktopBootstrapper`) via `LlmClientFactory.Build()`.

## Workflow recovery

Open the **Workflow recovery** tab and refresh the saved workflow list. Select a root workflow,
then an invocation to inspect a child agent. The summary shows the root's authoritative control
state, shared model/tool call counts, charged and reserved tokens, and execution time. Expand the
details to read lineage, tool arguments and results, recorded decisions, reconciliation evidence,
and journal entries.

- **Resume root** continues the original workflow and its saved permissions and limits.
- **Pause root** preserves resumable work. **Cancel root permanently** records an irreversible
  workflow cancellation. The controls beside **Run Agent** always target the active root;
  selecting another saved workflow cannot redirect them.
- Select an exact pending request to submit an answer, or inspect the tool arguments and operation
  fingerprint before approving. Answering or approving saves the receipt before resuming the root.
  **Deny** records the decision without resuming; use **Resume root** to process that decision.
- For an unknown tool outcome, select its execution, supply the independently established result,
  indicate whether it was an error, and provide verification evidence. Recording an outcome does
  not execute that tool. Confirmed results are reused on resume. A permanently cancelled workflow
  can have an unknown outcome reconciled but cannot resume.

The **New workflow limits and approvals** expander accepts optional total-token and active execution
limits (whole seconds), plus comma-separated tool names requiring approval. Limits apply to the
root and all descendants and persist across restart. Waiting for human input or a paused workflow
does not consume active execution time. Recovery uses the current Windows user's local-owner
identity; workflows belonging to a different caller cannot be controlled.

The workspace journal allows one host at a time. If CLI already owns it, close that host before
starting Desktop. Closing Desktop during active execution records a pause and waits for the
invocations to settle before releasing its store.
