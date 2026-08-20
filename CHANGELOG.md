# Changelog

All notable changes to this project are documented here.

The project follows Semantic Versioning where practical.

## [Unreleased]

### Planned

- Improve credential storage beyond plaintext local configuration.
- Expand automated tests for startup and failure-state handling.
- Improve accessibility and localization.

## [0.1.1] - 2026-08-20

### Added

- Optional proxy step in the setup wizard.
- Tunnel ID and Runtime API Key editing at the top of Advanced Options.

### Changed

- Apply the configured proxy to tunnel-client downloads, WinGet, uv/uvx, Windows-MCP package resolution, and Tunnel connections.
- Require manually entered proxy URLs to start with `http://` or `https://`.

## [0.1.0] - 2026-08-20

### Added

- WPF/XAML Windows launcher.
- Two-step Tunnel ID and Runtime API Key setup wizard.
- One-click dependency preparation and startup.
- Windows-MCP lifecycle management.
- OpenAI Secure MCP Tunnel lifecycle management.
- Control Plane proxy auto-detection and manual override.
- Tunnel readiness detection based on successful metadata retrieval.
- Advanced diagnostics, logs, and runtime settings.
- Portable self-contained Windows x64 publishing.
