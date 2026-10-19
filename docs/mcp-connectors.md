# MCP connector adapter

IronPaw provides an embedding API and reviewed CLI/Desktop host setup in `IronPaw.AgentCore.Mcp`, using
[the official C# SDK](https://github.com/modelcontextprotocol/csharp-sdk/tree/v2.2.0).
It turns explicitly reviewed server tools into ordinary `ITool` instances so the existing
orchestrator owns tool hooks, approvals, durable intent/results, shared workflow budgets,
execution events and diagnostic spans. Construction and registry discovery do no transport I/O.

The CLI and Desktop can load explicit connector configurations, launch Windows stdio servers or
connect to exact HTTP endpoints. Application settings continue to exclude process authority,
connector grants and credentials. Isolated graph task registries continue to expose only their
mediated filesystem tools. No server or configuration is discovered automatically from a repository.

## CLI and Desktop setup

`--inspect-mcp-config <file>` validates and displays targets, launch arguments, executable/input
hashes, credential references/identities, schemas, tool identities and limits. It reads no secret
values, opens no application state and performs no process/transport I/O. The HTTP
[example document](examples/mcp.example.json) is a structural example, not an operational server.

`--mcp-config <file>` selects the reviewed file for an ordinary workflow or `--resume`. Each new
workflow explicitly grants those exact connector identities and requires per-operation approval.
Use the existing saved-workflow approval controls to approve or deny the concrete operation.
Inspection, graph and evaluation commands cannot enable connectors. A saved resume requires the
same binding and retains its original permissions and approval decisions.

In Desktop, open the workflow settings tab and choose **Review connector configuration**. Review
the complete document summary before accepting it. A checkbox controls whether new workflows get
the grants; saved workflows still retain their accepted grants. Restart to replace a reviewed
configuration. Default specialists receive no connector grants, and parent grants cannot expand
their policies.

Hosted transports have explicit incoming line, total received-byte and frame/request limits before
SDK decoding. Stderr is discarded in fixed-size buffers and charged against the same byte bound.
The defaults are 1 MiB per line, 16 MiB per connection and 4,096 frames/requests. Session exhaustion
poisons the connection; it does not trigger a reconnect or retry of an uncertain effect.

For `kind: "stdio"`, supply an absolute `command`, its `executableSha256`, an absolute
`workingDirectory` and an argument array. Optional `reviewedFiles` map absolute launch/script/input
paths to SHA-256 values. They are checked again at launch and held read-only during the session;
reviewed inputs are limited to 64 files and 512 MiB. The executable is capped at 512 MiB. Pin the
server's launch files and relevant dependencies, not just a generic `dotnet`/Node launcher. These
hashes cannot attest every dependency or prevent a trusted server changing its own behavior.

Arguments are passed without a shell. Only SystemRoot/WINDIR/TEMP/TMP are inherited automatically;
`environmentVariables` explicitly map child names to source environment-variable names. Secrets
are resolved at dispatch, never embedded in the document or review. Environment references require
a nonsecret `credentialIdentity`, which must change when the granted credential identity changes.

Windows stdio servers are assigned a kill-on-close job with default `processLimits` of
`maxMemoryMiB: 512`, `maxProcesses: 8` and `maxCpuPercent: 25`. Supported ranges are 64-4,096 MiB,
1-32 processes and 1-100 percent CPU. Failure to apply the job limits stops startup. Disposal kills
the owned process tree, and a parent-termination fixture verifies job cleanup even for a server
that intentionally survives pipe EOF. Assignment occurs immediately after process creation; this
is lifetime/resource control for a trusted server, not a hostile-code startup/security sandbox.
The process still has its OS user's filesystem/network authority. Hosted stdio requires Windows;
other platforms can use HTTP or supply their own reviewed embedding transport.

For `kind: "http"`, supply an exact HTTPS `endpoint` without user info, query or fragment. Only
explicitly granted loopback servers may use HTTP. The host pins an allowed resolved IP for each
connection, disables proxies/cookies/redirects and rejects private addresses unless
`allowPrivateNetwork` is explicitly true. Authentication uses an optional
`bearerTokenEnvironmentVariable` and required nonsecret `credentialIdentity`; no ambient/default
credentials or OAuth discovery flow are enabled. Obtain/rotate the scoped bearer externally.
HTTP session cleanup is bounded. Connection loss never causes automatic transport reconnection.

Every normalized transport option, process limit, reviewed input, credential reference/identity,
catalog and adapter limit contributes to the tool binding. Tool grants do not sandbox a stdio
server's internals, measure its billing or authorize extra model calls.

## Reviewed bindings and permission grants

The embedding host constructs `McpConnectorOptions` with a connector ID, a nonsecret
`BindingRevision`, and a reviewed catalog of remote tool names, descriptions, input schemas
and optional output schemas. Server discovery cannot populate this catalog or extend it.
The host supplies a factory for a new, unconnected SDK `IClientTransport`, and owns the
connector's asynchronous lifetime. The factory must return promptly without performing I/O;
transport connection, reads and writes must honor cancellation.

`BindingRevision` is the host's explicit identity for its reviewed transport configuration.
The host must change it when the target, launch command/arguments, executable revision,
working directory, credential identity or other authority changes. Include identities or
digests rather than secret values. The adapter cannot attest an arbitrary host factory or
prove that a server implementation still has the same behavior.

Each exposed name is `mcp_` followed by 60 hexadecimal characters. The digest binds the
connector ID, binding revision, complete reviewed catalog and configured limits. A changed
binding requires a new exact tool grant and cannot reuse the previous pending approval or
saved model inputs. Keep `ToolNames` together with the reviewed definitions in host UI so
users see the remote name and purpose alongside the exact identity.

Permission rules apply at registry resolution and again at actual invocation:

- The actual caller must be an owner. Setting `SenderIsOwner` alone cannot grant invocation.
- The profile must be `Coding` or `Full`, with each exact identity in
  `ToolSelectionPolicy.AllowedMcpTools`. Broad profiles grant no connectors by default.
- Existing allowlists, denylists and group restrictions still apply. Explicit group
  restrictions must include `group:mcp`.
- Every inherited policy must grant the identity. A child needs its own explicit grant as
  well as the grant from every ancestor. Existing built-in specialists have no MCP grants;
  the reviewer also retains its explicit read-tool allowlist.
- Human approval is controlled through the existing `ApprovalRequiredTools` request field.
  Requiring all connector tool names provides per-operation approval before any connection.

The connection advertises no sampling, elicitation, roots, tasks or experimental handlers.
Server instructions, descriptions and `readOnlyHint`/`idempotentHint` annotations do not
grant permissions or become system instructions. The initial handshake requests MCP
`2025-11-25`; broader protocol/transport interoperability needs separate qualification.

## Embedding

Add a project reference to `src/IronPaw.AgentCore.Mcp` in a trusted embedding host. For example,
after implementing and authorizing `CreateAuthorizedTransport` in that host:

```csharp
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Mcp;
using IronPaw.AgentCore.Tools;
using System.Text.Json;

var schema = JsonSerializer.Deserialize<JsonElement>("""
    {"type":"object","properties":{"query":{"type":"string","maxLength":256}},
     "required":["query"],"additionalProperties":false}
    """);

await using var connector = new McpConnector(
    new McpConnectorOptions("catalog", "reviewed-target-revision-1",
        [new McpToolDefinition("search", "Search the reviewed catalog", schema)]),
    CreateAuthorizedTransport);

// Supply this registry to AgentOrchestrator alongside the other normal tools.
var registry = new InMemoryToolRegistry(connector.Tools);
var policy = new ToolSelectionPolicy(ToolProfile.Coding) {
    AllowedMcpTools = connector.ToolNames
};
// Set request.ToolPolicy = policy and request.ApprovalRequiredTools = connector.ToolNames.
// Supply the authenticated caller and CSharpDB workflow store through the normal host path.
// Keep the connector alive until all runs using its tools have settled.
```

The transport factory is an authority boundary, not a sandbox. A future process host must
review launch files, use explicit process permission, isolate credential/environment delivery,
avoid shell interpolation, bound transport frames/queues and clean up owned processes. Remote
hosts additionally need endpoint/credential identity binding, authentication and connection
policy. None of those controls is implied by registering an SDK transport in this API.

## Validation, limits and returned content

Schemas are validated with [JsonSchema.Net](https://docs.json-everything.net/schema/basics/).
This slice accepts self-contained Draft 7 and Draft 2020-12 object input schemas; output schemas may
describe other JSON types. References, IDs, custom vocabularies, alternate dialects, `pattern`
and `patternProperties` are rejected at registration. Schemas are limited to 65,536 UTF-8
bytes, 16 subschema levels and 1,024 subschemas. Those deliberate restrictions avoid reference
resolution and schema-provided regular-expression execution in the connector adapter.

Input must be an object satisfying the reviewed schema. Duplicate JSON keys, more than
32 container levels, more than 4,096 JSON values or excessive input bytes are rejected
before connecting. Defaults are 65,536 input bytes, 16,384 result characters and a 30-second
deadline covering queue wait, connection, discovery and call. Configured ranges are 256 to
262,144 input bytes, 256 to 65,536 result characters, and 1 to 300 seconds. A workflow's
earlier cancellation/deadline still applies. One call at a time uses a connector instance.

Immediately before each call, bounded paged discovery checks the requested remote name and
both schemas against the reviewed binding. Extra server tools never become IronPaw tools.
Missing/duplicate tools, changed schemas, repeated cursors, more than 16 pages or more than
128 discovered tools reject dispatch. Property-order/whitespace differences in schemas are
ignored; other schema changes need review. Catalog matching cannot prove the absence of
server-side implementation changes or prevent a server changing between discovery and call.

Returned text and structured JSON remain `ContentTrust.Untrusted`. Binary/embedded resources
and resource links produce omission markers; the adapter never follows a resource URI.
The text sent back into IronPaw is capped, with an explicit truncation marker and metadata.
Successful structured output must satisfy any reviewed output schema. A server-declared
tool error or an invalid output schema is a completed error result, with no automatic retry.

The result cap applies **after SDK decoding**. It is not a wire-size or process-memory limit.
The trusted host must bound its transport before using an untrusted server. Synchronous
schema validation uses reviewed schemas and bounded input; it is not preempted by cancellation.

## Durable execution and failures

When these tools run through `AgentOrchestrator` with CSharpDB, the normal tool path records
intent before connecting or dispatching and charges the shared tool-call budget. Model-token
budgets retain their existing meaning; they do not measure connector billing or enforce a
monetary spending cap. Direct `ITool.ExecuteAsync` calls do not provide durable workflow
budgets or approvals, just as with other IronPaw tools.

Returned results are committed and replayed without connector I/O, including when a later
model round is interrupted. Terminal replay also needs no connector. Pending approval and
tool execution remain bound to the accepted tool identity and operation fingerprint.

A transport error, malformed response, lost response, or cancellation after dispatch may
follow an external effect. The adapter poisons that connection and propagates the failure
through the existing unknown-outcome path. Resume requires reconciliation; it never
automatically reissues that saved operation. Reconciliation supplies an externally established
result and performs no connector I/O. Protocol exception payloads are replaced with a fixed
host error so server text is not promoted to trusted diagnostic content. Cancellation while
waiting for the connector queue does not poison a call that has not begun.

Failure during connection/discovery also follows this conservative unknown-outcome path once
the workflow has recorded `tool_started`; it does not claim that an external effect occurred.
Dispose the connector to cancel its in-flight work and close the SDK client/owned transport.

## Evidence and remaining work

`IronPaw.AgentCore.Mcp.Tests` exercises the real SDK over paired local streams against an
independent JSON-RPC fixture. Coverage includes discovery/calls, explicit and inherited grants,
caller isolation, input/output validation, catalog drift, bounded presentation, cancellation,
timeouts, serialized calls, disposal, human approvals, persistent tool budgets, physical
CSharpDB reopen, partial/terminal replay, changed bindings, delegated callers and reconciliation.
It uses no external connector, network service, provider or credentials.

Host tests also exercise real subprocesses and loopback HTTP peers, bearer delivery, redirect/private
network rejection, executable/input drift, traffic exhaustion, poisoned connections, process
disposal and abrupt parent termination. Release bundles run the actual CLI/Desktop executables
with fresh local state. Third-party production-server interoperability and hosted CI require their
own observed results; fixture peers do not establish universal connector compatibility, hostile-code
OS isolation, live-provider quality or cost/latency superiority.
