# Application settings and validation

The CLI and Desktop use the same validated settings format for new runs and the same specialist
catalog. Default provider/model routing, workflow token/time limits and compatible-provider context
options no longer need separate host configuration. Specialist instructions and permissions remain
defined by the host; the settings file can change only their provider/model routing.

## Shared user settings

Copy [settings.example.json](examples/settings.example.json) to
`%LOCALAPPDATA%/IronPaw/settings.json`. The default remains `local`, with no configured model or
workflow token/time limits. A missing default user file uses those defaults without creating a file.
Set `IRONPAW_CONFIG` to select another reviewed file for both apps. An explicitly selected missing
file fails instead of silently falling back. Repository-local settings are never discovered
automatically. This file cannot contain credentials, workspace paths, permission grants or live
evaluation settings; unknown properties are rejected.

For a new CLI run, `--config <path>` selects a file ahead of `IRONPAW_CONFIG`. Precedence for values is:

1. Built-in defaults.
2. The selected user settings file.
3. Supported environment overrides.
4. Explicit CLI flags, or the Desktop's visible new-run controls.

Supported environment overrides are `IRONPAW_PROVIDER`, `IRONPAW_MODEL`, the existing
`IRONPAW_LLM_CONTEXT_WINDOW_TOKENS`, `IRONPAW_LLM_SAFETY_MARGIN_TOKENS`,
`IRONPAW_LLM_MIN_OUTPUT_TOKENS_PER_REQUEST`, `IRONPAW_LLM_MAX_AUTO_CONTINUATION_PASSES`, and the
existing `IRONPAW_CODEGEN_*`, `IRONPAW_REVIEWER_*`, `IRONPAW_WEB_BROWSER_*` provider/model pairs.
Changing a provider clears a model inherited from a different provider unless a model override is
also supplied. Specialist routes left null retain the existing parent-inheritance behavior.
Provider credentials and local-server endpoint environment variables keep their existing handling;
they are not copied into settings or validation reports.

The loader validates the file before applying overrides. Invalid JSON, duplicate names (including
case variants), unknown fields, quoted numeric values, unsupported schemas/providers, invalid model
IDs, nonpositive budgets and contradictory LLM context limits fail before application state opens.
Do not use overrides to repair a malformed file. Compatible-provider tuning is snapshotted at
startup so constructing the lazy client does not reread those environment options later.

```powershell
dotnet run --project src/IronPaw.Cli -- --validate-config docs/examples/settings.example.json
dotnet run --project src/IronPaw.Cli -- --config C:\IronPaw\settings.json --provider local --prompt "Hello"
```

`--validate-config` is standalone. It checks only the named file, without reading provider
credentials, opening application state, discovering add-ins or executing a run. The Desktop
prefills provider, model and token/time controls from the same settings snapshot. Editing those
controls affects a new run and does not rewrite the user file.

## Saved work and evaluations

`--config` cannot be combined with workflow/graph controls or live evaluation commands. Saved
workflow/graph CLI commands bypass current user settings so malformed defaults do not prevent
inspection or recovery. Existing accepted requests, provider/model bindings, permissions and
budgets remain authoritative. Saved execution retains the existing environment handling for
specialist routing and lazy client tuning; inspecting saved state does not validate those values
or initialize a provider. New user-file defaults are not applied to saved execution. Per-run
provider/client snapshot versioning is a separate future change.

Live evaluations use their separate [configuration and immutable campaign binding](orchestration-evaluations.md).
Application settings cannot enable evaluations or replace campaign prices/caps. The Step 3 live
quality/cost/latency gate remains `NOT_ESTABLISHED`.

## Repeatable local and CI checks

The repository's `global.json` pins SDK `10.0.402` with SDK roll-forward disabled. Install that SDK
before building; an unrelated preview SDK is not selected automatically. See Microsoft's
[SDK selection rules](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json).

Run the same check used by GitHub Actions:

```powershell
./eng/Validate.ps1 -OutputDirectory C:\Temp\IronPaw-validation-001
```

The output directory must be new or empty. The script restores dependencies, builds the whole
solution in Release with warnings treated as errors, runs all test projects, requires fresh positive
TRX results with zero failures/skips, and validates both example settings documents. It clears
provider credentials and runtime defaults within the script process, uses the safe application
example, then restores that process's environment. It does not run live evaluation campaigns.

The nine test projects use MSTest 4.5.1 with Microsoft's test SDK and adapter. Test classes can run
concurrently; methods and data rows within each class stay sequential. Execution-diagnostics tests
run separately because they observe process-wide telemetry. Fixture cleanup uses `TestCleanup`,
and desktop tests retain their explicit STA dispatcher probes. CSharpDB process-recovery tests
retain their dedicated subprocess entry point. Coverage uses the existing Coverlet collector.

Evidence includes the build/test log, every TRX, selected SDK, source revision, dirty-worktree flag,
SHA-256 source-file manifest and final summary. Source changes during validation prevent a passing
summary. The source manifest includes uncommitted local
files so the commit ID alone is not mistaken for the complete tested source. Default local output
under `artifacts/` is ignored by Git. Generated fixture workspaces use their existing private
temporary directories.

The Windows GitHub workflow runs on pushes to `main`/`max/**`, pull requests and manual dispatch.
It uses read-only repository permissions, pinned action revisions, no persisted checkout token,
and retains validation artifacts even on failure. The pinned SDK is installed through
[setup-dotnet's global.json support](https://github.com/actions/setup-dotnet#using-the-global-json-file-input).
Hosted CI execution is established only when the workflow actually runs after publication.

Step 4 also includes the desktop execution timeline and
[execution diagnostics and resilient live delivery](execution-observability.md). The first
[MCP adapter slice](mcp-connectors.md) provides reviewed tool bindings and durable execution for
embedding hosts; CLI/Desktop connector setup and transport controls remain pending. A JavaScript
browser backend, packaging and a dependency maintenance policy also require their own
implementation and end-to-end release evidence.

Application storage is always CSharpDB. The removed `--store` selector fails with a migration
message before initialization. `--message-ttl-days` controls CLI message retention (default 30);
Desktop uses the same default. See [application storage and JSON import](application-storage.md).
