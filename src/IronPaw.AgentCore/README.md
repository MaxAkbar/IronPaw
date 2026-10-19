# IronPaw.AgentCore

`IronPaw.AgentCore` is the headless orchestration engine.
It runs the agent loop, coordinates tools, persists state, and emits event streams.

## Responsibilities

- Orchestrate one run with a stable `RunId`
- Serialize work per session via lane scheduler
- Build system prompt (including persistent memory injection and agent delegation guidance)
- Execute LLM rounds and process deltas
- Execute tool calls (including recursive agent invocations via `invoke_agent`)
- Persist transcripts and session usage
- Record completed runs in run history
- Apply transcript repair and tool-result truncation safety steps
- Publish lifecycle/assistant/tool/diagnostic events with optional `AgentId` attribution

## Main files

- `IronPawSettings.cs`: strict, versioned user defaults and environment resolution, independent of
  credentials, state stores and workspace files. Hosts use the same immutable settings snapshot.
- `DefaultAgentCatalog.cs`: shared specialist instructions, permission policies and routing defaults.
  User settings can select provider/model routes and cannot expand these policies.

- `AgentOrchestrator.cs`
  - main run loop and recovery boundaries
  - accepts optional `IMemoryStore`, `IRunHistoryStore`, and `IWorkflowStore`
- `SessionLaneScheduler.cs`
  - per-session serialization
- `DefaultSystemPromptBuilder.cs`
  - baseline system prompt generation with memory injection and agent delegation guidance
- `TranscriptSanitizer.cs`
  - tool-call/tool-result repair
- `ToolResultTruncator.cs`
  - context-budget truncation for oversized tool results

## Execution flow (simplified)

1. Receive `AgentRunRequest`
2. Acquire session lane
3. Load and sanitize transcript
4. Load global + session memories (skipped for sub-agent invocations where `ParentRunId` is set)
5. Build prompt and resolve tools
6. Stream LLM output deltas
7. Execute tool calls and append tool results
   - If the tool is `invoke_agent`, a child `AgentRunRequest` is constructed with an ephemeral `SessionKey` and incremented `SubagentDepth`, and the orchestrator is called recursively
8. Re-sanitize/truncate transcript and persist
9. Persist usage/session metadata
10. Record run summary to history (if `IRunHistoryStore` provided)
11. Return `AgentRunResult`

## Dependencies

- `IronPaw.AgentCore.Abstractions`
- `IronPaw.AgentCore.Tools` (tool execution/schema normalization)
- `IronPaw.AgentCore.Persistence`
- `IronPaw.AgentCore.Providers`

## Durable execution

When `IWorkflowStore` is provided, accepted work is persisted before entering the
session lane. `IResumableAgentOrchestrator.ResumeAsync` retains the run, task,
root, and immediate-parent identities and the original caller permissions.
Completed model responses and tool receipts reconstruct the saved execution;
they do not invoke the provider or tool again. Saved model prompts avoid clock
changes during recovery, while changed tool schemas or messages stop replay.

Every new model attempt or started tool consumes a persisted call budget. The
CSharpDB store enforces the root workflow's model/tool limits across descendants
in the same transaction as its checkpoint and journal event. Known token usage
is retained across failed attempts. `InterruptedModelCalls` identifies retries
whose earlier stream ended without a recorded complete response; its billing
may be unknown. Optional `MaxTotalTokens` and `MaxExecutionTime` limits also apply
to the entire root and its descendants, and remain consumed across resume.

Before a token-limited model attempt, the runtime reserves a conservative UTF-8
input estimate plus its capped output allowance. Provider continuations are
disabled for that attempt. Complete input/output receipts settle the reservation;
missing or partial usage and interrupted attempts retain the unreported amount.
Actual receipts always persist, including overshoots. Estimates and provider
tokenization can differ, so this is an admission limit rather than a guaranteed
billing ceiling. Completed replay does not incur another reservation or charge.

