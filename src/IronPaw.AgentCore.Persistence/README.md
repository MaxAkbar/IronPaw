# IronPaw.AgentCore.Persistence

`IronPaw.AgentCore.Persistence` contains file-based state storage for sessions and transcripts.

## Responsibilities

- Maintain session metadata store with safe read/write semantics
- Persist conversation transcript messages as JSONL
- Enforce session transcript path containment

## Main files

- `FileSessionStore.cs`
  - JSON session store with:
    - in-memory TTL cache
    - guarded mutation with `SemaphoreSlim`
    - atomic write flow (temp + replace/move)
    - retry logic for transient read issues
- `FileSessionStoreOptions.cs`
  - session-store configuration
- `JsonTranscriptStore.cs`
  - append/replace/load transcript messages (`.jsonl`)
  - path normalization and containment checks

## Data model integration

- Uses abstractions from `IronPaw.AgentCore.Abstractions`:
  - `SessionEntry`
  - `ChatMessage`
  - `ISessionStore`
  - `ITranscriptStore`

## Storage location

Host projects currently use:
- `%LOCALAPPDATA%/IronPaw/state/session-store.json`
- `%LOCALAPPDATA%/IronPaw/state/sessions/<session>.jsonl`

## Notes

- The implementation is intentionally inspectable and local-first.
- `JsonTranscriptStore` serializes one `ChatMessage` per line for easy debugging and recovery.
