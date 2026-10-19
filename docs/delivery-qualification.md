# Delivery qualification

The original four-milestone roadmap distinguishes implementation from evidence of task quality.
Mechanical checks cannot establish that a live orchestration strategy outperforms its baseline.

| Milestone | Implemented behavior | Qualification boundary |
|---|---|---|
| Reliable execution | Explicit outcomes, boundary fixes, inherited restrictions and failure recording | Component/fault checks in Release validation |
| Durable workflows | CSharpDB journals, shared budgets, control/input receipts, recovery and reconciliation | Real reopen/concurrency/process-termination checks |
| Verified orchestration | Dependency graphs, isolated candidates, bound command verification, acceptance gates and configurable paired campaigns | Local mechanics/replay; live quality/cost/latency remains `NOT_ESTABLISHED` |
| Product maturity | Shared settings, timeline, diagnostics, MCP host controls, JavaScript browsing, portable Windows packaging and dependency workflows | Fresh local application/package evidence plus an observed hosted CI result |

## Repeatable local evidence

Follow [release instructions](releases.md). Retain the complete Release source manifest and test
reports, dependency audit, published-host smoke reports/screenshots, per-file package manifest and
archive digest. The source snapshot includes uncommitted files; draft packages identify that fact.
Only a passing run against unchanged source qualifies that snapshot. Failing attempts remain
evidence and do not establish a release candidate.

Tests cover real CSharpDB and subprocess recovery, paired local MCP streams, independent local
HTTP/process peers and actual Chromium loopback sites. The packaged CLI and Desktop run through
their normal composition roots with isolated local state, and the packaged browser executes a
static JavaScript installation probe without network admission. These are deliberately bounded
checks, not universal server/site compatibility or hostile-code OS-isolation claims.

## External proof gates

- A completed hosted workflow must demonstrate the checks against the published source commit.
  Creating YAML, producing a local ZIP, or holding unpushed commits does not establish hosted CI.
- The Step 3 campaign needs a chosen provider, exact model, reviewed prices and total spending cap,
  followed by actual paired trials and the documented independent review/billing/statistical gates.
  This remains disabled until explicitly configured and authorized. Configuration and scripted
  replay never change the `NOT_ESTABLISHED` qualification.
- Qualify the actual production connector endpoints/authentication and representative site
  workflows before claiming compatibility for a deployment. Local fixture/reference peers do not
  establish another server's behavior, credentials, external effects or billing.
- Public release publication/signing and distribution require explicit authorization and their
  own recorded outcome. A workflow artifact is a candidate, not a publicly published release.

Broader recommendations in the original architectural analysis remain a source of future work.
In particular, trusted in-process skill extensions and trusted connector processes are not a
restricted worker sandbox for arbitrary generated code. Provider/context redesign and broader
task-corpus coverage need their own specifications and qualification. Do not read completion of
these mechanical delivery slices as a claim that every architectural recommendation or the
overall world-class quality target has been achieved.