Execution time counts one root wall-clock interval while any invocation holds a
lease. Overlapping children count once; settled input/approval waits and pauses
do not count. An unclean shutdown conservatively counts downtime until recovery.
The deadline cancels active work cooperatively and durable admission checks
prevent further effects. Budget exhaustion returns `BudgetExhausted` with partial
output, completed work and a stopping reason. Uncertain started effects still
require reconciliation before another invocation in the same session.

An unfinished started tool has an unknown outcome and blocks resume, including
when the tool threw after starting. `ReconcileToolAsync` records an externally
established result and explanation; it never executes the action. Tools receive
a stable `ToolExecutionId` that destinations can use for idempotency. CSharpDB
cannot make an external file or HTTP side effect part of its transaction.

The workflow checkpoint is authoritative; conversation transcripts and run
history remain separate projections. `request_input` and routing clarifications
create durable human request IDs. `AnswerAsync` records an answer; `ResumeAsync`
continues the same workflow by resolving that answer during replay. Pending
questions return a suspension without calling the provider. Responses are
idempotent when identical and reject conflicting or unauthorized submissions.

`AgentRunRequest.ApprovalRequiredTools` opts selected tools into approval. Each
decision binds the exact execution ID, arguments, descriptor schema and caller.
A denied operation returns a known error without executing. Planned operations
waiting for input or approval are distinct from uncertain started effects.

`PauseAsync` and `CancelAsync` apply to the root and all descendants. Pause is
resumable after active calls settle; permanent cancellation is sticky across
restart and forbids further execution. Ordinary cancellation-token interruption
remains recoverable. Active calls are interrupted cooperatively; durable
admission checks also block new effects if a provider ignores cancellation.
Already returned receipts and usage still commit during shutdown.

Delegation records a stable child ID before invoking it. A waiting child
suspends its parent; answering the child's request and resuming the parent
continues the same invocation. Desktop recovery shows root/child lineage, shared
budgets, decisions, tool outcomes and the journal, with resume/pause/cancel controls.
The task-graph APIs below add dependency scheduling independently of nested delegation. Callers
that omit the store retain the original execution API without durability.

## Task graph contracts

`WorkflowTaskGraph` describes a logical objective and named tasks independently of their
agent invocations. Each task carries dependencies, requested tools/resources, an optional local
budget, expected artifacts, and acceptance criteria. Local task budgets must remain subordinate
to the existing shared root ledger when scheduling is introduced.

```csharp
var proposal = new WorkflowTaskGraph("feature", "Implement the requested feature", [
    new("inspect", "Inspect the relevant code"),
    new("implement", "Implement and verify the change") {
        Dependencies = ["inspect"],
        ExpectedArtifacts = [new("patch", "Reviewable change", TaskArtifactKind.Patch)],
        AcceptanceCriteria = [new("tests", "Relevant tests pass") { ArtifactIds = ["patch"] }]
    }
]);
var validation = TaskGraphValidator.Validate(proposal);
// Use Graph, DefinitionFingerprint and TopologicalTaskIds only when IsValid.
```

Validation returns structured errors and no accepted graph or task order for an invalid proposal.
It rejects duplicate/missing identities, unresolved dependencies, self dependencies, cycles,
invalid budgets and malformed declarations. Iterative dependency traversal handles deep graphs;
ready tasks are ordered by their original declaration index. Accepted snapshots copy all nested
policy lists so later caller mutations cannot change them. This is structural validation, not
permission approval or an executable schedule.

`TaskResultValidator.Validate(acceptedGraph, expectedTaskId, result)` checks result consistency.
Results carry a graph ID and SHA-256 definition fingerprint, so matching task IDs cannot replay
receipts across different graphs or changed plans. The fingerprint includes the complete frozen
definition, including dependencies, policies, resource declarations and acceptance criteria.
A success claim needs at
least one required acceptance criterion, its passing evidence, required artifact coverage and no
unresolved issues. Artifact producers, artifact kinds, result/evidence revisions and command exit
codes must agree. Incomplete outcomes can preserve partial artifacts and unresolved issues.
`HasConsistentSuccessClaim` describes only these checks: the validator does not run commands,
hash or open artifacts, authenticate receipts, or establish that a reported revision is immutable.
The host must supply and verify those facts before a future acceptance gate marks work complete.

