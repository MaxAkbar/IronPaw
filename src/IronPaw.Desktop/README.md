# IronPaw.Desktop

`IronPaw.Desktop` is the WPF UI host for IronPaw.

## Purpose

- Provide an interactive Windows interface over the headless agent core.
- Display real-time event stream from orchestrator runs.
- Support provider/model selection and session-based execution.

## Main files

- `MainWindow.xaml`
  - command-center style interface:
    - session key input
    - provider/model controls
    - prompt input
    - assistant output
    - event stream list
- `MainWindow.xaml.cs`
  - runtime composition of orchestrator stack
  - provider routing and key resolution
  - add-in skill loading and diagnostics
- `Properties/launchSettings.json`
  - local debug profile and environment variables

## Run

```powershell
dotnet run --project src/IronPaw.Desktop --launch-profile IronPaw.Desktop
```

## Runtime configuration

- Providers:
  - `local`
  - `openai`
  - `ollama`
  - `lmstudio`
  - `anthropic`
- API keys read from environment variables:
  - `OPENAI_API_KEY`
  - `ANTHROPIC_API_KEY`
  - `OLLAMA_BASE_URL` (default `http://localhost:11434`)
  - `OLLAMA_API_KEY` (optional; default `ollama`)
  - `LMSTUDIO_BASE_URL` (default `http://localhost:1234`)
  - `LMSTUDIO_API_KEY` (optional; default `lm-studio`)

## Add-in skill loading

On startup, the desktop host loads skills from:
- `./addins/skills`
- `%LOCALAPPDATA%/IronPaw/skills`

Skill load diagnostics are written to the event panel.

## Notes

- The Desktop project is a host/composition layer; core runtime behavior remains in `IronPaw.AgentCore`.
