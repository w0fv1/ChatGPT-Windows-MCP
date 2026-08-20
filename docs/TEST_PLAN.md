# Test plan

## Build

- [ ] `dotnet restore` succeeds with .NET 10 SDK.
- [ ] `dotnet build -c Release` succeeds without warnings that affect execution.
- [ ] `scripts\publish.ps1` produces `dist\ChatGPT-Windows-MCP.exe`.
- [ ] The published EXE starts on a clean Windows 11 x64 VM without a preinstalled .NET runtime.

## First run

- [ ] `config.json` is created beside the EXE.
- [ ] `tools`, `profiles`, `logs`, and `runtime` directories are created.
- [ ] GUI warns that the Runtime API Key is plaintext.
- [ ] Opening config/log directories works.

## Dependencies

- [ ] If `uv` exists, the launcher detects it.
- [ ] If `uv` is absent and WinGet exists, dependency install completes.
- [ ] If WinGet is absent, the launcher gives a clear error.
- [ ] `tunnel-client` downloads from `openai/tunnel-client` into `tools\tunnel-client`.
- [ ] `cloudflared.exe` is copied beside `tunnel-client.exe`.

## Windows-MCP

- [ ] With port 8000 free, launcher starts Windows-MCP.
- [ ] Windows-MCP binds only to `127.0.0.1`.
- [ ] Default startup does not pass `--exclude-tools`.
- [ ] PowerShell and Registry are exposed when provided by the selected Windows-MCP version.
- [ ] If port 8000 is already listening and reuse is enabled, launcher does not start a second MCP.
- [ ] If port 8000 is occupied and reuse is disabled, launcher fails clearly.

## Tunnel

- [ ] Profile is created inside `profiles\`.
- [ ] `CONTROL_PLANE_API_KEY` is passed via child-process environment, not command line.
- [ ] `doctor --explain` succeeds for a valid Tunnel ID/key.
- [ ] `run` stays alive and reports the tunnel started.
- [ ] ChatGPT connector discovery succeeds while the launcher is running.
- [ ] Stopping the launcher kills only child processes it owns.

## Failure cases

- [ ] Bad Tunnel ID.
- [ ] Bad/expired Runtime API Key.
- [ ] No Internet.
- [ ] GitHub unavailable.
- [ ] OpenAI control plane unavailable.
- [ ] Windows-MCP package install failure.
- [ ] tunnel-client exits immediately.
- [ ] `config.json` directory is read-only.

## Shutdown lifecycle

- [x] Closing the WPF window while Windows-MCP is listening exits without freezing the dispatcher.
- [x] Graceful close releases the owned MCP port and terminates the listener process.
- [x] Force-terminating the launcher releases the owned MCP port through Windows Job Object cleanup.
- [x] Shutdown has a bounded UI wait and falls back to kernel/process-tree cleanup.