The validators and pure `TaskGraphProgress.Inspect` helper live in `IronPaw.AgentCore.Domain`,
which depends only on Abstractions. Both the runtime and CSharpDB store use that shared domain
logic without a persistence/runtime dependency cycle. Existing validation namespaces remain unchanged.

## Durable task scheduling

`TaskGraphScheduler` drives an accepted graph through an `ITaskGraphStore`, a host-owned
`IWorkflowTaskExecutor`, and a trusted `ITaskAcceptanceVerifier`. All three are required.
The existing CSharpDB workflow store implements the graph store in separate schema-1 tables;
the invocation checkpoint schema remains 3.

```csharp
await using var journal = await CSharpDbWorkflowStore.OpenAsync("workflows.cdb");
// The host supplies executor, verifier, caller, and cancellationToken.
var scheduler = new TaskGraphScheduler(journal, executor, verifier);
var checkpoint = await scheduler.StartAsync(proposal, caller,
    maxConcurrency: 2, ct: cancellationToken);
var progress = TaskGraphProgress.Inspect(checkpoint);
```

Acceptance persists the frozen graph, fingerprint, caller and immutable concurrency limit.
Claims choose dependency-ready tasks in declaration order and reserve capacity atomically,
including across scheduler instances sharing the store. The limit applies per graph and counts
both executing and verifying claims. Failed dependencies keep descendants blocked while independent
branches continue. `TaskGraphProgress` exposes ready tasks, blocked dependencies and active claims.

Each attempt gets a stable ID and a separate fencing token before the executor runs. A structurally
valid success proposal is saved as `AwaitingAcceptance`; the host verifier must independently check
its exact revision and artifacts before a separate receipt changes it to `Succeeded`. Rejected
verification fails the task. Inconclusive or interrupted read-only verification retains the proposal
for a later explicit `ResumeAsync`, without repeating execution. Completed accepted tasks replay
without invoking either callback.

An executor interruption without an established result becomes `NeedsReconciliation`, including
after store reopen. `ReconcileAsync` records an independently established terminal outcome and note;
a reconciled success still requires the verifier on explicit resume. Unknown effects are never
automatically retried. Stable attempt IDs support destination idempotency but cannot make external
effects atomic with a database transaction. Fencing prevents stale callbacks from changing the
journal; it does not terminate physical workers.

Pause and permanent cancellation stop new claims and cooperatively interrupt active callbacks.
Already admitted callbacks may still return established receipts, which remain auditable. Graph
control stays authoritative: a late accepted receipt cannot dispatch a dependent task or report the
paused/cancelled graph as successful. Resume clears pause only after claims have settled; permanent
cancellation cannot be cleared. Recovery commands require the original caller and ownership level.

This scheduler is a host API. Custom executors must enforce resource permissions, budgets, workspace
isolation and termination. The built-in adapter below supplies a restricted file-task execution path.

## Isolated agent task graphs

`AgentTaskGraphRunner` connects the scheduler to durable `AgentOrchestrator` child invocations.
Use the same `CSharpDbWorkflowStore` instance for graph and workflow storage. The provider/model,
host policy, shared budget, exact Git base commit, workspace roots and verification mappings are
persisted atomically with a non-executable root ledger anchor. Resume reads the accepted binding.

