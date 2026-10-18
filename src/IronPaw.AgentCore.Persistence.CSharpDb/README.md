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

Task graphs use separate versioned graph snapshots and an append-only graph
journal in the same exclusively owned database. `ITaskGraphStore` accepts an
immutable validated definition, caller identity, and concurrency limit. Its
commands claim ready tasks atomically in declaration order, with a stable attempt
identity and a fencing token persisted before dispatch. Dependencies unlock only
after a trusted host records acceptance of the exact successful result revision.
Candidate results and acceptance receipts commit with their journal entry;
receipts bind the graph fingerprint, task, attempt, result digest, and revision.
The store revalidates these bindings when reading persisted graphs.

An interrupted execution claim becomes `NeedsReconciliation` on reopen and is
never automatically dispatched again. A saved successful proposal can reclaim a
new token for read-only acceptance verification without repeating execution.
Inconclusive verification retains the proposal for a later explicit resume.
Pause and permanent cancellation block further claims while known outcomes can
still settle. Previous fencing tokens cannot establish a new outcome after
recovery; exclusive database ownership does not prove physical workers stopped.

Generic schema 1 graph scheduling is a host API boundary. The host-provided task
executor must enforce resources, budgets, isolation, and effect idempotency.
Its acceptance verifier must independently establish artifact and evidence
truth. Persisting a model's success claim alone never unlocks dependencies.

`IAgentTaskGraphStore` accepts schema 2 agent graphs with an immutable execution
binding and a synthetic Step 2 root workflow in one transaction. The binding
freezes the caller, source/base/workspace roots, provider/model, inherited tool
policy, shared budget, and host artifact verification mappings. Generic graphs
cannot be rebound. The synthetic root is a ledger/control anchor and cannot run
provider work or be modified through ordinary workflow commands.

Bound child identities derive from the graph fingerprint, task and stable
attempt. Creation, leasing, and new model/tool admissions require a matching
running graph claim and the exact accepted child request. Nested delegation is
rejected. Shared model/tool counts and token reservations use the Step 2 ledger;
graph and child leases keep one root execution clock active without double
counting. Recovery charges an unclean active interval through reopen, then stops
it once stale graph/child leases settle. Graph pause/cancellation and the root
control commit atomically. Clearing pause waits for both kinds of lease.

`ReclaimAgentTaskAsync` explicitly resumes the same interrupted attempt only
when its child has no active lease or unknown tool effects; a missing child is
safe because no provider admission occurred. A saved child outcome can replay
without new effects. Unknown effects require established receipts first. A
bound task outcome also cannot be reconciled while its child has an active lease
or unresolved tool effects; this guard commits atomically with task reconciliation. A
pending verification proposal can become accepted or budget exhausted through
the trusted host verifier. Scoped workspaces, tool enforcement and verification
remain the executor and verifier's responsibility.

Schema 3 agent graphs select `AcceptedChanges`. Their initial task claim atomically freezes the
complete accepted ancestor closure, exact producer attempts/results/acceptance receipts, base commit
and consumer identity. The digest is included in canonical child requests. Reads, claim settlement,
reclaim and verification reject altered input receipts. Existing schema 2 bindings retain independent
`BaseCommitOnly` execution; no historical baseline is invented during recovery. Switching modes on
an accepted graph is rejected. File composition and baseline/changeset integrity are enforced by the
workspace host outside database transactions.

Schema 4 agent graphs add explicit owner-authorized command profiles and required-criterion mappings.
The verification ledger binds each invocation to the exact graph/task/attempt, candidate result digest,
revision and profile fingerprint. `BeginTaskVerificationAsync` atomically checks the claim fence,
root controls and shared budgets before recording intent. Completed receipts are immutable and reused;
known outcomes can settle after pause/cancellation or budget overshoot. Passing acceptance requires
every mapped command to have a passing receipt and rechecks shared budgets in the same transaction.

A started invocation without an established result becomes `NeedsReconciliation` on abandonment or
reopen. Such candidates cannot be claimed for automatic verification. Their shared root execution
clock remains active through pauses and restart until the uncertainty is resolved; stopping the host
does not prove an external worker stopped. `ReconcileTaskVerificationAsync` requires the original
caller, a known completion and a note while the task is unleased. Explicit resume then performs
acceptance without rerunning the command. Log bounds/digests, test counts, profile/result identity and
ledger states are revalidated on read. Graph schemas 1/2/3 retain their previous contracts; invocation
checkpoint schema remains 3.

Live evaluation campaigns have a separate schema-1 snapshot and append-only event journal. One
atomic store instance hosts campaign reservations, workflow budgets and task graphs. Owner,
provider/model, currency, cap and opaque complete binding are immutable. Model requests reserve
estimated spend before dispatch, once per frozen identity. Complete usage settles the reservation;
uncertain requests retain the larger of reservation and known partial spend, then freeze admission.
Reopening a started request requires reconciliation. Completed receipts and trial observations
cannot be replaced. Actual estimated charges exceeding reservations, or observed token-ceiling
violations, remain visible and block further paid requests. Explicit owner reconciliation needs
a known receipt and note and never starts a provider or process. External provider billing is not
queried by this store. Existing workflow and graph checkpoint versions remain unchanged.
