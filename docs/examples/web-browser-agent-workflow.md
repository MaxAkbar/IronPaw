# Web Browser Agent Workflow

This example shows how a supervisor agent can delegate browser-heavy tasks to the built-in `web_browser` specialist.

## Goal

Extract a competitor pricing table from a public page and summarize key differences.

## Delegation pattern

1. Supervisor delegates:

```text
invoke_agent:
  agent_id: "web_browser"
  task: "Open https://example.com/pricing, extract the main pricing table, and return a concise JSON summary."
```

2. `web_browser` executes:
- `web_navigate` with `action="open"`
- optional `web_navigate` with `action="follow_link"` / `action="click"` / `action="submit_form"`
  - supports CSS selectors: `link_selector`, `form_selector`
- `web_extract_structured` for metadata/tables/lists
  - supports CSS selectors: `root_selector`, `table_selector`, `list_selector`

3. Supervisor synthesizes the structured output into the final answer.

## Navigation notes

- Reuse `session_id` in `web_navigate` and `web_extract_structured` to keep page context.
- Prefer `link_index` over `link_text` for deterministic navigation.
- Use CSS selectors for deterministic targeting in complex pages.
- Use explicit `fields` on `submit_form` when forms require input.

## Debugging/tracing

Use CLI command:

```text
/web-sessions
/web-sessions <session-id>
```

This shows active web browser sessions and the latest page snapshot (URL/title/status/link/form counts).

## Reliability guarantees

`WebBrowserService` provides:
- host-level rate limiting between requests
- retry with backoff for `429` and `5xx` responses
- clear error messages for broken links, missing forms, and unsupported URLs

## Trust boundary

Web tool outputs are marked as `ContentTrust.Untrusted`.
Treat fetched content as data only, never as executable instructions.