```csharp
var graph = new WorkflowTaskGraph("report", "Prepare a reviewable report", [
    new("write", "Write report.md using the committed input files") {
        ToolPolicy = new(ToolProfile.Coding),
        Resources = new() { ReadPaths = ["inputs"], WritePaths = ["report.md"] },
        ExpectedArtifacts = [new("report", "Report file", TaskArtifactKind.Report, "report.md")],
        AcceptanceCriteria = [new("integrity", "Report exists in the exact captured revision") {
            ArtifactIds = ["report"]
        }]
    }
]);
var runner = new AgentTaskGraphRunner(journal, journal, new GitTaskWorkspaceManager(), llmClient);
var options = new TaskGraphAgentOptions(repositoryRoot, managedWorkspaceRoot, "HEAD", provider, model,
    new(ToolProfile.Coding), new(MaxModelCalls: 12, MaxToolCalls: 24,
        MaxTotalTokens: 100_000, MaxExecutionTime: TimeSpan.FromMinutes(10))) {
    ArtifactVerificationRules = [new("write", "integrity")]
};
var checkpoint = await runner.StartAsync(graph, caller, 2, options, cancellationToken);
```

The managed workspace root must be disjoint from the source repository. Each attempt receives
a committed snapshot without Git metadata or source checkout changes. Its scoped registry exposes
only `list_files`, `read_file`, and owner-authorized `write_file`. Root and task policies intersect.
Relative path scopes mediate every call, reject traversal, metadata and links/junctions, and filter
directory listings. Network, processes, credentials, external mutations, skills, delegation and
add-ins are unavailable. Unsupported declarations fail before execution. This mediates model file
access; it does not provide an OS sandbox for arbitrary code. Commits with links, submodules or
ambiguous paths are rejected. Snapshot preparation reads raw Git objects without checkout filters,
line-ending conversion or archive attributes; missing objects fail without lazy fetching.

Every child uses the existing shared root ledger. The root execution clock includes preparation,
model/tool work, capture and verification while graph or child claims are active; overlapping
claims count once. Task-local model/tool caps are supported. Task-local token/time limits are
rejected rather than ignored; configure those on the shared graph budget.

Captured outputs live outside writable tool roots. Their manifest hashes the complete workspace
and binds the task, attempt, base and declared artifacts to an exact revision. The built-in verifier
re-reads captured content and rejects alterations. A completed loop proposes
`WorkflowTaskStatus.AwaitingVerification`, without invented passing evidence. Acceptance needs
at least one required criterion and an explicit host verification mapping for every required
criterion. These mappings establish file presence/content identity only; they do not assert that
code works, tests passed or prose is correct. Unmapped criteria remain `AwaitingAcceptance`.

For artifact-only runs, `ResumeAsync` retries pending read-only verification without repeating the provider.
`RecoverTaskAsync` explicitly reclaims an interrupted attempt and stable child invocation,
replaying recorded work. It refuses active children and unknown tool effects; reconcile those
effects through the workflow API before recovery. Known cancellation outcomes persist, and
paused recovery cannot clear a later pause.

The adapter is available to embedding hosts and through the shared CLI/Desktop graph controls.
Controlled replay evaluations exercise combined code changes, behavioral verification and recovery.
Step 3's live quality, cost and latency comparison against direct execution remains unestablished;
see the [evaluation protocol](../../docs/orchestration-evaluations.md).

### Accepted predecessor inputs and file integration

New `TaskGraphAgentOptions` default to `TaskDependencyMode.AcceptedChanges`. Before a task starts,
the store atomically freezes its full transitive predecessor set in `TaskGraphTaskState.Inputs` and
the claim. Each pin includes the exact producer task/attempt, result digest, revision and accepted
receipt. The input digest also binds the consumer task/attempt, graph fingerprint and Git base.
The durable child request includes this identity. Reclaim and verification retain the same inputs;
changed or missing pins fail closed. These agent graphs use checkpoint schema 3, or schema 4 when
trusted command verification is enabled.

The workspace manager loads sealed predecessor revisions, verifies their content and receipt
bindings, and composes each predecessor's own changes once in stable dependency order. Each own
changeset compares the producer's final snapshot with the frozen inputs it originally received.
This keeps inherited edits from being counted twice in a diamond graph or reverting another branch.
Input manifests and checksums are host-owned files outside the model's writable workspace.

