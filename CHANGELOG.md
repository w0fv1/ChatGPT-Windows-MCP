# Changelog

All notable changes to this project are documented here.

The project follows Semantic Versioning where practical.

## [Unreleased]

### Planned

- Improve credential storage beyond plaintext local configuration.
- Expand automated tests for startup and failure-state handling.
- Improve accessibility and localization.

## [0.2.1] - 2026-10-03

### Added

- Optional start with Windows switch under Advanced Options → Runtime Settings; changes take effect immediately without administrator rights.
- Offer to enable startup after successful setup completion, skipping failed/cancelled setup and already-enabled startup.
- Automatically connect saved MCP and Tunnel services when launched at Windows sign-in; keep manual launch behavior unchanged.
- Regression coverage for quoted startup paths, registration removal and portable-copy ownership.

## [0.2.0]

### Added

- Bundle uv/uvx, Python, Windows-MCP and all pinned dependencies, Tunnel client and cloudflared in the portable executable.
- Verify and extract the bundled runtime locally with file-level progress and integrity repair.
- Provide manual MCP app creation and copyable Plugin Creator prompts in the setup wizard.
- Check configuration, local MCP tool discovery and Tunnel readiness before completing setup.

### Changed

- Start local services automatically after Tunnel, API key and proxy setup; open websites only when requested.
- Show the current operation with a percentage on the same line, without a spinner or progress bar.
- Add copy buttons for setup links and ChatGPT, and preserve optional registered MCP app IDs.
- Flush detailed process and startup logs immediately while redacting credentials.
- Retain uv installation fallbacks for development builds without bundled assets; no WinGet or system Python is needed by the default portable build.

## [0.1.2]

### Fixed

- Transition unexpected child exits to Faulted instead of displaying a stale connected state.
- Fence health and process observations by run generation; serialize lifecycle operations.
- Clean up owned command processes on cancellation without killing unknown listeners by port.
- Respect fixed tunnel-client versions using versioned installations and cache integrity receipts.
- Stop silently falling back to an old tunnel-client release when latest lookup fails.
- Bound the UI log buffer and tolerate log-file I/O failures.

### Added

- Passive local-port and loopback tunnel readiness monitoring with explicit unknown/degraded states.
- Thirty regression cases and a Windows verification script used by CI and releases.
- Draft-first release publication with downloaded-asset checksum verification.

### Limitations

- Real ChatGPT/Tunnel multi-session behavior remains a separate end-to-end validation task.
- Readiness does not prove ChatGPT tool availability. No automatic tool-call replay is performed.
- Plaintext portable credentials and cache-first latest selection remain unchanged policies.

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
