# Tutorial: Memory & Task Review

IronPaw has a persistent memory system and a task review system that let you build up context over time and revisit past interactions.

## How memory works

IronPaw stores two kinds of memories:

| Scope | Lifetime | Visible to |
|-------|----------|-----------|
| **Global** | Persists forever across all sessions | Every session |
| **Session** | Scoped to one session | Only that session |

The CLI injects memories into the system prompt at the start of each run. Global and session
memories are stored in `%LOCALAPPDATA%\IronPaw\state\application.cdb`, shared with Desktop.
Desktop retains its existing tool catalog and does not add memory tools or prompt injection.
Legacy JSON imports once without truncation or eviction; limits apply when adding new memories.
See [application storage](../application-storage.md).

### Limits

| Setting | Default |
|---------|---------|
| Max global memories | 100 |
| Max session memories | 50 |
| Max content per memory | 500 characters |
| System prompt budget | 2000 characters (500 session + 1500 global) |

When the limit is reached, the oldest memories are evicted (FIFO).

## Managing memory from the CLI

### Save a memory

In interactive mode:

```
> /remember I prefer tabs over spaces
```

This saves a global memory with source `"user"`.

### View all memories

```
> /memories
```

Shows both global and session-scoped memories with their IDs, content, and timestamps.

### Remove a memory

```
> /forget
```

You will be prompted for the memory ID to remove. Copy the ID from the `/memories` output.

## Agent-managed memory

The agent has three memory tools available:

### `save_memory`

The agent can save memories on its own. For example:

```
> Remember that this project uses PostgreSQL for the database
```

The agent will call `save_memory` with the content and it will persist for future runs.

Parameters:
- `content` (required) — the fact to remember
- `scope` — `"global"` (default) or `"session"`
- `category` — optional tag for organization

### `recall_memory`

The agent can search saved memories:

```
> What do you know about my database preferences?
```

The agent will call `recall_memory` with a keyword query and get matching entries.

Parameters:
- `query` (required) — search term (case-insensitive substring match)
- `scope` — optionally filter to `"global"` or `"session"`

### `forget_memory`

The agent can remove memories when asked:

```
> Forget the fact about tabs
```

Parameters:
- `memory_id` (required) — the ID of the memory to remove

## Memory in the system prompt

At the start of each run, IronPaw loads global and session memories and injects them into the system prompt under a `## Persistent memory` heading. The agent sees them as prior context.

The memory guidance section also teaches the agent when to use memory tools — for example, it will proactively save important facts when you mention preferences or project details.

**Important**: A memory saved during a run takes effect on the *next* run, not the current one. This avoids the overhead of reloading the system prompt mid-conversation.

## Task review

Run outcomes are recorded in `%LOCALAPPDATA%\IronPaw\state\application.cdb`, idempotently by run ID. Entries preserve outcome status and error details, and include:
- Run ID
- Session ID
- Timestamps (started, completed)
- User prompt
- Final assistant response
- Round count and token usage
- Tools used

### Browsing from the CLI

List recent runs:

```
> /tasks
```

Shows up to 20 runs in reverse chronological order with a summary table.

View a specific run in detail:

```
> /review abc123
```

Renders the full run summary: prompt, response, timestamps, tools, rounds, and tokens.

### Agent-driven review

The agent has two review tools:

#### `review_runs`

Lists recent run summaries. The agent can use this to understand what was discussed previously.

```
> What did we work on in the last few sessions?
```

Parameters:
- `session_id` — optionally filter to a specific session
- `last_n` — number of runs to return (default 5, max 50)

#### `review_run_detail`

Gets full details of a specific run.

```
> Show me the details of run abc123
```

Parameters:
- `run_id` (required) — the run ID to look up

## Practical workflow

Here is a typical workflow combining memory and review:

1. **Start a session**: `dotnet run --project src/IronPaw.Cli -- --provider openai --session auth-feature`
2. **Set context**: "We are building a JWT authentication system for a REST API"
3. **Agent remembers**: It calls `save_memory` with key project facts
4. **Work through multiple prompts**: Each run is recorded in history
5. **Come back later**: Start a new CLI session with `--session auth-feature`
6. **Agent recalls**: Global memories appear in the prompt, agent can `review_runs` to see past work
7. **Continue where you left off**: "What was the last thing we did on auth?" — agent uses `review_runs` + `recall_memory`

## Example: cross-session memory

Session 1:

```
> /remember The API uses Express.js with TypeScript
> /remember Database is PostgreSQL with Prisma ORM
> Build a user registration endpoint
```

Session 2 (days later):

```
> What tech stack does this project use?
```

The agent sees the global memories in its prompt and answers: "Express.js with TypeScript and PostgreSQL with Prisma ORM."