Integration uses complete file preimages/postimages, with additions, replacements and deletions;
it does not execute a textual patch. An expected preimage must match the composed state. Identical
final file content can coalesce; divergent edits to the same file, case aliases and file/directory
collisions produce an inspectable failed task before its provider is called. Disjoint edits in one
file conservatively conflict too. The source checkout remains untouched. Partial initialization is
preserved for inspection rather than overwritten.

For example, two independent tasks can change different files, and a final task can depend on both:

```csharp
var graph = new WorkflowTaskGraph("feature", "Produce a combined reviewable result", [
    implementationTask,
    documentationTask,
    finalReviewTask with { Dependencies = [implementationTask.TaskId, documentationTask.TaskId] }
]);
```

The final task starts with both accepted changes applied against the known base. Its captured
revision contains the combined workspace. A final integration task should depend on every branch
that must be assessed together; separate accepted branch results do not establish combined behavior.
Artifact-integrity verification still establishes bytes and declared files only, not build/test success.

Preparation does not expand the consumer's readable paths, writable paths or available tools. On
capture, each producer's own file changes must remain within its original write scopes. Captured
revisions include the frozen input-manifest digest and a deterministic changeset. Replay preserves
the existing working files and refuses changed baselines or predecessor revisions instead of
reapplying inputs over completed edits. Empty-directory-only changes are unsupported and rejected.
Before invoking or replaying a child, a read-only working-state check compares the workspace with
its frozen baseline plus confirmed successful file-write receipts. An unstarted attempt must still
be pristine, and completed writes must retain their established bytes. Unexpected changes stop
execution without overwriting files or repeating tools.

Existing schema 2 agent graphs retain `BaseCommitOnly`, preserving their original independent-base
execution and saved tool/model inputs. Embedding hosts can explicitly select that mode for new runs.
An accepted binding cannot switch modes. Generic schema 1 graphs keep their host-defined behavior.

### Durable build and test verification

Command verification is opt-in, with `TaskVerificationAuthority.None` as the default. An owner host
can grant `TrustedLocalProcess` for an `AcceptedChanges` graph and supply exact profiles to a
`TrustedLocalTaskVerificationWorker`. Profiles are persisted with the binding: executable path and
SHA256 digest, literal arguments, working directory, timeout, log limit and test-report location.
The model cannot choose or change them. Criterion mappings are explicit, and each required criterion
has one artifact or command verifier. One command profile may cover several required criteria.

For a graph whose `integrate` task declares required `build` and `tests` criteria:

```csharp
// The embedding host chooses a trusted executable and computes its approved SHA256 digest.
var profiles = ImmutableArray.Create(
    new TaskVerificationProfile("release-build", dotnetPath, dotnetDigest,
        ["build", "Application.csproj", "--configuration", "Release"], TimeSpan.FromMinutes(5)),
    new TaskVerificationProfile("release-tests", dotnetPath, dotnetDigest,
        ["test", "Application.Tests.csproj", "--configuration", "Release",
         "--logger", "trx;LogFileName=tests.trx", "--results-directory", "verification-results"],
        TimeSpan.FromMinutes(5)) {
        Kind = TaskVerificationKind.Tests,
        TestResultsPath = "verification-results/tests.trx"
    });
var worker = new TrustedLocalTaskVerificationWorker(profiles, TaskVerificationAuthority.TrustedLocalProcess);
var runner = new AgentTaskGraphRunner(journal, journal, new GitTaskWorkspaceManager(), llmClient, worker);
var commandOptions = options with {
    VerificationAuthority = TaskVerificationAuthority.TrustedLocalProcess,
    VerificationProfiles = profiles,
    CommandVerificationRules = [new("integrate", "build", "release-build"),
                                new("integrate", "tests", "release-tests")]
};
var checkpoint = await runner.StartAsync(graph, ownerCaller, 2, commandOptions, cancellationToken);
```

