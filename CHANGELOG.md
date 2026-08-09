# Changelog

All notable changes to MatterHelm are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/) from
0.1.0 onward. The Matter *protocol* version (`bridge/src/ipc/protocol.ts`) is
versioned independently of the app/bridge SemVer and only ever bumped with an
ADR (see `docs/ENGINEERING-STANDARDS.md`).

No versions have been tagged yet — see `[Unreleased]` below. `v0.1.0` is
expected once Sprint 3 packaging (`build.ps1`, single dist folder, factory
reset, user guide — see `BACKLOG.md` S3-1/S3-2/S3-3) lands.

## [Unreleased]

Feature-complete and paired against real Google Home hardware; packaging
(v0.1.0) is the remaining gap before a first tagged release. High-level
summary of what's shipped (see `README.md` "Status" for the authoritative,
up-to-date version):

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

### Known gaps before v0.1.0

- Packaging: Node SEA single-exe + `dotnet publish` self-contained app +
  unified `build.ps1` dist folder (Sprint 3, `S3-1`).
- Unpair/factory-reset flow and `docs/user-guide.md` (`S3-2`).
- Perf/budget release pass and the first tagged `v0.1.0` (`S3-3`).
- Continued hardware end-to-end validation, logged in `docs/e2e-log.md`.
