# IronPaw.AgentCore.Persistence.CSharpDb

Durable workflow storage backed by the pinned `CSharpDB.Engine` NuGet package.
It persists accepted requests, model responses, tool intents/results, usage,
run state, and journal revisions. Each checkpoint and journal record commits
in one file-backed transaction.

```csharp
await using var store = await CSharpDbWorkflowStore.OpenAsync("workflows.cdb");
// Pass store as AgentOrchestrator's optional workflowStore argument.
```

One store owns the database file exclusively for its lifetime. Store operations
serialize their entire transaction; model and tool calls execute outside those
transactions. Independent hosts cannot open the same journal simultaneously.
On reopen, stale running leases become interrupted, or require reconciliation
if a started tool has no confirmed result. Planned input/approval waits and
managed child invocations can resume without being classified as unknown
effects. Confirmed receipts remain immutable.

Schema version 3 includes immutable human request/response records, tool states,
root pause/permanent cancellation, and nonterminal suspension results. Version
1 and 2 upgrades and their journal entries commit atomically. Legacy clarifications
become pending input unless a newer conversation already superseded them.
Root controls are checked in the same transaction that admits new work; receipt
and usage cleanup remains available after cancellation.

Saves require the current revision. Root model/tool budgets include descendants
and cannot be reset by resuming. Token-limited attempts reserve estimated input
and their provider output allowance before execution. Recorded usage receipts
consume actual tokens; successful attempts with complete input/output usage
release unused reservations. Partial or all-null usage does not release capacity.
Interrupted attempts and successful attempts without complete usage keep their unreported
allowance charged. Actual receipts always commit, including provider overshoot;
new work is then denied. Reservations and receipts commit with their checkpoint.
Historical schemas did not retain every retry's token reservation. Their known
usage and call counters survive migration, but estimated unknown reservations
cover saved incomplete calls only; historical billing cannot be reconstructed.

A separate root execution clock counts elapsed time once while any descendant
holds an execution lease. Settled human waits and pauses consume no execution
time. Reopen conservatively charges downtime for an unclean active interval;
legacy active runs without a clock charge their uncertain interval from the
oldest live invocation creation. The clock updates in the checkpoint transaction
without modifying the parent revision. `GetBudgetAsync` reports charged and
reserved tokens, elapsed execution time, and remaining limits. `OpenAsync` accepts
an optional final `TimeProvider` argument for deterministic clock tests.

Unsupported snapshot versions fail closed.
CLI and Desktop keep journals under their state directory, partitioned by the
normalized absolute workspace path. Existing conversation store formats are
independent of this workflow journal.
