# Runtime health, version selection and process-safety fixes

This change is based on upstream `600eeaa0f1c0e9ec60ec565ed622231acdb91ddd` and is included in v0.1.2. GitHub Actions is the source of build/test evidence. A successful build is not a Windows desktop or ChatGPT end-to-end test.

## Health states

`RuntimeHealthState` holds a synchronized snapshot with a generation counter. A stopped/replaced run rejects stale probe and process-exit observations. A child exiting during Starting or Running transitions to Faulted. The WPF display reads one snapshot, not independent boolean flags.

`RuntimeManager` serializes lifecycle operations. Every run uses a fresh health-address file and a cancellation lifetime. While running, a five-second passive monitor checks the local TCP listener and the tunnel client's loopback `/readyz`. Numeric loopback HTTP addresses only are accepted; redirects, credentials, remote names and unexpected paths are rejected. Requests do not use system proxies. Missing/unsupported health routes remain unknown rather than falsely green.

TCP readiness is not MCP identity/protocol validation. `/readyz` is not proof of sustained control-plane connectivity or of a particular ChatGPT session's tool availability. No monitor initializes/deletes MCP sessions, lists tools, takes screenshots or replays actions. A surviving child after a sibling fails may remain until Stop, retry, reconfiguration or window close; the UI explicitly reports the fault. Automatic reconnection is not added in this release.

## Version policy

Fixed `TunnelClientVersion` values use `tools/tunnel-client/versions/vX.Y.Z/`. A receipt records the selected upstream release tag and SHA-256 of installed files. Staging is on the destination volume, followed by a directory rename. An incomplete cache is reported, not silently overwritten while it may be running. Another concurrent installer may win the rename only if its completed install validates.

`latest` means reuse a selected cache, not upgrade at every startup. Existing root-layout EXEs remain supported only in latest mode and are logged as version-unverified. A clean latest install resolves a concrete upstream tag and records the selection. Fixed versions never silently reuse the legacy EXE. Failed latest resolution does not fall back to an old hard-coded release.

A local receipt is not a publisher signature, a supply-chain authenticity guarantee or a check of the binary's self-reported version. The selected Windows-MCP package specification is unchanged.

## Cancellation and shutdown

Cancelling a one-shot command requests termination of its owned process tree; cancellation and deadline expiry are distinguished. PATH lookup no longer invokes a synchronously read `where.exe`. Long-lived children retain the existing Job Object safety net, with owned process-tree cleanup as fallback. Port-to-PID killing is removed: an occupied port is not ownership evidence.

The WPF close path cancels the window lifetime, waits for orderly cleanup with a bounded UI wait, and schedules emergency cleanup off the dispatcher if needed. Unknown listeners are reported for manual inspection, never killed by port number.

## Verification

On Windows with .NET 10 SDK:

```powershell
.\scripts\verify-runtime-fix.ps1
.\scripts\publish.ps1
```

The first script builds WPF with warnings as errors and runs the regression console against linked production helper implementations. The 30 cases cover health transitions, stale observations, URL validation, unsupported readiness, cancellation, version normalization/cache integrity, stdout/stderr draining, subprocess cancellation and PATH lookup. They do not cover all RuntimeManager races or the real tunnel service.

Before daily use, test in a separate Windows environment: startup; a child exiting; network loss; Stop and window close during startup; a fixed version change; and sequential read-only calls in ChatGPT A, B, then A again. Verify the actual tool catalog separately. Never use destructive operations to test reconnection.

## Release workflow

Tags must match `Directory.Build.props`. Each release requires Windows build/regressions and packaging. Assets are first uploaded to a draft, downloaded again and SHA-256 compared before publication. Existing releases/tags are not moved or overwritten.

For the explicitly requested v0.1.2 release, merging `docs/releases/v0.1.2.md` into main also triggers this gated workflow. This branch-triggered path refuses every version except 0.1.2; subsequent releases use explicit tags (or a manually dispatched matching tagged ref). A failed upload/verification leaves a draft for inspection, not an incomplete public release.

## Unchanged limitations

ChatGPT-side tool filtering, action permissions and protocol-session recovery are outside this launcher patch. Runtime API Keys remain plaintext in portable config.json as in upstream; never upload this file or unsanitized logs. This release does not establish multi-chat isolation or promise to eliminate Session terminated.
