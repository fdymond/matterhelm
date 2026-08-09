# Changelog

All notable changes to MatterHelm are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/) from
0.1.0 onward. The Matter *protocol* version (`bridge/src/ipc/protocol.ts`) is
versioned independently of the app/bridge SemVer and only ever bumped with an
ADR (see `docs/ENGINEERING-STANDARDS.md`).

## [Unreleased]

Nothing yet.

## [0.1.0] — 2026-08-09

First tagged release: everything from Sprints 0–7 plus the Sprint 3
hardening/ship stories (S3-1 packaging, S3-2 factory reset + user guide,
S3-3 release gate). Feature-complete and paired against real Google Home
hardware. High-level summary of what's shipped (see `README.md` "Status"
for the authoritative, up-to-date version):

### Packaging & release (Sprint 3)

- `build.ps1` produces a single self-contained `dist\` folder: Node SEA
  sidecar (`sidecar\bridge.exe`, with an automatic `node.exe + bridge.cjs`
  fallback layout) plus a self-contained single-file tray exe — no Node.js
  or .NET required on the target machine. Sizes: tray 111 MB, sidecar
  88 MB, total ~200 MB.
- Factory-reset flow (tray menu, deletes Matter storage after a spelled-out
  confirmation) and `docs/user-guide.md`; scripted hardware E2E checklist
  in `docs/e2e-log.md`.
- Tag-triggered release workflow (verify + tests + dist build + GitHub
  Release zip).
- Release perf/budget gate (ADR-007 method: private bytes, settled ≥ 2 min
  idle, packaged dist under test) — all budgets met:

  | Metric | Budget | Measured (2026-08-09) |
  |---|---|---|
  | Tray idle private bytes | ≤ 32 MB | 15.5 MB |
  | Sidecar processes | 1 | 1 (SEA `bridge.exe`) |
  | Sidecar idle private bytes | ≤ 120 MB | 93 MB |
  | Idle CPU (both) | < 0.5 % | tray 0.08 %, sidecar 0 % |
  | Sidecar cold start → "bridge started" | < 3 s | 2.04 s |
  | UI churn probe (`--probe-resources`) | PASS | PASS (all 4 bounds) |
  | `npm audit` | clean | 0 vulnerabilities |

  Bonus observation: after a hard tray-app kill the packaged sidecar exits
  via its stdin tether in 0.6 s (backlog watch item P-4 did not reproduce
  with the SEA layout).

### Bridge (`bridge/`)

- Full Matter device model behind the `matter/adapter.ts` boundary: an
  Aggregator exposing a Speaker (volume/mute) and momentary-switch endpoints
  for media/power actions, commissioned via QR against a real Nest hub.
- Protocol v2 (`bridge/src/ipc/protocol.ts`) with custom-command support
  (`HTPC_BRIDGE_ENDPOINTS`-driven dynamic endpoint construction), a pure
  `mapping/` layer translating cluster writes to IPC actions and back.
- Structured diagnostics: pino logging, session/subscription observability,
  per-action timing with correlation ids.
- esbuild single-file bundle (one Node process, ~0.9 s cold start).
- 322 tests; pure modules (`mapping/`, `ipc/protocol.ts`, `config.ts`) gated
  at ≥ 90% line coverage in CI.

### Tray application (`app/MatterHelm`)

- Sidecar supervisor (spawn, token auth, restart backoff, stdin tether) and
  loopback IPC server; `ActionExecutor` for CoreAudio volume/mute
  (with change observation), SMTC media keys, configurable key-sequence
  chords, app launch, and display power.
- Click-through, non-activating overlay HUD (incoming command + executed
  action, volume fill bar, update-in-place, DPI-safe at 200%).
- Settings window: categorized left nav, search, staged edits with
  validation, full custom-command CRUD including key-sequence capture, dark
  mode.
- Local metrics (action/ack/restart/reconnect counters) and a
  privacy-hardened diagnostics export (Settings → Advanced → Export
  diagnostics): scrubbed logs/manifest as a local-only zip, no automatic
  upload.
- 402 tests; measured resource budgets enforced (ADR-007: single sidecar
  process, WinForms-baseline memory, idle-churn probes).
- Renamed to **MatterHelm** (from the original working name) with an atomic
  `%APPDATA%` migration that preserves the paired Matter fabric.

### Process / governance

- Seven sprints delivered and adversarially reviewed (five dedicated review
  passes plus per-sprint review stories) — see `BACKLOG.md`.
- Open-source-grade project scaffolding: license, contributor/security/
  conduct docs, issue and PR templates, release automation (this change).

### Known gaps

- Continued hardware end-to-end validation, logged in `docs/e2e-log.md`
  (the full scripted checklist has not yet been executed against the
  packaged dist — needs the human, a Nest hub, and a phone).
