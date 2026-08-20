# Architecture

```text
ChatGPT
   │
   │ Custom connector / MCP
   ▼
OpenAI control plane
   │
   │ outbound Secure MCP Tunnel
   ▼
tunnel-client.exe
   │
   │ http://127.0.0.1:<port>/mcp
   ▼
Windows-MCP
   │
   ▼
Windows UI / keyboard / mouse / apps / optional PowerShell / Registry
```

## Local directories

The launcher is intentionally portable:

- `config.json` — user configuration and **plaintext Runtime API Key**
- `tools/tunnel-client/` — downloaded OpenAI tunnel-client files
- `profiles/` — local tunnel-client YAML profiles
- `logs/` — launcher/process logs
- `runtime/` — transient runtime files

Nothing is stored in Windows Credential Manager.

## Startup sequence

1. Load and validate `config.json`.
2. Locate `uv`.
3. Download `openai/tunnel-client` if absent.
4. Reuse an already-listening local MCP endpoint if configured; otherwise start Windows-MCP with `uvx`.
5. Wait for the MCP TCP port.
6. Regenerate the tunnel profile with the selected Tunnel ID.
7. Run `tunnel-client doctor`.
8. Start `tunnel-client run`.
9. Mark the launcher ready and optionally open ChatGPT connector settings.

## Security defaults

This build intentionally uses full tool mode and does not configure an excluded-tool list. Because the Runtime API Key is stored in plaintext per project requirements, `config.json` is ignored by Git and must not be distributed.

## Tool exposure

This build starts Windows-MCP without `--exclude-tools`, so the complete tool set exposed by the selected Windows-MCP package is available through the connector.

## Process lifecycle and shutdown

Processes started by the launcher are assigned to a Windows Job Object configured with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. This gives the launcher two shutdown layers:

1. **Graceful shutdown** — the UI requests an orderly stop, kills owned process trees, and verifies the owned MCP listener is gone.
2. **Kernel fallback** — if the launcher is force-terminated, closing the Job Object handle causes Windows to terminate assigned `uvx` / `windows-mcp` and `tunnel-client` process trees.

WPF window shutdown has a hard three-second graceful deadline. Potentially blocking process-tree operations execute off the dispatcher thread so closing the app cannot freeze the UI indefinitely.