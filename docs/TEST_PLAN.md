# Test plan

## Build

- [ ] `dotnet restore` succeeds with .NET 10 SDK.
- [ ] `dotnet build -c Release` succeeds without warnings that affect execution.
- [ ] `scripts\publish.ps1` produces `dist\ChatGPT-Windows-MCP.exe`.
- [ ] The published EXE starts on a clean Windows 11 x64 VM without a preinstalled .NET runtime.

## First run

- [ ] `config.json` is created beside the EXE.
- [ ] `tools` and `logs` are created; `profiles` and `runtime` are created only when needed, and empty working directories are cleaned without deleting files.
- [ ] GUI warns that the Runtime API Key is plaintext.
- [ ] Opening config/log directories works.

## Dependencies

- [ ] The published EXE embeds uv and uvx; on a clean directory with empty PATH, no WinGet and blocked network, both extract and execute from `tools/uv/bundled`. Embedded file hashes and licenses are checked.
- [ ] Development builds without bundled uv can detect existing uv or use the fallback below.
- [ ] If `uv` is absent and WinGet exists, dependency install completes.
- [ ] If WinGet is absent, the official uv archive installs into `tools/uv` and startup continues without restarting.
- [ ] If WinGet fails, times out after 90 seconds, or completes without a discoverable uv/uvx, the archive fallback runs.
- [ ] After WinGet updates the user/machine PATH, uv is detected immediately in the same launcher process.
- [ ] The uv fallback honors the configured proxy; failed checksum/download or cancellation publishes no partial installation.
- [ ] Wizard entry, step navigation and startup never open a browser automatically, including with old AutoOpenChatGptConnectors config values. Explicit page-opening buttons still work; each copy-link button copies the corresponding URL and shows success feedback.
- [ ] `tunnel-client` downloads from `openai/tunnel-client` into `tools\tunnel-client`.
- [ ] `cloudflared.exe` is copied beside `tunnel-client.exe`.

## Proxy

- [ ] The dedicated third-step proxy page accepts an empty value and rejects values without `http://` or `https://`; its Next button leads to plugin configuration.
- [ ] A configured proxy is used by the tunnel-client HTTP download and WinGet uv installation.
- [ ] uv/uvx and tunnel-client doctor/run receive the proxy through their environment.
- [ ] With the field empty, enabled Windows system proxy detection still accepts native `host:port` values.

## Windows-MCP

- [ ] With port 8000 free, launcher starts Windows-MCP.
- [ ] Windows-MCP binds only to `127.0.0.1`.
- [ ] Default startup does not pass `--exclude-tools`.
- [ ] PowerShell and Registry are exposed when provided by the selected Windows-MCP version.
- [ ] If port 8000 is already listening and reuse is enabled, launcher does not start a second MCP.
- [ ] If port 8000 is occupied and reuse is disabled, launcher fails clearly.

## Tunnel

- [ ] The wizard proceeds Tunnel → API Key → proxy → ChatGPT plugin. The fourth step asks for the plugin name and permits copying a dynamic creation prompt; no guide card appears on the home page.
- [ ] Entering the plugin step automatically prepares dependencies and starts MCP and Tunnel with the saved settings. A short inline status reports progress; startup does not open a browser or navigate home. No separate startup button, startup log or prompt preview is shown.
- [ ] Open ChatGPT opens `https://chatgpt.com/` only after a click; Copy ChatGPT Link copies `https://chatgpt.com/`. The obsolete auto-open option is removed.
- [ ] Both home and plugin pages show the actual current action with x% on the right of the same line; there is no loading icon or progress bar. Percentages advance monotonically through actual preparation stages; success reaches 100%, failure retains partial progress. Old progress callbacks cannot alter a finished or newer attempt.
- [ ] Clipboard failures show inline feedback for prompt and ChatGPT link copying.
- [ ] Changing the plugin name or connection refreshes the prompt; blank names, missing connections, unrelated plugin URLs and invalid links disable copying. Prompts contain no Runtime API Key and preserve the configured Tunnel ID and extracted app ID.
- [ ] Copied creation prompts require binding the supplied app ID, updating a same-name placeholder when appropriate, and validating tools through that connection; placeholders and unrelated tools must not count as completion.
- [ ] Connection IDs persist across reopening the plugin page and restart, and clear when the Tunnel changes. The home page's Configure Plugin button opens step 4 directly without stopping services or offering proxy navigation.
- [ ] Register Tunnel automatically starts stopped services or retries failed startup before checking the active Tunnel ID, fresh readiness and local initialize/tools/list. Failed startup never opens ChatGPT. Concurrent clicks cannot launch duplicate services; operation controls are disabled during startup.
- [ ] Startup rejects an open port serving non-MCP, protocol errors, invalid tool lists or empty tools. Discovery supports JSON and SSE responses, carries the diagnostic session and handles pagination, then closes only its own session. No tools/call is made.
- [ ] Connection Diagnostics can inspect a running service without regenerating its Profile or stopping it. Results distinguish local checks from ChatGPT registration and workspace permissions.
- [ ] Returning from the plugin step to the proxy step preserves entered values and stops services before proxy edits. Returning to the plugin step restarts with the saved proxy. Completing configuration returns home with services still running.
- [ ] Daily logs and the home startup log receive progress immediately; files include commands, PIDs, streams, exit status, durations, full exceptions and redacted secrets.
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

- [ ] Preparation text distinguishes embedded uv extraction, Tunnel release lookup/download/unpacking, Python download, dependency download/build/install, MCP startup, tool discovery and Tunnel readiness.
- [ ] Portable publishing prepares and validates the pinned official uv payload before embedding it; missing payload fails the portable build, and the EXE alone contains it.

## 完整内置环境验证

运行 `dotnet run --project tests/RuntimeBundleSmoke/RuntimeBundleSmoke.csproj -c Release`：从 EXE 资源释放到全新目录，修复被损坏的启动脚本，在空 PATH、不可连接的下载代理下启动 Python/Windows-MCP，通过 MCP initialize 和 tools/list 验证工具发现，最后运行内置 Tunnel 客户端的版本检查。仅使用临时服务和空闲本地端口，不调用 Windows 操作工具，也不启动真实 Tunnel 连接。构建前需运行两个 prepare-bundled 脚本。
