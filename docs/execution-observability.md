# Execution diagnostics and desktop delivery

IronPaw exposes optional .NET `ActivitySource` traces and `Meter` instruments under
`IronPaw.AgentCore`, version `1.0.0`. No collector, exporter, network connection or telemetry file is
enabled by default. Hosts can attach standard .NET listeners, or configure their own exporter for
that source and meter. No additional runtime packages are required for these hooks.

## Operations and correlation

| Activity | Observed boundary | Outcome meaning |
|---|---|---|
| `ironpaw.run` | One executing or resumed agent invocation, including lane wait and settlement | `completed` means the agent loop finished, not independent task verification |
| `ironpaw.graph` | One scheduler invocation, including its parallel claims and final saved state | `accepted` requires the persisted graph to have all tasks accepted |
| `ironpaw.task` | One claimed task attempt, through proposal and acceptance/abandonment | Follows the saved host decision, including inconclusive and uncertain outcomes |
| `ironpaw.model` | One `ILlmClient.StreamAsync` enumeration that reaches the adapter | Completion requires the stream to end normally with a completion marker and no error delta |
| `ironpaw.tool` | One actual `IToolExecutor.ExecuteAsync` call | Reports the executor return/error, not a guarantee that external effects were rolled back |
| `ironpaw.verification` | An admitted command worker invocation through its saved receipt | Follows the established command outcome; unknown worker outcomes need reconciliation |

Spans carry the applicable `ironpaw.run.id`, `ironpaw.workflow.root_id`, `ironpaw.parent_run.id`,
`ironpaw.agent.id`, `ironpaw.graph.id`, `ironpaw.task.id`, `ironpaw.task.attempt_id`,
`ironpaw.tool.call_id`, `ironpaw.tool.execution_id`, `ironpaw.verification.invocation_id`, and
`ironpaw.revision`. IDs are bounded to 256 characters. Parent activities connect graph/task/agent
work and nested delegation in the current invocation. Restarted invocations have new span IDs;
durable invocation and attempt IDs provide the link across restarts. Graph recovery can span
multiple scheduler invocations.

Prompts, model names, session keys, caller names, tool names/arguments/results, file paths, generated
text, logs and exception messages are not recorded as diagnostic payloads. Custom identity values
are still host data: hosts choose whether and where to export traces. No permission or execution
decision depends on a trace, metric, or listener callback.

## Metrics

| Instrument | Type / unit | Dimensions |
|---|---|---|
| `ironpaw.operations` | Counter / operations | `operation`, `outcome` |
| `ironpaw.operations.active` | Up/down counter / operations | `operation` |
| `ironpaw.operation.duration` | Histogram / seconds, measured by a monotonic clock | `operation`, `outcome` |
| `ironpaw.model.reported_tokens` | Counter / reported tokens | `direction`: input, output, cache_read, cache_write |
| `ironpaw.presentation.events` | Counter / presentation updates | `outcome`: Coalesced, Omitted, Truncated, CallbackFailed, ObserverFailed |

Metric dimensions use fixed operation/outcome/direction values. IDs and other user-defined strings
are confined to traces. Outcomes distinguish `completed`, `accepted`, `failed`, `cancelled`,
`paused`, `awaiting_input`, `budget_exhausted`, `round_limit`, `inconclusive`, and
`needs_reconciliation`. Durations cover the named operation and may overlap; adding parent and
child durations does not yield end-to-end latency.

Model/tool counters surround the actual adapters. Reusing stored model responses, tool results or
verification receipts does not report another adapter call or another token receipt. Reading a
cached terminal agent result does not create a new run span. Inspect/list operations do not create
execution spans. Resuming an already completed graph still observes a brief graph scheduler
invocation, without reporting model/tool/verification work again. Internal provider HTTP retries or
continuations are not separately counted by this logical adapter instrumentation.

Token metrics include positive normalized usage deltas that the client actually reported. They do
not include estimated/reserved tokens or establish billing totals. Missing or partial receipts remain
subject to the durable budget ledger, which is the execution authority. Active execution-time limits,
task acceptance, and live-provider qualification also remain independent of telemetry.

Listener exceptions are contained, including activity start/stop and measurement callbacks. A faulty
listener can lose telemetry; it cannot turn a diagnostic failure into a provider retry, accepted task,
or changed checkpoint. These are in-process .NET observers, so host listeners should still return
promptly. IronPaw does not isolate arbitrary listener code in another process.

## Live desktop view

Desktop producers snapshot bounded display data into an in-memory queue. They do not invoke a UI
callback or wait for the dispatcher. Runtime actions and saved-state operations begin on a background
thread, so provider/store continuations do not capture the dispatcher or hold shared storage gates
while waiting for UI delivery. Inputs and authority selections are captured before leaving the UI;
only presentation and control enablement return to it. The queue holds at most 512 entries and 131,072 charged
characters plus bounded per-entry overhead. It coalesces only adjacent assistant text for the same
run and agent, in chunks up to 8,192 characters plus a shortening marker. Other summaries are limited to 2,048 characters plus a marker,
16 primitive fields and bounded identity strings; arbitrary payload `ToString` methods are not
called, and mutable structured payload objects are not retained.

The dispatcher drains at most 128 entries every 100 ms. Lifecycle/tool entries have priority over
text/diagnostic entries when the queue fills. Even lifecycle-only floods remain bounded: omissions,
shortened updates and display failures are explicitly reported in the live view. Presentation-loss
metrics are emitted when the view drains, so closing a view can discard unreported display counts.
Live display delivery is best effort and is not an audit log.

The view retains the latest 500 display entries, at most 4,096 characters plus an omission marker
per entry. The main agent's text is batched into the response pane, capped at 65,536 characters plus
an explicit shortening message. Child text stays attributed to its own agent/run in the event list.
The returned final response replaces the streamed preview; delayed text cannot append to that final
response. Closing the window stops the pump and makes subsequent publications no-ops.

The workflow's CSharpDB checkpoints, lifecycle journal and operation receipts retain their existing
durability and recovery rules. The UI queue does not replace, edit, or retry those writes. Use
**Timeline** and the recovery controls for saved evidence; not every transient text/diagnostic update
is present in the durable journal.

All orchestrator event callbacks, including callbacks forwarded into tools and delegated agents,
are guarded against observer exceptions. Unlike the earlier intermediate-event behavior, throwing
from a sink no longer stops work: use cancellation or durable pause/cancel controls to stop execution.
Returned results report `eventDeliveryError` and
`eventDeliveryFailures` for the current invocation; these presentation diagnostics do not rewrite
the committed workflow result. Asynchronous callbacks are bounded by the supplied cancellation
token. Other hosts still own their synchronous callback behavior and backpressure; desktop uses the
non-blocking queue described above.

## Verification boundary

Local checks exercise real CSharpDB persistence, nested trace correlation, actual adapter counts,
replay, callback/listener faults, cancellation, concurrent publishers and queue overflow. WPF probes
keep the dispatcher blocked during a 10,000-update burst, exercise rendering failures and output
limits, and check the authoritative final response and close behavior. Separate probes use the real
orchestrator and CSharpDB with a controlled local model adapter: both new and resumed workflows
persist successful completion while the dispatcher remains blocked, before the view can update.
These checks do not establish
production-load throughput, an external collector/exporter, hosted CI execution, or live-model
quality/cost/latency superiority.
