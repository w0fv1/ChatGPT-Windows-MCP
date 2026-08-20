# ChatGPT Windows MCP

[![Windows build](https://github.com/w0fv1/ChatGPT-Windows-MCP/actions/workflows/build.yml/badge.svg)](https://github.com/w0fv1/ChatGPT-Windows-MCP/actions/workflows/build.yml)
[![CodeQL](https://github.com/w0fv1/ChatGPT-Windows-MCP/actions/workflows/codeql.yml/badge.svg)](https://github.com/w0fv1/ChatGPT-Windows-MCP/actions/workflows/codeql.yml)
[![Release](https://img.shields.io/github/v/release/w0fv1/ChatGPT-Windows-MCP)](https://github.com/w0fv1/ChatGPT-Windows-MCP/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A focused Windows desktop launcher for **Windows-MCP + OpenAI Secure MCP Tunnel**. It turns setup into a two-step wizard and keeps local process, proxy, diagnostics, and Tunnel details behind an Advanced Options section.

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
- Two-step setup wizard: **Tunnel ID → Runtime API Key**.
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
- WinGet is recommended for automatic `uv` installation.

## Quick start

1. Download the latest `ChatGPT-Windows-MCP-win-x64.zip` from [Releases](https://github.com/w0fv1/ChatGPT-Windows-MCP/releases).
2. Extract it and run `ChatGPT-Windows-MCP.exe`.
3. Click **创建链接**.
4. Step 1 opens the OpenAI Tunnel page. Create a Tunnel and paste the `tunnel_...` value.
5. Step 2 opens the Runtime API Key page. Create a Runtime API Key and paste it.
6. Step 3 optionally accepts an HTTP(S) proxy. Proxy URLs must start with `http://` or `https://`.
7. Click **完成**, then **启动**.
8. Keep the launcher running while using the corresponding ChatGPT connector.

The proxy is used for tunnel-client downloads, WinGet/uv network access, Windows-MCP package resolution, and the OpenAI Tunnel connection. When left blank, environment variables and the Windows system proxy can be detected automatically. Other runtime settings, logs, and diagnostics are under **高级选项**.

## Architecture

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
  "AutoOpenChatGptConnectors": true,
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
