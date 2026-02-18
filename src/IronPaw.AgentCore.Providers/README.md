# IronPaw.AgentCore.Providers

`IronPaw.AgentCore.Providers` contains LLM client adapters implementing `ILLMClient`.

## Responsibilities

- Convert IronPaw’s internal request/delta model into provider-specific HTTP APIs
- Parse provider responses into normalized deltas:
  - `AssistantTextDelta`
  - `ToolCallDelta`
  - `UsageDelta`
  - `CompletedDelta` / `LlmErrorDelta`
- Route calls to provider clients by name

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
  - `provider -> ILLMClient` dispatch with fallback client

## Environment variables

- `OPENAI_API_KEY`
- `ANTHROPIC_API_KEY`
- `OLLAMA_BASE_URL` (default: `http://localhost:11434`)
- `OLLAMA_API_KEY` (optional; default: `ollama`)
- `LMSTUDIO_BASE_URL` (default: `http://localhost:1234`)
- `LMSTUDIO_API_KEY` (optional; default: `lm-studio`)

## Notes

- Provider parsing currently uses request/response JSON bodies (not true token-by-token SSE streaming).
- Tool schema normalization is handled in `IronPaw.AgentCore.Tools`.
