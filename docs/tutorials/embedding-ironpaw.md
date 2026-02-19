# Tutorial: Embedding IronPaw in Your Own Application

IronPaw is designed as a headless engine. The CLI and Desktop apps are thin hosts over a shared core. You can embed the same engine in your own application — a web API, a background service, a chatbot, or anything else.

## Architecture overview

```
Your Application
    ↓
AgentOrchestrator.RunAsync(request, eventSink, ct)
    ↓
Returns AgentRunResult (final message, transcript, metadata)
```

Your app provides the request and an event sink. IronPaw handles everything else: session management, transcript persistence, tool execution, LLM communication, and memory.

## Step 1: Reference the projects

Add these project references (or NuGet packages, if published):

```xml
<ItemGroup>
  <ProjectReference Include="..\IronPaw.AgentCore\IronPaw.AgentCore.csproj" />
  <ProjectReference Include="..\IronPaw.AgentCore.Abstractions\IronPaw.AgentCore.Abstractions.csproj" />
  <ProjectReference Include="..\IronPaw.AgentCore.Tools\IronPaw.AgentCore.Tools.csproj" />
  <ProjectReference Include="..\IronPaw.AgentCore.Persistence\IronPaw.AgentCore.Persistence.csproj" />
  <ProjectReference Include="..\IronPaw.AgentCore.Providers\IronPaw.AgentCore.Providers.csproj" />
</ItemGroup>
```

## Step 2: Wire up the components

Here is a minimal setup using the OpenAI provider:

```csharp
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Providers;
using IronPaw.AgentCore.Tools;

// Storage paths
var stateDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "MyApp", "state");
Directory.CreateDirectory(stateDir);

var sessionsDir = Path.Combine(stateDir, "sessions");

// Persistence
var sessionStore = new FileSessionStore(Path.Combine(stateDir, "session-store.json"));
var transcriptStore = new JsonTranscriptStore(sessionsDir);

// Tools
var tools = BuiltInToolFactory.CreateDefault(
    workspaceRoot: Environment.CurrentDirectory);
var toolRegistry = new InMemoryToolRegistry(tools);

// Provider
var llmClient = new RoutedLlmClient(
    providers: new Dictionary<string, ILlmClient>
    {
        ["openai"] = new OpenAiLlmClient(
            () => Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            baseUrl: "https://api.openai.com")
    },
    fallback: new RuleBasedLlmClient());

// Orchestrator
var orchestrator = new AgentOrchestrator(
    laneScheduler: new SessionLaneScheduler(),
    sessionStore: sessionStore,
    transcriptStore: transcriptStore,
    toolRegistry: toolRegistry,
    toolExecutor: new ToolExecutor(),
    schemaNormalizer: new ToolSchemaNormalizer(),
    llmClient: llmClient,
    transcriptSanitizer: new TranscriptSanitizer(),
    toolResultTruncator: new ToolResultTruncator(),
    promptBuilder: new DefaultSystemPromptBuilder(toolRegistry));
```

## Step 3: Run a request

```csharp
var request = new AgentRunRequest(
    SessionKey: new SessionKey("my-session"),
    UserInput: "What files are in the current directory?",
    Provider: "openai",
    Model: "gpt-4o-mini",
    ToolPolicy: ToolSelectionPolicy.Default("Coding"));

var result = await orchestrator.RunAsync(
    request,
    NullAgentEventSink.Instance,
    CancellationToken.None);

Console.WriteLine(result.FinalAssistantMessage);
Console.WriteLine($"Rounds: {result.Metadata["rounds"]}");
Console.WriteLine($"Tokens: {result.Metadata["totalTokens"]}");
```

## Step 4: Handle events (optional)

To stream events in real-time (for a chat UI, logging, etc.), implement `IAgentEventSink`:

```csharp
public sealed class MyEventSink : IAgentEventSink
{
    public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct)
    {
        switch (evt.Stream)
        {
            case AgentEventStream.Assistant when evt.Type == "assistant_delta":
                // Stream text to UI as it arrives
                Console.Write(evt.Data?["text"]);
                break;

            case AgentEventStream.Tool when evt.Type == "tool_call":
                Console.WriteLine($"\n[Tool: {evt.Data?["name"]}]");
                break;

            case AgentEventStream.Lifecycle when evt.Type == "run_completed":
                Console.WriteLine($"\n[Done — {evt.Data?["rounds"]} rounds]");
                break;
        }

        return ValueTask.CompletedTask;
    }
}
```