Only map artifact criteria through `ArtifactVerificationRules`; build/test criteria use the command
rules above. A final integration task must depend on every branch whose combined output needs these
checks. Each profile receives its own disposable copy of the exact sealed combined candidate, with
host-owned input proofs and logs outside the copy. Commands may generate build files in their copy;
they cannot change which candidate the receipt names. The original seal is checked again before
acceptance. Tests require exit zero and a fresh, valid TRX report with positive passing test counts
and no failures; exit zero with no tests does not pass.

Copy preparation is reversible I/O. Immediately before worker execution, the store atomically records
a `Started` intent bound to graph/task/attempt, candidate revision/result digest and profile fingerprint.
Known completion commits under the current fence even after pause/cancel or budget overshoot. Saved
passing receipts are reused without launching another command. Read-only acceptance inspects those
receipts and artifact hashes; it never launches a process. Atomic final acceptance checks shared budgets
again, settling `BudgetExhausted` if verification raced exhaustion.

A crash with no established command outcome, or an interruption after process start, requires
reconciliation. Resume cannot rerun that command or admit dependent tasks. The shared execution clock
keeps charging while its effects remain uncertain, including across pause and restart. The original
owner must establish a known completion and record a note through `ReconcileVerificationAsync`, then
explicitly resume. Receipt reuse needs no worker grant; admitting a missing command requires a matching
runtime worker. Neither reconciliation nor resume can change the accepted profile.

The local worker runs repository code with the current OS user's authority. It is not an OS sandbox.
It clears inherited environment values and uses private home/temp directories, but code still has
access to that user's files, network and available credentials. The executable checksum pins the
launcher rather than every SDK/library it loads. Hosts needing stronger boundaries must provide an
isolated worker. The scoped agent file registry itself still exposes no process command.

### CLI and Desktop graph controls

`TaskGraphHostController` supplies both hosts with launch, list, inspection, pause, permanent cancel,
resume, explicit task recovery and task/command/child-tool reconciliation. It uses the same atomic
graph/workflow store and checks the original caller on every inspection or mutation. The configured
source and managed workspace roots must match the accepted binding; a launch file cannot switch
workspaces or grant process authority. An active runner is retained for prompt cooperative control.

`TaskGraphLaunchDocument` contains `graph`, `options`, `maxConcurrency` and `schemaVersion` (currently 1).
`TaskGraphDocumentJson` reads UTF8 files up to 4 MiB, accepts a UTF8 BOM, and rejects unknown/duplicate
properties, numeric enum values, comments and trailing commas. The definition, scopes, mappings,
budgets and requested capabilities are validated before Git, provider setup or graph admission.
The host user must separately enable the trusted local worker for each launch or recovery action
that needs command execution; saved receipts can be accepted without that grant.

Inspection returns task status/dependency blockers, child invocations, artifacts, command receipts,
bounded logs, root usage/budgets and the graph journal. Read-only/control/reconciliation commands
do not construct provider clients or invoke models. Reconciliation records an independently
established result and note, then leaves resume/recovery as an explicit separate operation. Bound
graph anchors and children cannot be resumed or controlled through the legacy workflow screens.
Both hosts reject unsupported provider names before dispatch, including after a cached child result
is reused. Reconcile active or uncertain child operations before settling the enclosing task outcome.

