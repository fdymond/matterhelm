---
name: merge-gates
description: Runs MatterHelm's full merge gates, coverage, audit, relevant acceptance demos, protocol-parity checks, and packaging smoke. Use it before reporting implementation work complete or preparing a release.
when_to_use: Use after a code, configuration, packaging, or protocol change and before integration.
---

# Run MatterHelm merge gates

Everything here runs headless: no window, tray icon, console window, browser, or Explorer may
appear on the maintainer's desktop. Never pass `--visible` or set `MATTERHELM_VISIBLE_UI=1`
unless a human asks to watch.

Run from the repository root. Stop on a failing check, preserve its verbatim output, fix the
cause if it is in scope, and restart the affected sequence.

## 1. Bridge gates

Use the locked dependency graph, then run the merge gate, coverage thresholds, and dependency
audit:

```powershell
Push-Location bridge
npm ci
npm run verify
npm run coverage
npm audit
Pop-Location
```

## 2. App gates

```powershell
dotnet build app/MatterHelm/MatterHelm.csproj -c Release
dotnet test app/MatterHelm.Tests/MatterHelm.Tests.csproj -c Release
```

When `dotnet` is not on `PATH`, resolve it locally; do not put a machine-specific path into
repo files or reports as a requirement.

## 3. Acceptance demos

Run the Release executable with every flag relevant to the change (headless by default; a
check reported as `SKIPPED-HEADLESS` needs a human-observed `--visible` run before release):

- UI/HUD: `--demo-overlay`
- sidecar lifecycle or authentication: `--demo-sidecar-chaos`
- pairing UI: `--demo-pairing-window`
- app/bridge wiring, protocol, actions, or configuration: `--demo-wired`
- onboarding: `--demo-welcome-window`
- settings UI or configuration: `--demo-settings-window`

Record each demo's objective PASS/FAIL output. Hardware-in-the-loop steps remain human-only.

## 4. Protocol parity

If the wire contract or BLUEPRINT section 2.3 changed, verify all of these together:

- `bridge/src/ipc/protocol.ts` and `app/MatterHelm/Sidecar/Protocol.cs` describe identical
  frames, required fields, bounds, enum values, version, and `hello` handshake.
- Both bridge schema tests and app protocol tests cover valid, invalid, and boundary frames.
- `app/MatterHelm/DemoAssets/StubSidecar.js` and `app/MatterHelm/Demos/WiredDemo.cs` use the
  same contract.
- A breaking change bumps `protocol` in `hello` and has an ADR.

## 5. Packaging smoke

Packaging requires the exact Node.js version from `.node-version` on `PATH`. It accepts
`-VerifyNodeChecksum`, which is mandatory in CI.

After the gates pass, run:

```powershell
./build.ps1 -SkipTests
```

Do not append `2>&1` under PowerShell 5.1; native stderr can be promoted to a terminating
error. The SEA step must fail loudly. Use `-AllowNodeLayoutFallback` only when the maintainer
explicitly accepts the documented fallback.

## Report

Report commands and verbatim outputs, failures or skips with reasons, demo flags exercised,
protocol-parity findings, packaging layout, and any human-only evidence still needed. Do not
replace output with test-count claims; CI is the durable source for current totals.