Use it:

```csharp
var result = await orchestrator.RunAsync(request, new MyEventSink(), ct);
```

## Adding memory and run history

```csharp
// Memory store
var memoryStore = new FileMemoryStore(new FileMemoryStoreOptions(
    GlobalMemoryPath: Path.Combine(stateDir, "memories.json"),
    SessionMemoriesDirectory: sessionsDir));

// Run history store
var runHistoryStore = new JsonRunHistoryStore(
    Path.Combine(stateDir, "run-history.jsonl"));

// Include memory/review tools
var tools = BuiltInToolFactory.CreateDefault(
    workspaceRoot: Environment.CurrentDirectory,
    memoryStore: memoryStore,
    runHistoryStore: runHistoryStore);

// Pass to orchestrator
var orchestrator = new AgentOrchestrator(
    // ... same as before ...
    memoryStore: memoryStore,
    runHistoryStore: runHistoryStore);
```

## Multiple providers

Route between providers by adding more entries to the `RoutedLlmClient`:

```csharp
var llmClient = new RoutedLlmClient(
    providers: new Dictionary<string, ILlmClient>
    {
        ["openai"] = new OpenAiLlmClient(
            () => Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            baseUrl: "https://api.openai.com"),
        ["anthropic"] = new AnthropicLlmClient(
            () => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
        ["ollama"] = new OpenAiLlmClient(
            () => "ollama",
            baseUrl: "http://localhost:11434"),
        ["local"] = new RuleBasedLlmClient()
    },
    fallback: new RuleBasedLlmClient());
```

Then set `Provider` in the request to route:

```csharp
var request = new AgentRunRequest(
    SessionKey: new SessionKey("my-session"),
    UserInput: "Hello",
    Provider: "anthropic",             // route to Anthropic
    Model: "claude-3-5-haiku-latest",
    ToolPolicy: ToolSelectionPolicy.Default("Coding"));
```

## Loading skill add-ins

```csharp
using IronPaw.AgentCore.Tools;

var skillTools = SkillAddinLoader.LoadFromDirectories([
    "./addins/skills",
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IronPaw", "skills")
]);

var allTools = BuiltInToolFactory.CreateDefault(workspaceRoot: ".")
    .Concat(skillTools)
    .ToList();

var toolRegistry = new InMemoryToolRegistry(allTools);
```

## Session continuity

Sessions survive across runs automatically. Use the same `SessionKey` to continue a conversation:

```csharp
// First interaction
var result1 = await orchestrator.RunAsync(
    new AgentRunRequest(new SessionKey("chat-1"), "My name is Alice", "openai", "gpt-4o-mini",
        ToolSelectionPolicy.Default("Coding")),
    NullAgentEventSink.Instance, ct);

// Second interaction — same session, transcript is preserved
var result2 = await orchestrator.RunAsync(
    new AgentRunRequest(new SessionKey("chat-1"), "What is my name?", "openai", "gpt-4o-mini",
        ToolSelectionPolicy.Default("Coding")),
    NullAgentEventSink.Instance, ct);

// result2.FinalAssistantMessage will reference "Alice"
```

## Tool profiles

Control which tools the agent can use via `ToolSelectionPolicy`:

```csharp
// Only system tools (time, memory, review)
ToolSelectionPolicy.Default("Minimal")

// File system + system + skills (default)
ToolSelectionPolicy.Default("Coding")

// Everything including runtime skill creation
ToolSelectionPolicy.Default("Full")

// Custom: explicit allow/deny lists
new ToolSelectionPolicy(
    AllowedGroups: ["group:system", "group:fs"],
    DenyList: ["write_file"])  // allow reads but block writes
```

## Summary

The key integration points are:

| Component | Your responsibility |
|-----------|-------------------|
| `AgentRunRequest` | Provide session key, user input, provider, model, policy |
| `IAgentEventSink` | Handle streaming events (or use `NullAgentEventSink`) |
| `AgentRunResult` | Read the final message, metadata, and transcript |
| Storage paths | Choose where to persist sessions, transcripts, and memory |
| API keys | Provide keys via environment variables or config |
