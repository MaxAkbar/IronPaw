# Tutorial: Using LLM Providers

IronPaw routes requests to different LLM backends through a pluggable provider system. This tutorial covers setting up and using each supported provider.

## Provider overview

| Provider | Backend | API key required | Default model |
|----------|---------|-----------------|---------------|
| `local` | Built-in rule engine | No | `rule-based-v1` |
| `openai` | OpenAI API | Yes | `gpt-4o-mini` |
| `anthropic` | Anthropic API | Yes | `claude-3-5-haiku-latest` |
| `ollama` | Ollama (local) | No | `llama3.2` |
| `lmstudio` | LM Studio (local) | No | `local-model` |

## Local provider (no setup needed)

The `local` provider is a deterministic rule-based engine. It does not call any external API. Use it for:
- Exploring the framework without credentials
- Testing the tool pipeline
- Offline development

```powershell
dotnet run --project src/IronPaw.Cli -- --provider local --prompt "Write a hello world to hello.txt"
```

The local provider recognizes patterns like "write/create" + a filename and emits the appropriate `write_file` tool call.

## OpenAI

### Setup

```powershell
$env:OPENAI_API_KEY = "sk-..."
```

Or, if you start interactive mode without the key set, the CLI will prompt you to enter it (masked input).

### Usage

```powershell
# Default model (gpt-4o-mini)
dotnet run --project src/IronPaw.Cli -- --provider openai --prompt "Explain what IronPaw does"

# Specific model
dotnet run --project src/IronPaw.Cli -- --provider openai --model gpt-4o --prompt "Explain what IronPaw does"
```

### Compatible models

Any model available through the OpenAI `/v1/chat/completions` endpoint with streaming and tool-call support works. Examples:
- `gpt-4o-mini` (default, fast and cheap)
- `gpt-4o` (more capable)
- `gpt-4-turbo`
- `o1-mini`, `o1-preview` (reasoning models — may have limited tool support)

## Anthropic

### Setup

```powershell
$env:ANTHROPIC_API_KEY = "sk-ant-..."
```

### Usage

```powershell
# Default model
dotnet run --project src/IronPaw.Cli -- --provider anthropic --prompt "List the files in this directory"

# Specific model
dotnet run --project src/IronPaw.Cli -- --provider anthropic --model claude-sonnet-4-20250514 --prompt "List the files"
```

### Compatible models

Any model on the Anthropic `/v1/messages` streaming API:
- `claude-3-5-haiku-latest` (default, fast)
- `claude-sonnet-4-20250514` (balanced)
- `claude-opus-4-20250514` (most capable)

### Note on tool format

Anthropic uses a different message format for tool results (sent as user messages with `tool_result` content blocks). IronPaw handles this automatically — you do not need to configure anything.

## Ollama (local models)

[Ollama](https://ollama.com/) runs open-source models locally. IronPaw connects to it using the OpenAI-compatible API.

### Setup

1. Install and start Ollama
2. Pull a model: `ollama pull llama3.2`
3. Ollama serves on `http://localhost:11434` by default

```powershell
# Optional: customize the base URL
$env:OLLAMA_BASE_URL = "http://localhost:11434"
```

### Usage

```powershell
dotnet run --project src/IronPaw.Cli -- --provider ollama --model llama3.2 --prompt "Hello"
```

### Tool support

Tool calling support depends on the model. Models like `llama3.2` and `mistral` support tool calls. Smaller or older models may not. If tool calls are not supported, the agent will still respond with text but won't be able to use tools.

## LM Studio (local models)

[LM Studio](https://lmstudio.ai/) provides a GUI for running local models with an OpenAI-compatible API.

### Setup

1. Install LM Studio and load a model
2. Start the local server (default: `http://localhost:1234`)

```powershell
# Optional: customize the base URL
$env:LMSTUDIO_BASE_URL = "http://localhost:1234"
```

### Usage

```powershell
dotnet run --project src/IronPaw.Cli -- --provider lmstudio --prompt "Summarize the project"
```

## Switching providers at runtime

In interactive mode, use the `/provider` command:

```
> /provider
```

A selection menu lets you pick a new provider without restarting. Use `/model` to change the model:

```
> /model gpt-4o
```

## Environment variables reference

| Variable | Default | Description |
|----------|---------|-------------|
| `OPENAI_API_KEY` | — | API key for OpenAI |
| `ANTHROPIC_API_KEY` | — | API key for Anthropic |
| `OLLAMA_BASE_URL` | `http://localhost:11434` | Ollama server URL |
| `OLLAMA_API_KEY` | `ollama` | Ollama API key (usually not needed) |
| `LMSTUDIO_BASE_URL` | `http://localhost:1234` | LM Studio server URL |
| `LMSTUDIO_API_KEY` | `lm-studio` | LM Studio API key (usually not needed) |

## Choosing a provider

| Scenario | Recommended provider |
|----------|---------------------|
| Quick local testing, no API key | `local` |
| Production quality, cloud-hosted | `openai` or `anthropic` |
| Privacy-sensitive, runs on your machine | `ollama` or `lmstudio` |
| Offline development | `local` or `ollama` |
