# Tutorial: Interactive Mode

IronPaw's CLI includes a full interactive REPL for conversational use, session management, memory, and task review.

## Starting interactive mode

Launch without a `--prompt` argument:

```powershell
dotnet run --project src/IronPaw.Cli -- --provider openai
```

You will see a welcome banner with your current settings (provider, model, session, profile) and a prompt waiting for input.

## Sending prompts

Type any message and press Enter:

```
> What files are in the current directory?
```

The agent will stream events (lifecycle, tool calls, assistant text) and render the final response in a panel.

## Slash commands

Type `/help` to see all available commands:

| Command | Description |
|---------|-------------|
| `/help` | Show the command list |
| `/settings` | Display current provider, model, session, and profile |
| `/provider` | Switch LLM provider interactively |
| `/model` | Change the model for the current provider |
| `/session` | Switch to a different session (conversation thread) |
| `/remember` | Save a global memory |
| `/forget` | Remove a memory by ID |
| `/memories` | List all global and session memories |
| `/tasks` | Show recent run history |
| `/review` | View details of a specific past run |
| `quit` / `exit` / `/quit` / `:q` | Exit the REPL |

## Switching providers on the fly

```
> /provider
```

A selection menu appears. Pick a new provider and continue chatting — no restart needed. If the selected provider requires an API key that is not set, you will be prompted to enter it.

## Working with sessions

Sessions are named conversation threads. Transcripts, metadata, and session-scoped memories are isolated per session.

```
> /session my-feature
```

This switches to the `my-feature` session. If it already exists, the previous transcript is restored and the agent continues where it left off. If it is new, you start fresh.

Switch back any time:

```
> /session default
```

## Reviewing past tasks

List recent runs across all sessions:

```
> /tasks
```

This shows up to 20 recent runs in reverse chronological order, including run ID, session, prompt preview, round count, and token usage.

To see full details of a specific run:

```
> /review <run-id>
```

This renders the complete run summary: user prompt, final response, timestamps, tools used, rounds, and tokens.

## Using memory

Save a fact for future sessions:

```
> /remember I prefer tabs over spaces
```

View all saved memories:

```
> /memories
```

This shows both global memories (persist across all sessions) and session memories (scoped to the current session).

Remove a memory:

```
> /forget
```

You will be prompted for the memory ID to remove.

The agent also has access to memory tools (`save_memory`, `recall_memory`, `forget_memory`) and will use them autonomously when appropriate. For example, if you say "Remember that this project uses PostgreSQL", the agent will call `save_memory` on its own.

## Tool profiles

The `--profile` flag controls which tools are available:

| Profile | Available tool groups |
|---------|---------------------|
| `Minimal` | `group:system` only (time, memory, review) |
| `Coding` | `group:fs` + `group:system` + `group:skills` (default) |
| `Messaging` | `group:messaging` + `group:system` + `group:skills` |
| `Full` | All groups including `group:runtime` (dynamic skill creation) |

Example with full access:

```powershell
dotnet run --project src/IronPaw.Cli -- --provider openai --profile Full
```

## Tips

- **Session continuity**: Use named sessions (`--session feature-auth`) to keep related conversations grouped. Transcripts survive restarts.
- **Pipe input**: You can pipe prompts from files or other commands: `echo "List files" | dotnet run --project src/IronPaw.Cli -- --provider local`
- **Suppress events**: Use `--no-events` for cleaner output when scripting.
- **Prompt files**: Use `--prompt-file path/to/prompt.txt` for long prompts.
