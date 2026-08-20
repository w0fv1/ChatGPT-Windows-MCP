# Security Policy

ChatGPT Windows MCP can expose powerful Windows automation capabilities through an MCP connector. Treat the launcher, the OpenAI account, the configured Tunnel, and the local machine as a single security boundary.

## Supported versions

Security fixes are provided for the latest published release and the current `main` branch.

## Important security characteristics

- Windows-MCP binds to loopback (`127.0.0.1`) by default.
- The OpenAI Secure MCP Tunnel is outbound from the local machine.
- This project intentionally runs Windows-MCP in full-tool mode when the upstream package provides those tools.
- The Runtime API Key is currently stored in plaintext in the local `config.json` by design. That file is excluded from Git and must never be shared.
- Tunnel credentials are passed to child processes through environment variables rather than command-line arguments.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting for this repository when available. If that is unavailable, contact the repository owner privately through GitHub rather than opening a public issue.

Include:

- affected version/commit;
- reproduction steps;
- security impact;
- whether credentials or arbitrary command execution are involved;
- suggested mitigation, if known.

Do not include real API keys, tokens, private Tunnel IDs, personal data, or exploit payloads that could harm third parties.
