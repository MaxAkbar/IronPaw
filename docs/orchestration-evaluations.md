# Orchestration evaluations

The Step 3 quality, cost and latency exit gate is **NOT_ESTABLISHED**. The current
corpus establishes controlled local execution behavior. Scripted provider responses
cannot establish live model quality, billed cost or the value of a routing policy.

## Live comparison configuration

Copy [live-evaluation.example.json](examples/live-evaluation.example.json) to a local settings
file and edit it when a provider, exact model and spending limit have been chosen:

```json
{
  "schemaVersion": 1,
  "enabled": false,
  "provider": null,
  "model": null,
  "maxTotalSpend": null,
  "currency": "USD",
  "pricing": null,
  "maxInputTokensPerRequest": null,
  "maxOutputTokensPerRequest": null,
  "inputFramingTokenAllowance": null,
  "pairedTrials": 20,
  "seed": 1729
}
```

Live evaluation defaults to disabled. A disabled file may leave provider, model and cap unset;
no model or budget is inferred from the normal chat configuration. Setting `enabled` to `true`
requires all three explicitly. `provider` must identify a supported model provider; the `local`
rule-based replay client cannot qualify a live comparison. `model` preserves the supplied model
ID exactly. `maxTotalSpend` is a positive decimal amount for the **whole comparison**, including
both strategies, all tasks, trials and retries. Enabled execution supports `USD`; no currency
conversion is performed. It also requires an explicit price schedule, input and output token
ceilings, and a reviewed allowance for provider request framing. `pricing` contains nonnegative
USD rates per million tokens for `inputPerMillion`, `outputPerMillion`, `cacheReadPerMillion`
and `cacheWritePerMillion`, together with the reviewed `source` and an `asOfUtc` timestamp.
No rate, model, token ceiling or spending cap is inferred from the normal application settings.

Validate a reviewed file from the repository root:

```powershell
dotnet run --project src/IronPaw.Cli -- --validate-evaluation-config docs/examples/live-evaluation.example.json
```

The command runs before application bootstrap. It reads no credentials, opens no workflow journal
and never calls a provider. Invalid schemas, unsupported providers, incomplete enabled settings,
nonpositive caps, unknown fields and duplicate JSON properties fail validation. API keys remain
in the existing provider environment configuration; they do not belong in this file.

Validating or enabling the file does not run a comparison or establish Step 3 qualification.
The opt-in comparison runner now consumes these settings and records admission and usage in
a separate CSharpDB campaign journal. New campaigns require an empty output directory outside
the source checkout and explicit trusted local process verification permission. Inspection and
billing reconciliation do not initialize provider credentials or execute verification commands.

## Paired campaign protocol v1

`LiveEvaluationRunner` implements `ironpaw-paired-code-doc/v1`. It creates three private source
repositories for checked integer addition, subtraction and multiplication, each with a matching
documentation task. The installed SDK version is recorded in a committed `global.json` with
roll-forward disabled. Source commits, input digests, the verification launcher digest, configuration,
paired order and output location are frozen before model dispatch. The configured model string is
fixed; the provider's resolved model revision is not independently attested.

Each pair uses the same source commit, complete objective, output requirements, file capabilities,
24-model-call/24-tool-call/200,000-token/five-minute execution budget, build profile and five behavioral
assertions. The graph strategy has separate implementation and documentation producers followed
by a combined review task; the direct strategy receives both edits in one task through the same
isolated executor. The model can access only `Calc.cs` and `README.md`. Both strategies count
planning/review/model calls, tool work and command verification in their totals. Verification builds
and tests exact sealed copies; the source repositories do not receive generated builds or edits.

`pairedTrials` defaults to 20 and cycles across these three cases. `seed` deterministically fixes the
strategy order within each pair. It does not seed provider sampling or control warm/cold cache state.
Failed trials are retained and never rerun to improve a score. Resume skips saved results, reuses
established receipts and stops at unknown effects. Reports include per-trial artifacts and verification
receipts, root usage, estimated spend, admission-to-settlement wall time, median/p95 wall time,
pending graph/task/command identities and explicit qualification limits. Recovery downtime counts
in wall time; corpus creation and campaign setup do not.

