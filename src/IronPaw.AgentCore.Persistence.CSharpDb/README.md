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
if a tool intent has no confirmed result. Confirmed receipts remain immutable.

Saves require the current revision. Root model/tool budgets include descendants
and cannot be reset by resuming. Unsupported snapshot versions fail closed.
CLI and Desktop keep journals under their state directory, partitioned by the
normalized absolute workspace path. Existing conversation store formats are
independent of this workflow journal.
