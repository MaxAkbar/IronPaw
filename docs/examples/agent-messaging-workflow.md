# Agent Messaging Workflow

This walkthrough demonstrates end-to-end agent-to-agent messaging with:

- multi-agent delegation (`invoke_agent`)
- scoped messaging (`send_agent_message`, `read_agent_messages`)
- observability events (`message.sent`, `message.received`)
- durable message queues when using SQLite store backend

## Prerequisites

- A live provider configured (for example OpenAI or Anthropic).
- Full profile enabled so agent tools are available.

## Run the workflow

Use SQLite store backend so messages are persisted in `ironpaw.db`:

```powershell
dotnet run --project src/IronPaw.Cli -- `
  --store sqlite `
  --provider openai `
  --model gpt-4o-mini `
  --profile Full `
  --session messaging-demo `
  --prompt "Plan and implement a tiny feature update. Delegate implementation to codegen and review to reviewer. Require codegen to send a structured handoff message to reviewer, and require reviewer to read agent messages before producing findings. Return a final summary that includes what was handed off."
```

## Expected behavior

1. Supervisor routes work with `invoke_agent`.
2. `codegen` sends a message to `reviewer` using `send_agent_message`.
3. `reviewer` reads pending messages via `read_agent_messages`.
4. CLI event stream includes:
   - `Diagnostics/message.sent`
   - `Diagnostics/message.received`
5. Final response summarizes the handoff and review output.

## Event stream example

You should see lines similar to:

```text
12:34:56.123 @codegen Diagnostics/message.sent recipient_agent_id=reviewer, message_id=...
12:34:56.456 @reviewer Diagnostics/message.received recipient_agent_id=reviewer, message_count=1
```

## Inspecting message scopes in interactive mode

In CLI interactive mode, list recent run scopes for the current session:

```text
/scope-runs --limit 10
```

Then inspect queued messages for a specific scope run:

```text
/agent-messages <scope-run-id> reviewer --peek
```

Filter by sender/correlation when investigating a workflow:

```text
/agent-messages <scope-run-id> reviewer --sender codegen --correlation corr-123 --peek
```

## Notes

- Messaging is scoped to the same root run/session lineage.
- Authorization is enforced by `AllowedMessageSenders` on each `AgentDefinition`.
- If authorization fails, `send_agent_message` returns an error and no message is enqueued.