The authoritative state is `<campaign-directory>/evaluation.cdb`; `report.json` is an atomic exported
snapshot that can be regenerated. Inspection is read-only. Billing, command, task and file-tool
reconciliation refresh the report without executing models or resuming work. All campaign commands
use this isolated journal, never the normal application journal. See the
[CLI usage and reconciliation commands](../src/IronPaw.Cli/README.md#usage).

Local integration tests exercise all three paired cases with scripted responses, incorrect source,
protected-harness write attempts, interrupted provider usage, insufficient funds, source drift,
completed recovery and Git environment isolation. Separate ledger tests kill the exact writer
process before and after known completion. These tests establish local mechanics only. This small
workflow family, unchecked external billing, uncontrolled provider/cache variability and missing
independent quality review leave the roadmap's live quality/cost/latency gate **NOT_ESTABLISHED**.

## Durable monetary admission and response replay

A campaign freezes the original owner, exact provider and model, USD cap, full price schedule,
token limits, corpus inputs, paired order, seed, output paths and verification executable identity.
Resume uses that binding. It cannot silently replace the settings or raise the cap. Graph and
direct strategies, every paired trial and all their model calls share the same campaign balance.
The generated task tools cannot read or mutate the campaign journal, prices or admission policy.

Before dispatch, the host atomically saves a stable request identity and a reservation. Only its
first admission permits a provider call. Concurrent admissions cannot reserve more than the
remaining campaign balance. The conservative per-request estimate is:

```text
reservation = (
  declared_input_ceiling * (input_rate + cache_read_rate + cache_write_rate)
  + requested_output_ceiling * output_rate
) / 1,000,000
```

The host checks serialized request content plus the configured framing allowance against the
declared input ceiling, limits requested output, disables hidden automatic continuation and keeps
the frozen provider/model. This is an **estimated admission cap under declared assumptions**.
The UTF-8 content guard is not a model tokenizer or proof of the provider's hidden framing.
The price schedule may differ from the provider's invoice; taxes, account arrangements, endpoint
pricing changes and other billing terms are not obtained from the response. The journal cannot
guarantee a maximum external bill. Review the exact endpoint/model prices and ceilings before
enabling execution; reports retain this limitation.

After a successful terminal stream with complete valid usage, the host saves the observed token
counts and cost calculated from the frozen schedule, then releases the unused reservation.
Anthropic input, cache reads and cache writes are charged as separate categories. OpenAI-compatible
inclusive input usage is conservatively charged at the highest configured input-category rate;
it is not a cache-discount calculation. Anthropic documents that its input count excludes the
separate cache categories. See [Claude prompt caching usage fields](https://platform.claude.com/docs/en/build-with-claude/prompt-caching#tracking-cache-performance).

The full response is buffered to a bounded replay record (at most 1 MiB), hashed and durably saved
with complete usage **before** any text or tool call is delivered to the workflow. A matching
completed request can replay that response after a crash without another paid call or charge.
Corrupt or missing response evidence never falls back to redispatch. Successful response delivery
does not itself establish task acceptance: the sealed artifact, build and behavioral-test gates
still apply.

Cancellation, transport failure, invalid or partial usage, an oversized response, and process
interruption retain the full reservation (or any larger observed cost) and require reconciliation.
Opening the exclusive campaign store converts interrupted `Started` requests to
`NeedsReconciliation`. Any uncertain request freezes new paid admissions across the campaign;
resume never automatically repeats it. Missing usage is not zero usage: OpenAI warns that an
interrupted or cancelled stream can omit its final usage chunk. See [OpenAI streaming usage](https://developers.openai.com/api/reference/resources/chat/subresources/completions/streaming-events).

Only the original local owner may reconcile uncertain usage using externally established counts,
cost evidence and a written note. Reconciliation cannot discard already observed cost, tokens or
ceiling violations. It establishes accounting; it does not invent a missing model response or
resolve unrelated unknown tool/process effects. Known receipts may settle after pause or cancel.
Cancellation is permanent, while pause can be cleared once active paid requests have settled.

If observed cost exceeds its reservation, or reported token counts exceed the declared ceilings,
the host preserves the receipt and blocks further paid work even when the total cap has not yet
been reached. An observed amount above the total cap remains visible; it is never clamped to
make the report appear within budget. Completed strategy/trial results and request receipts are
immutable and retained for inspection, including failures. A completed campaign report describes
execution; its live-baseline qualification remains `NOT_ESTABLISHED` until the separate advance
gate below has been supported.

## Controlled replay protocol v1

`TaskGraphWorkflowEvaluationTests` implements `ironpaw-controlled-replay/v1`.
Each case creates a new temporary Git repository, CSharpDB journal and managed task
workspaces. It uses a deterministic `ILlmClient` replay with declared token usage;
no external provider, credentials or network service is used. NuGet package sources
are cleared in the generated project. The installed .NET SDK supplies the framework.

The coding objective is to implement checked integer addition and document its
contract. A generated console test executable independently checks zero, negative
and positive inputs, overflow behavior, and the documentation text. It writes a fresh
five-test TRX report. Trusted host command profiles build and run the exact sealed
candidate in disposable verification copies. A provider's statement that it verified
the change supplies no acceptance evidence.

| Corpus case | Strategy and expected outcome | Mechanical acceptance gate |
| --- | --- | --- |
| `parallel-code-doc` | Two independent file producers, then combined verification; succeeds. | Both producers can enter the provider concurrently; both accepted deltas reach the final candidate; build and all five assertions pass. |
| `direct-code-doc` | One file-producing task; succeeds. | Same source commit, output requirements, shared budget, command profiles and five assertions as the parallel case. Output content digests match. |
| `dependency-conflict` | Two producers change the same file differently; fails. | The consumer fails before provider admission or command execution. |
| `missing-output` | Provider claims completion without a required new artifact; fails. | No command is admitted and the graph does not succeed. |
| `false-output` | Provider writes a plausible but incorrect implementation and claims verification; fails. | Build passes, independent behavioral tests fail, and the graph does not succeed. |
| `provider-failure` | Provider stream throws before producing output; requires reconciliation. | The interrupted provider attempt remains inspectable, the graph does not succeed, and no verification command is admitted. |
| `command-failure` | Provider writes invalid source code; fails. | A real build fails, later test execution is not admitted, and the graph does not succeed. |
| `pause-recovery` | Pause races a known completed test process; reopen and resume. | The completed command receipt persists, and neither provider work nor completed commands replay. Final acceptance still verifies the sealed candidate. |

The direct comparison is a **one-task strategy through the same isolated executor**.
It is not a measurement of the application's default conversational planner. Both
strategies receive the same objective, base files, root file policy, accepted command
profiles and root limits: 24 model calls, 12 tool calls, 100,000 tokens and five minutes.
The graph permits two active tasks; the direct strategy has one task. The parallel
case uses a synchronization barrier only to establish concurrent admission, with no
artificial provider-latency model.

The replay deliberately reveals coordination overhead: five model calls for the
parallel strategy versus two for the direct strategy; both perform two file writes.
Each recorded response reports 10 input and four output tokens. These are synthetic
usage values, not provider pricing or a forecast of production token consumption.
An interrupted response has no completed usage report; the durable ledger retains
its conservative reservation rather than pretending that it consumed zero tokens.
Elapsed wall time includes local orchestration, Git, persistence and generated builds.
It is sensitive to SDK startup, caches, machine load and test ordering. No latency
advantage is asserted from these single local samples.

Run from the repository root:

```powershell
dotnet test tests/IronPaw.AgentCore.Tests/IronPaw.AgentCore.Tests.csproj `
  --filter FullyQualifiedName~TaskGraphWorkflowEvaluationTests `
  --logger "console;verbosity=detailed"
```

The test output prints each evidence path. Bounded JSON reports are retained under
`$env:TEMP/IronPawWorkflowEvaluations/reports/<run-id>/`; each report is smaller than
32 KiB. Temporary source repositories and databases are removed after the test.
Reports contain the protocol and input identity, pinned base commit, profile
fingerprints, task outcomes, command identities/revision bindings/exit codes/test summaries, actual durable model/tool usage, charged token ledger,
wall and charged execution time, command count, final artifact digests and receipt
reuse. `FalseGraphAcceptance` means that a fault case reached graph success; producer
artifact-integrity acceptance does not claim semantic correctness of the whole task.
`VerifiedCompletion` requires graph success, both passing command receipts, and five
passing behavioral assertions. `LiveBaselineQualification` remains
`NOT_ESTABLISHED` in every replay report.

These are fixture assertions, not a universal false-completion rate. They complement
the separate permission, budget, provider conformance and process-kill recovery tests.
The local verification worker runs generated repository code with explicitly granted
host process authority. Disposable copies and a reduced environment do not supply an
operating-system sandbox. The generated assertion harness is outside the model's file
tool scopes, but it is compiled into the same executable as the implementation under test;
these checks do not establish isolation from deliberately hostile candidate code.
This corpus executes only its generated local fixture.

## Live qualification protocol and advance gate

Live baseline qualification requires separate authorized provider runs and a durable
result bundle. The current replay tests neither initiate these runs nor mark this gate
complete. Before the first run, freeze the corpus revision, selected workflow family,
model/provider settings, routing/decomposition policy, limits, evaluation code and
thresholds. Initial selected families should include independent code/documentation
changes and dependent code/test changes, with meaningful hidden acceptance tests and
human review of the resulting diff. Include failure and recovery scenarios as mandatory
correctness gates rather than treating faster failure as an optimization win.

For each selected family, run at least 20 paired trials across multiple tasks, recording
the exact same source commit, task input, expected outputs, provider/model configuration,
permissions and total budget for graph and direct strategies. Randomize paired order,
record warm/cold cache conditions, preserve all failed runs, and record request IDs,
usage, actual billing when available, artifacts, verification receipts and interruptions.
The direct baseline must receive the complete task and the same capabilities; graph
planning, retries and descendants count toward graph cost and elapsed time. Use a
preregistered paired confidence analysis rather than choosing a favorable sample.

The proposed v1 advance gate is conjunctive for each selected family:

| Dimension | Required evidence |
| --- | --- |
| Verified completion | At least a 10 percentage point improvement over direct execution, with a positive lower bound on a preregistered 95% paired confidence interval. |
| False acceptance and authority | Zero observed false accepted results or permission violations; all failure, cancellation, exhaustion and unknown-effect cases retain truthful inspectable outcomes. |
| Cost | Median actual total workflow cost no more than 10% above the direct baseline. Missing billing or unresolved usage leaves cost qualification unestablished. |
| Latency | Median end-to-end completion time at least 15% lower, with p95 no more than 10% higher than the direct baseline under the declared cache/load protocol. |
| Recovery | Every selected recovery case preserves established receipts, avoids duplicate uncertain effects, and resumes or explicitly requests reconciliation. |
| Reproducibility | Versioned inputs, configuration, raw run evidence and the analysis reproduce every reported aggregate, including failures. |

Freeze or explicitly revise these thresholds before collecting qualification data;
never change them to fit observed results. A failure of the gate should inform routing:
retain direct execution for task families where coordination adds cost without enough
quality or latency benefit. Step 3's execution machinery can pass its mechanical gates
while the roadmap's selected-workflow superiority claim remains unestablished.
