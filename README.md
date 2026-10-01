# ChatGPT Windows MCP

[![Windows build](https://github.com/w0fv1/ChatGPT-Windows-MCP/actions/workflows/build.yml/badge.svg)](https://github.com/w0fv1/ChatGPT-Windows-MCP/actions/workflows/build.yml)
[![CodeQL](https://github.com/w0fv1/ChatGPT-Windows-MCP/actions/workflows/codeql.yml/badge.svg)](https://github.com/w0fv1/ChatGPT-Windows-MCP/actions/workflows/codeql.yml)
[![Release](https://img.shields.io/github/v/release/w0fv1/ChatGPT-Windows-MCP)](https://github.com/w0fv1/ChatGPT-Windows-MCP/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A focused Windows desktop launcher for **Windows-MCP + OpenAI Secure MCP Tunnel**. Its setup wizard follows Tunnel ID → Runtime API Key → network proxy → ChatGPT plugin.

> **Unofficial community project.** This repository is not affiliated with or endorsed by OpenAI. "ChatGPT" and "OpenAI" are trademarks of their respective owners.

**中文文档:** [docs/README.zh-CN.md](docs/README.zh-CN.md)

## Why this exists

Running a local Windows MCP server is only part of the job. A usable desktop app also needs to manage the local server, Secure MCP Tunnel, dependency setup, connection readiness, failures, and diagnostics without forcing users to understand every implementation detail.

The normal flow is intentionally small:

```text
Create/open an OpenAI Tunnel
        ↓
paste Tunnel ID
        ↓
create/paste Runtime API Key
        ↓
Start
        ↓
Windows-MCP + Secure MCP Tunnel
        ↓
ChatGPT connector can operate the local Windows machine
```

## Features

- WPF/XAML Windows desktop UI.
- Four-step setup wizard: **Tunnel ID → Runtime API Key → network proxy → ChatGPT plugin**.
- Name the plugin and copy a generated Plugin Creator prompt with the configured Tunnel ID, without including the Runtime API Key.
- One-click dependency preparation and startup.
- Starts and supervises Windows-MCP locally.
- Creates and runs the official OpenAI `tunnel-client` profile.
- Confirms real Tunnel readiness from successful control-plane metadata retrieval.
- Automatic Windows/system proxy discovery with explicit Control Plane proxy override.
- Advanced diagnostics and process logs without cluttering the primary workflow.
- Portable, self-contained Windows x64 release.
- CI build, CodeQL analysis, Dependabot, and tag-driven GitHub Releases.

## Requirements

- Windows 10/11 x64.
- Internet access.
- An OpenAI organization/account with access to Tunnels and ChatGPT MCP connectors.
- A Tunnel ID.
- A Runtime API Key with the required Tunnel permissions.
- Portable Windows x64 releases embed uv/uvx 0.12.21, Python 3.13.14, Windows-MCP 0.8.5 with all 92 pinned dependencies, and Tunnel client v0.0.15 with cloudflared. Default configuration extracts and verifies these locally; no WinGet, system Python or dependency download is required. OpenAI connectivity still requires Internet access. Custom Python/package/Tunnel versions retain the online installation path. Development builds without embedded payloads retain the existing download fallback.

## Quick start

1. Download the latest `ChatGPT-Windows-MCP-win-x64.zip` from [Releases](https://github.com/w0fv1/ChatGPT-Windows-MCP/releases).
2. Extract it and run `ChatGPT-Windows-MCP.exe`.
3. Click **创建链接**.
4. In Step 1, click **打开 Tunnel 创建页面** or **复制链接**, create a Tunnel and paste the `tunnel_...` value.
5. In Step 2, click the Runtime API Key page button or copy its link, then create and paste a Runtime API Key. Pages open only after an explicit button click; wizard navigation and startup never open a browser automatically.
6. In Step 3, set the optional HTTP(S) proxy, then continue to the plugin page.
7. Entering Step 4 automatically prepares dependencies and starts Windows-MCP and Tunnel using the saved Tunnel ID, API Key and proxy. Name the plugin while the service starts; no return to home is required.
   Preparation shows the current action and an inline percentage on the right on both the home and plugin pages. Percentages reflect completed preparation stages, rather than elapsed time or total download bytes, and reach 100% only after startup succeeds.
8. Click **创建 MCP 应用**. Choose Add → Create MCP app in ChatGPT, set Connection to Tunnel and paste the copied Tunnel ID. Alternatively, copy the AI creation prompt directly; the connection detail URL is optional. Paste the resulting connection detail URL into the launcher, then click **复制创建提示词** and send it to **@Plugin Creator** in ChatGPT Work. **完成配置** returns home and keeps services running. The home page's **配置插件** opens this step directly. Keep the launcher running while creating and using the plugin.

The plugin page requires a registered connection detail URL or `plugin_asdk_app...` ID before enabling prompt copying. It saves the extracted ID and clears it when the Tunnel changes. The prompt asks Plugin Creator to bind that connection, update an existing matching or placeholder plugin, and verify tools from that connection. It forbids placeholder creation and unsupported success claims. The launcher checks the link format; it cannot verify the account's Tunnel mapping. See the [official Plugin Creator connection instructions](https://developers.openai.com/plugins/build/plugins) and [Tunnel guide](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels#connect-from-chatgpt).

Register Tunnel automatically starts stopped services or retries a failed startup, then checks the configured Tunnel, fresh readiness and local MCP initialization and tool discovery before opening ChatGPT. Startup also checks MCP initialization and tool discovery; Connection Diagnostics can repeat these checks while running without stopping services. Going back to edit the proxy stops services first, and proceeding to Step 4 restarts them with the saved settings. These checks never execute tools and do not establish ChatGPT workspace permissions or prove the registration succeeded.

The proxy is used for tunnel-client downloads, WinGet/uv network access, Windows-MCP package resolution, and the OpenAI Tunnel connection. When left blank, environment variables and the Windows system proxy can be detected automatically. Other runtime settings, logs, and diagnostics are under **高级选项**.

## Architecture

`profiles/` and `runtime/` are created only when needed for Tunnel configuration and health discovery; empty working directories are cleaned without deleting existing files. Daily logs are flushed on each entry and include session details, command starts, PIDs, separate stdout/stderr, exit codes, durations, health changes, and complete redacted exception diagnostics. Python runs with unbuffered output.

```text
ChatGPT
   │ connector / MCP
   ▼
OpenAI control plane
   │ outbound Secure MCP Tunnel
   ▼
tunnel-client.exe
   │ loopback HTTP
   ▼
Windows-MCP
   │
   ▼
Windows desktop / apps / exposed Windows-MCP tools
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for details.

## Process cleanup

The launcher owns the processes it starts. Normal shutdown is asynchronous and bounded so the WPF UI does not freeze. On Windows, owned `uvx` / Windows-MCP and `tunnel-client` processes are placed in a Job Object with kill-on-close semantics, which also cleans them up if the launcher itself is force-terminated.
## Security model

This software controls a local Windows MCP endpoint and can expose powerful automation tools. Read [SECURITY.md](SECURITY.md) before use.

Important current behavior:

- Windows-MCP is bound to `127.0.0.1` by default.
- The Secure MCP Tunnel uses outbound connectivity.
- Full-tool mode is intentionally enabled when the installed Windows-MCP package provides those tools.
- The Runtime API Key is currently stored as plaintext in local `config.json` for portable operation.
- `config.json`, profiles, logs, downloaded tools, runtime files, build outputs, and local backups are ignored by Git.

Never upload a real `config.json` or post unsanitized logs publicly.

## Advanced configuration

A generated local `config.json` can contain:

```json
{
  "TunnelId": "tunnel_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
  "RuntimeApiKey": "sk-REPLACE_WITH_RUNTIME_API_KEY",
  "McpPort": 8000,
  "ProfileName": "windows-mcp",
  "WindowsMcpSpec": "windows-mcp",
  "PythonVersion": "3.13",
  "ReuseExistingMcp": false,
  "AutoDownloadTunnelClient": true,
  "TunnelClientVersion": "latest",
  "AutoDetectSystemProxy": true,
  "ControlPlaneHttpProxy": ""
}
```

This file is local-only and intentionally excluded from Git.

## Development

```powershell
git clone https://github.com/w0fv1/ChatGPT-Windows-MCP.git
cd ChatGPT-Windows-MCP
./scripts/dev-run.ps1
```

Release build:

```powershell
dotnet build ./src/ChatGPTWindowsMcp/ChatGPTWindowsMcp.csproj -c Release -warnaserror
./scripts/publish.ps1
```

Output:

```text
dist/
  ChatGPT-Windows-MCP.exe
  config.example.json
  README.md
```

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Bugs and features have structured GitHub issue forms. Large changes should be discussed before implementation.

## Project docs

- [Architecture](docs/ARCHITECTURE.md)
- [Test plan](docs/TEST_PLAN.md)
- [Roadmap](ROADMAP.md)
- [Changelog](CHANGELOG.md)
- [Support](SUPPORT.md)
- [Security policy](SECURITY.md)

## License

MIT. See [LICENSE](LICENSE).
