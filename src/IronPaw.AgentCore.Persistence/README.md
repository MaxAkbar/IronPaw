# IronPaw.AgentCore.Persistence

`IronPaw.AgentCore.Persistence` contains file-based state storage for sessions, transcripts, memory, and run history.

## Responsibilities

- Maintain session metadata store with safe read/write semantics
- Persist conversation transcript messages as JSONL
- Persist global and session-scoped memory entries
- Record completed run summaries for task review
- Enforce session transcript path containment

## Main files

- `FileSessionStore.cs`
  - JSON session store with:
    - in-memory TTL cache
    - guarded mutation with `SemaphoreSlim`
    - atomic write flow (temp + replace/move)
    - retry logic for transient read issues
- `FileSessionStoreOptions.cs`
  - session-store configuration (store path, cache TTL)
- `JsonTranscriptStore.cs`
  - append/replace/load transcript messages (`.jsonl`)
  - path normalization and containment checks
- `FileMemoryStore.cs`
  - persistent memory store for global and session-scoped entries
  - FIFO eviction when limits are reached
  - search by keyword (case-insensitive substring match)
- `FileMemoryStoreOptions.cs`
  - memory-store configuration (global path, session directory)
- `JsonRunHistoryStore.cs`
  - append-only JSONL store for completed run summaries
  - supports load-all, load-by-session, and get-by-ID queries

## Data model integration

- Uses abstractions from `IronPaw.AgentCore.Abstractions`:
  - `SessionEntry`, `ChatMessage`
  - `ISessionStore`, `ITranscriptStore`
  - `IMemoryStore`, `MemoryEntry`, `MemoryScope`
  - `IRunHistoryStore`, `RunSummary`

## Storage location

Host projects currently use:
- `%LOCALAPPDATA%/IronPaw/state/session-store.json`
- `%LOCALAPPDATA%/IronPaw/state/sessions/<session>.jsonl`
- `%LOCALAPPDATA%/IronPaw/state/memories.json`
- `%LOCALAPPDATA%/IronPaw/state/sessions/<session>.memories.json`
- `%LOCALAPPDATA%/IronPaw/state/run-history.jsonl`

## Notes

- The implementation is intentionally inspectable and local-first.
- `JsonTranscriptStore` serializes one `ChatMessage` per line for easy debugging and recovery.
- `FileMemoryStore` enforces configurable limits: 100 global memories, 50 session memories, 500 chars per entry.
