# Application storage and JSON migration

CLI and Desktop use CSharpDB for their shared application data. The default database is
`%LOCALAPPDATA%/IronPaw/state/application.cdb`; embedding hosts can select a different state root.
It contains session metadata, ordered transcripts, global and session memories, run history and
agent messages. `StoreBackend` and the CLI `--store` selector have been removed. Obsolete flags
fail before application state or providers initialize, with instructions to remove the flag/value.

Workspace workflow databases remain at `state/workflows/<workspace-id>/workflows.cdb`, using the
same normalized workspace identity. Their accepted requests, execution journals, receipts, schema
upgrades, permission bindings and recovery controls are unchanged. Workflow inspection and
reconciliation do not trigger application-data import.

## First startup with JSON data

Close older IronPaw instances before the first migration. New hosts coordinate with an application
file lock; older JSON writers do not participate in this protocol and must stay closed during import.
The first application-store operation imports these sources from the configured state root:

| Source | Imported records |
| --- | --- |
| `session-store.json` | Session metadata dictionary, including original timestamps and diagnostics |
| `sessions/<safe-name>.jsonl` | Transcripts, retaining message and tool-call order |
| `memories.json` | Global memories |
| `sessions/<safe-name>.memories.json` | Session memories |
| `run-history.jsonl` | Run summaries, including status and error fields |

Imported records and a completion marker commit together. Import preserves original values and
per-file ordering; it applies no memory retention or content truncation. Older history entries
without status/error retain the compatible succeeded/null defaults. Empty state roots also record
completion, so later stale JSON files are not unexpectedly imported. After success, the database
is authoritative and the original files are never read again by normal application operations.

All original JSON and SQLite files remain intact. SQLite data is **not imported**. JSON adapters
remain in `IronPaw.AgentCore.Persistence` for existing library consumers. There is no ongoing dual
write or fallback to JSON. Preserve backups when changing application versions: an older host
continues writing its own files and does not understand the new application database.

## Import errors and retry

Import reads a snapshot and rechecks its file set, modification times and content hashes before
committing. A changed, removed or newly added source aborts import. Cancellation, malformed JSON,
duplicate properties, duplicate run IDs, inconsistent identities and unidentified session files
also fail without importing partial records or recording completion. Errors identify the source
and explain how to retry. An empty database schema may exist after failure.

Correct or relocate the identified source while older instances are closed, then retry application
access. Do not delete an existing application database to retry a failed first import: without the
completion marker the importer retries automatically. Interrupted processes release their OS file
locks; uncommitted transactions recover on the next open.

Session filenames use the same rules as the JSON adapters: conventional lowercase ASCII keys
are reversible filenames; other keys, including Unicode and case-sensitive keys, use a SHA-256
name beginning with `~`. Hash-named files require the original full session key from session
metadata, history or memory entries. Lossy older sanitized names are never guessed. Restore the
full identity and map the file explicitly to the safe filename when resolving an ambiguity.
Unknown JSON/JSONL files in the sessions directory fail import; lock files and temporary write
remnants are not data sources. Transcript paths in old metadata are preserved as provenance;
the importer only reads sources in the configured state directory.

## Operation and retention behavior

Each application operation runs off the desktop dispatcher and acquires `application.cdb.lock`,
with cancellation support and a 30-second acquisition timeout. It opens CSharpDB only while
holding the lock and closes it before releasing the lock. Multiple processes and workspaces
share application records through this protocol. A persistent, unlocked sidecar file is normal;
do not delete it while processes are running. Workspace workflow databases retain their existing
exclusive lifetime ownership, so this does not allow two hosts to own one workflow journal.

Session mutations and transcript append/replacement commit atomically. A failed or cancelled
replacement retains the previous transcript. New memories retain the defaults of 100 global
entries, 50 per session and 500 characters per entry, evicting oldest inserted entries when a new
add exceeds a limit. Imports can temporarily exceed these limits until the next add to that scope.
Search remains case-insensitive and includes only global/requested-session memories.

Run history is idempotent by run ID; recording the same run updates its outcome in place without
duplicating it or changing its position. Agent sends preserve registry authorization. Reads retain
session/root-run/recipient isolation, sender and correlation filters, FIFO order, peek/read state,
and the existing 1–100 message read limit. Selection and marking as read share one transaction so
concurrent consumers cannot consume a message twice. Expired messages are removed on send/read.
The CLI retains `--message-ttl-days` with a 30-day default; Desktop uses 30 days. Embedding options
can disable expiration with a nonpositive value.

The CLI keeps memory tools and prompt injection. Desktop keeps its existing tool catalog without
adding memory tools or memory prompt injection. Persisted requests retain their saved permissions.
CLI transcript displays describe `application.cdb`, including for imported legacy sessions.
