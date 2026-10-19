# JavaScript browser

Both hosts retain basic HTML browsing and can explicitly enable a Chromium backend. Chromium
uses a new nonpersistent context for each scoped browser session. Cookies and local storage are
not shared between those contexts or with a personal browser profile. Browser state is ephemeral:
after restart an unknown navigation or form effect requires reconciliation; it is not replayed.

The CLI accepts `--inspect-browser-config <file>` for a standalone review without starting a
browser, opening state or initializing providers. Use `--browser-config <file>` with a normal
workflow (or its saved resume) to select it. In Desktop, open the workflow settings tab, choose
**Review JavaScript browser sites**, inspect the grant, and select web browsing or all available
tools. Restart the host to change its browser configuration. Configuration is never discovered
from a repository or inferred from model/page content.

```json
{
  "schemaVersion": 1,
  "allowedOrigins": ["https://example.com"],
  "allowPrivateNetwork": false,
  "allowMutations": false,
  "maxSessions": 8,
  "timeoutSeconds": 30,
  "maxRequestsPerAction": 64,
  "maxResponseBytes": 2097152,
  "maxSessionBytes": 16777216
}
```

Origins must be exact HTTPS scheme/host/port grants, with no path, credentials, query or fragment.
Explicit loopback HTTP is available for local sites when private-network access is granted.
Other private addresses require that explicit network grant even when the origin is HTTPS.
Requests are mediated through the host's HTTP client, resolving and connecting to an allowed IP
in one operation. Proxies, automatic HTTP redirects and ambient cookies are disabled there.
Cross-origin resources need their own explicit origin grant; images, media and fonts are omitted.

GET redirects to granted document origins use a fresh intercepted navigation, capped at five.
The host does not pass an HTTP redirect chain to Chromium, where subsequent requests could bypass
route interception. Redirected subresources and redirects that could repeat a mutation fail
explicitly. Cookies from a permitted document redirect remain in that browser context.

Without `allowMutations`, methods other than GET/HEAD are blocked before dispatch, including
requests initiated by page scripts. Enabling mutations adds `web_navigate` to the workflow's
mandatory per-operation approvals. That operation can include the site's own requests within its
reviewed origins and limits. It is not an approval of one independently inspectable HTTP request.
Use a narrowly scoped origin grant and trusted sites for workflows that can change external data.

Network admission belongs to an active tool action. It closes before the tool returns and waits
for admitted requests to settle; later page timers cannot acquire new HTTP authority. WebSockets,
service workers, downloads, file uploads, worker constructors and direct WebRTC constructors are
disabled. Chromium's sandbox stays enabled. These controls and ephemeral contexts are not an OS
sandbox for arbitrary hostile browser-engine code or a guarantee against browser vulnerabilities.

The complete normalized browser configuration is hashed into `ToolSelectionPolicy.BrowserBinding`
and saved with the accepted workflow. Every inherited policy must grant the same binding, and the
tools recheck it at invocation. A broad tool profile alone cannot grant the configured JavaScript
browser. Changing origins, mutation authority or limits cannot expand an old workflow's grant.
Existing policies without a browser binding retain their original serialized shape.
Resume basic-HTML work without selecting a JavaScript configuration; resume JavaScript work with
exactly its originally reviewed configuration.

Browser aliases/returned IDs are scoped to the authenticated caller, root run, session and agent.
Supplying another workflow's ID cannot select its context. The bound is 1-16 contexts per host,
1-120 seconds per action, 1-256 requests per action, 4 KiB-4 MiB per response and at most 64 MiB of
response bytes per context lifetime. Request bodies are capped at 64 KiB and rendered HTML at
2 MiB. Cancellation closes and removes the affected context. There are no automatic action retries.
Hosts release browser contexts when an ordinary root run settles; paused/awaiting-input roots keep
their current in-process context. A cleanup failure closes admission and disables further browsing
until restart rather than repeatedly allocating untracked contexts. Embedding consumers using the
browser API directly own their session lifetimes.

`web_navigate` supports JavaScript button clicks through `link_selector` and an optional
`wait_selector` for the expected rendered state. Form field values remain data arguments, never
interpolated script. Extraction uses the same metadata/table/list representation as the HTML
backend. Returned page content remains untrusted. The runtime does not evaluate model-supplied
JavaScript or expose a general browser-code tool.

Local tests launch actual Chromium against loopback fixture sites and cover rendering, clicks,
form effects, cookie/local-storage isolation, redirect grants, timer requests, cancellation and
resource limits. They do not establish arbitrary-site compatibility, production browser security
qualification or live-model task quality. See [release packaging](releases.md) for the bundled
runtime and installation probe.
