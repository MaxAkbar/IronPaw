# IronPaw.AgentCore.Domain

Pure task-graph domain logic shared by orchestration and persistence. This project references
only `IronPaw.AgentCore.Abstractions`; it performs no storage, filesystem, network or process I/O.

- `TaskGraphValidator` validates proposed definitions, freezes inherited tool-policy lists,
  computes a deterministic definition fingerprint, and provides dependency order.
- `TaskResultValidator` checks a result's graph/task binding, artifact and revision consistency,
  required acceptance evidence, and unresolved issues. Consistency does not authenticate evidence.
- `TaskGraphProgress` inspects a store-validated checkpoint to report graph status, ready tasks,
  blocked dependencies and active execution/verification claims. It does not authorize dispatch.
- `AgentTaskBindingValidator` freezes the supported host execution binding, rejects unsupported
  capabilities/local budgets and builds deterministic root/child requests with intersected policies.
- `TaskDependencyResolver` derives a unique accepted ancestor closure in stable dependency order,
  pins exact attempts/results/acceptance receipts and validates frozen input identities for replay.

The existing `IronPaw.AgentCore` namespace is retained for these APIs. The runtime and the CSharpDB
store both reference this project, so the store can independently validate persisted declarations
and receipts without depending on the execution runtime.