See the [CLI commands and JSON example](../IronPaw.Cli/README.md#durable-task-graphs) and
[Desktop task graph tab](../IronPaw.Desktop/README.md#task-graphs).

### Live evaluation campaigns

`LiveEvaluationSettings` stores the optional provider, exact model ID, whole-comparison decimal
spending cap, price provenance, token ceilings and paired schedule, with live evaluation disabled by default. `Validate()` permits unset
values while disabled and requires explicit valid values when enabled. `RequireEnabled()` returns
a validated snapshot without authorizing or initiating execution. Strict JSON configuration uses
`TaskGraphDocumentJson`; secrets and unknown fields are rejected.

The CLI can validate this configuration before bootstrap, without loading provider credentials or
opening a journal. `LiveEvaluationRunner` executes paired graph/direct trials on three checked
integer code/documentation variants, pinning source commits, SDK version, executable digest,
permissions, profiles and shared execution budgets. The host assertions are outside model file
scope. Verification still executes generated code with owner OS authority; it is not an adversarial
grading sandbox. The output directory and CSharpDB journal remain outside the project.

`EvaluationBudgetedLlmClient` reserves estimated spend atomically before dispatch using the explicit
price schedule and declared token ceilings. It disables hidden continuations, buffers bounded
completed responses and persists usage plus replay evidence before delivering any model tool call.
Known receipts replay without another provider call; unknown usage retains its reservation and
blocks new admission until owner reconciliation. Observed cost or ceiling overruns are preserved
and prevent future admission. This is an estimated admission cap under declared assumptions, not
a guarantee about external provider invoices.

Reports preserve failed trials, verification receipts, durable latency, usage, cost estimates,
pending effects and graph/direct summaries. Inspection is read-only. Explicit billing, command,
tool and task reconciliation export a refreshed report without resuming execution. Neither local
tests nor completing a campaign establish live superiority; `LiveBaselineQualification` remains
`NOT_ESTABLISHED` pending the documented independent review, billing and statistical gates. See the
[template and configuration instructions](../../docs/orchestration-evaluations.md#live-comparison-configuration).

## Multi-agent support

The orchestrator is reused as the execution engine for multi-agent delegation. When the `invoke_agent` tool is called:

- A child `AgentRunRequest` is constructed with the specialist agent's system prompt, tool policy, and an ephemeral `SessionKey` for transcript isolation.
- `SubagentDepth` is incremented to prevent infinite recursion (max depth: 3).
- The child's `FinalAssistantMessage` is returned as a `ToolResult` to the parent agent.
- All events from sub-agents carry an `AgentId` field for attribution.
- Sub-agents skip memory loading (they operate statelessly by default).

## Extension points

- Swap `ILlmClient` with different provider routers/adapters.
- Extend tool set via `IToolRegistry`.
- Register specialist agents via `IAgentRegistry` for multi-agent delegation.
- Replace prompt builder/sanitizer/truncator with alternative implementations.
- Provide `IMemoryStore` / `IRunHistoryStore` for persistent memory and task review.

## Execution timeline projection

`ExecutionTimelineBuilder` projects persisted graph/workflow checkpoints and host verification receipts
into read-only task rows and activity entries. It does not execute providers or tools, infer acceptance
from model text, or treat an ordinary completed agent run as verified. Graph acceptance follows saved
host decisions; build/test labels require established command receipts. Dependency waits, unknown
effects, active tools, human input, shared budgets and bounded saved output are available to hosts.

Graph host inspection includes invocation journals through `TaskGraphHostDetail.InvocationJournal`.
The projection filters entries by captured invocation identity, original caller and checkpoint revision.
It merges streams deterministically while preserving each stream's revision order, including timestamp
regressions, and caps visible events at 250 with explicit total/omitted counts. Store reads remain
independent; the view does not promise a globally atomic snapshot or global causal ordering.

## Diagnostics and observers

`ExecutionDiagnostics` exposes optional .NET activity/metric hooks for graph, task, agent, model,
tool and command-verification operations. Adapter counters exclude durable replay, and traces carry
bounded execution identities without prompt/tool/output payloads. Observer callback exceptions are
isolated from execution. See [the diagnostics contract](../../docs/execution-observability.md) for
instrument names, outcome semantics, privacy boundaries and desktop delivery limits.

## MCP tools

The optional sibling project `IronPaw.AgentCore.Mcp` adapts a reviewed MCP catalog to `ITool`.
Use an explicit `ToolSelectionPolicy.AllowedMcpTools` grant as well as the ordinary caller,
profile and inherited-policy checks. Connector calls use the same durable intent, approval,
budget, result and reconciliation path; saved replay performs no connector I/O. The built-in
CLI/Desktop hosts do not register connectors yet. See [the MCP adapter contract](../../docs/mcp-connectors.md).
