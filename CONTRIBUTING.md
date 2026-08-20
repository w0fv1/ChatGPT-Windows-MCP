# Contributing

Thanks for helping improve ChatGPT Windows MCP.

## Development setup

Requirements:

- Windows 10/11 x64
- .NET 10 SDK
- PowerShell 7 recommended

Clone the repository, then run:

```powershell
./scripts/dev-run.ps1
```

Build a release locally with:

```powershell
dotnet build ./src/ChatGPTWindowsMcp/ChatGPTWindowsMcp.csproj -c Release
./scripts/publish.ps1
```

## Pull requests

1. Create a focused branch from `main`.
2. Keep unrelated refactors out of the same PR.
3. Update documentation when behavior or configuration changes.
4. Run the Release build before opening a PR.
5. Never commit `config.json`, Runtime API Keys, Tunnel IDs from private environments, logs, profiles, or downloaded tools.

For UI changes, include before/after screenshots when practical.

## Coding conventions

- Nullable reference types stay enabled.
- Prefer explicit error messages over silent fallback.
- Keep the normal user flow simple; implementation details belong in Advanced Options.
- Network and process failures must be surfaced in logs without exposing secrets.
- New dependencies should have a clear maintenance and security justification.

## Reporting security issues

Do not open a public issue for a vulnerability involving credential exposure, privilege escalation, unsafe command execution, or connector authorization. Follow [SECURITY.md](SECURITY.md).
