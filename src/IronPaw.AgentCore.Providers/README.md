# IronPaw.AgentCore.Providers

`IronPaw.AgentCore.Providers` contains LLM client adapters implementing `ILlmClient`, provider configuration defaults, and shared factory logic.

## Responsibilities

- Convert IronPaw's internal request/delta model into provider-specific HTTP APIs
- Parse provider responses into normalized deltas:
  - `AssistantTextDelta`
  - `ToolCallDelta`
  - `UsageDelta`
  - `CompletedDelta` / `LlmErrorDelta`
- Route calls to provider clients by name
- Provide shared provider defaults and factory wiring for all host applications

## Implementations

- `RuleBasedLlmClient.cs`
  - local deterministic provider for development/testing
  - no API key required
- `OpenAiLlmClient.cs`
  - uses OpenAI chat completions endpoint
  - supports function tool calls
  - reads `OPENAI_API_KEY`
  - also powers OpenAI-compatible local clients (`ollama`, `lmstudio`) via custom base URLs
- `AnthropicLlmClient.cs`
  - uses Anthropic messages endpoint
  - supports `tool_use` / `tool_result`
  - reads `ANTHROPIC_API_KEY`
- `RoutedLlmClient.cs`
  - `provider -> ILlmClient` dispatch with fallback client

## Shared configuration

- `ProviderDefaults.cs`
  - canonical `ProviderNames` list and `DefaultModels` dictionary
  - single source of truth for both CLI and Desktop hosts
- `LlmClientFactory.cs`
  - `Build()` — creates a fully wired `RoutedLlmClient` with all providers
  - `ResolveApiKey()` — resolves an API key from environment with fallback
  - `ResolveOpenAiCompatibleBaseUrl()` — normalizes base URLs for Ollama/LM Studio

## Environment variables

- `OPENAI_API_KEY`
- `ANTHROPIC_API_KEY`
- `OLLAMA_BASE_URL` (default: `http://localhost:11434`)
- `OLLAMA_API_KEY` (optional; default: `ollama`)
- `LMSTUDIO_BASE_URL` (default: `http://localhost:1234`)
- `LMSTUDIO_API_KEY` (optional; default: `lm-studio`)

## Notes

- OpenAI and Anthropic parse SSE streams and report usage as increments, including Anthropic cache read/write usage.
- `LlmRequest.MaxAutoContinuationPasses` can lower OpenAI's configured continuation cap. A value of zero permits only the initial HTTP request; negative values are rejected.
- `MaxOutputTokens` limits output across OpenAI continuation passes and is sent as Anthropic's `max_tokens`. OpenAI's context planning uses estimates, so these estimates are not exact input-token accounting.
- Tool schema normalization is handled in `IronPaw.AgentCore.Tools`.
