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

Desktop shares the CLI's user settings file at `%LOCALAPPDATA%/IronPaw/settings.json`, or the file
selected by `IRONPAW_CONFIG`. Provider/model and workflow limits prefill the new-run controls;
specialists use the shared host catalog and configured routing. File validation occurs before
application state opens. Credentials, permissions and evaluation settings cannot be supplied in
this file. See [shared configuration](../../docs/runtime-configuration.md).

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

## Task graphs

Open **Task graphs** to import a reviewed graph launch JSON document, inspect saved graphs,
or control their durable execution. The imported document uses the shared `TaskGraphLaunchDocument`
format (`graph`, `options`, `maxConcurrency`, `schemaVersion`). The tab shows the configured source
workspace and managed workspace directory; the document must name those exact paths. Desktop
keeps graph workspaces beside this workspace's CSharpDB file under `task-workspaces`, outside the
source checkout. See the AgentCore README for the complete manifest structure.

Expand **Imported manifest** before starting to review task dependencies, file scopes, shared limits,
artifact criteria, and any literal verification command profiles. Importing a file grants no process
authority. Graphs with command profiles require the separate **Allow trusted local verification for
this action (host OS authority)** checkbox before starting. Resume and recovery can inspect or reuse
completed receipts with that checkbox clear; pending commands remain unexecuted. The checkbox resets
after execution or when another graph is selected. Local verification runs repository code as the
current Windows user. Its disposable copy and reduced environment do not provide an OS sandbox.

Select a saved graph and task to inspect dependency status, the immutable attempt and child invocation
IDs, accepted input receipts, artifacts and content identities, command completion receipts, bounded
stdout/stderr, shared token/time accounting, and the graph journal. **Resume graph** schedules ready
work and pending acceptance. **Recover selected task invocation** explicitly reclaims an interrupted
task's original durable child after uncertain effects have been established. **Pause graph** preserves
work; **Cancel graph permanently** records permanent cancellation. Selecting another graph cannot
redirect controls for an active execution. Closing Desktop pauses the active graph and waits for its
claims to settle before releasing CSharpDB.

For an uncertain task, command, or child file-tool operation, use **Reconcile an independently established
outcome**. Select the task and exact saved operation, import or enter the matching JSON result, and
provide evidence describing how its outcome was established. Command results use `TaskVerificationCompletion`,
task results use `WorkflowTaskResult`, and child tool results use `ToolResult`. Recording the result does
not execute or resume anything. Use resume or task recovery separately after reconciliation. Failed
checks and unknown command outcomes cannot be replaced with success by simply retrying a process.

Graph anchors and their children use graph controls and do not appear as executable roots in the
ordinary **Workflow recovery** tab. Browsing, pause/cancel, and recording reconciliation do not load
provider configuration or invoke a model. Provider construction is deferred until model work is needed.
Launches reject unsupported provider names before graph admission. Resume and recovery validate the
saved provider only when their schedulable work may need a model. Candidate-only verification and
reconciliation remain available when the saved provider is no longer configured.

## Execution timeline

Open **Timeline** to browse saved ordinary workflows and task graphs. The view refreshes every two
seconds while this tab is visible; clear **Refresh automatically** to hold the view or use **Refresh**
to read it again. Browsing reads CSharpDB checkpoints, journals and host receipts without constructing
a provider, claiming work, starting verification, or resuming a run. The full execution ID is available
on the selector's tooltip. Caller ownership and graph workspace boundaries also apply to this view.

Task rows show dependency waits, active agents and tools, pending human input, verification outcomes,
and failures that need attention. The progress bar counts host-accepted tasks. A finished model run
does not count as accepted work or independent verification. Artifact-only acceptance explicitly says
that no build/test check is configured. Test success requires an established zero-exit receipt with a
positive test count, all tests passing, and none failed or skipped. Unknown effects remain **Needs
review** and do not trigger automatic retries. Requested pause/cancellation stays visibly unsettled
while work still owns a claim.

Select a task and expand **Selected task and saved evidence** for candidate revisions, output locations
and digests, host decisions, and bounded command logs. The summary shows shared token reservations,
charged tokens, call counts and active execution time. It does not infer monetary cost. **Saved activity**
shows up to the latest 250 journal entries and reports the total if older entries are omitted. Revision
order is preserved within each journal even after clock corrections; parallel journals may interleave.
These independently read checkpoints and ledgers form a refreshable view, not an atomic snapshot of
all concurrent activity.

Changing selection or closing the window cancels pending timeline reads and rejects stale results.
Unchanged task/activity collections retain their selection and scroll position. **Open controls** moves
to the existing recovery or graph controls; execution, reconciliation and authority grants remain
separate actions. Opening graph controls clears the local-verification checkbox. Automatic timeline
refresh does not replace drafts in the recovery controls.

## Resilient live updates

Live events use a bounded queue drained by the dispatcher. Slow or closed views do not block
producers. Adjacent text updates are combined; lifecycle/tool entries have priority under pressure,
and omitted/shortened entries are reported explicitly. The event list holds the latest 500 entries,
while the response pane streams the main invocation's text and then shows the returned final response.
Long response previews carry an explicit shortening message. Late text cannot change that final response.
Saved workflow state and receipts remain available through Timeline and recovery.

See [execution diagnostics and desktop delivery](../../docs/execution-observability.md) for exact
limits, telemetry hooks, failure isolation and the boundary between live display and durable evidence.

## Application storage

Desktop shares `<state-root>/application.cdb` with the CLI for sessions, transcripts, memories,
run history and messages. Storage work runs off the dispatcher. The first application operation
imports legacy JSON once; close older IronPaw instances before migrating. Original files remain
intact; SQLite files are not imported. The existing Desktop tool catalog and saved workflow
permissions are preserved. See [application storage](../../docs/application-storage.md).
