# Security Policy

## Supported versions

MatterHelm is pre-1.0 and has not yet had a tagged release. Security fixes
land on `main`, which is the only supported line — there are no maintained
release branches. Once tagged releases begin (starting at v0.1.0), the most
recent release will be the supported one.

## Reporting a vulnerability

Please report suspected security issues **privately** via
[GitHub Security Advisories](https://github.com/fdymond/matterhelm/security/advisories/new)
for this repository ("Report a vulnerability" under the Security tab) rather
than opening a public issue. This repository is currently private, so only
people already with access can see it either way, but private advisories
keep the disclosure and fix contained until a patch ships and keep a
consistent process for when the repo goes public.

Include what you'd include for any report: affected component (`bridge/` or
`app/`), version/commit, reproduction steps, and impact.

## Response expectations

MatterHelm is maintained by a single person as a personal/hobby project, on a
best-effort basis. There is no SLA. Reports will be acknowledged and
triaged as promptly as reasonably possible, but response and fix timelines
are not guaranteed.

## Scope notes — what "secure" means for this project

MatterHelm's threat model is deliberately narrow (see
`docs/ENGINEERING-STANDARDS.md` "Security" and `docs/BLUEPRINT.md`):

- **Loopback-only IPC.** The bridge's WebSocket server and the tray app's
  client are bound to `127.0.0.1` only — never exposed on the LAN. The
  connection is token-authenticated and the socket is closed on the first
  invalid frame. Every inbound IPC frame is validated at the boundary
  (`zod` on the bridge side, typed-record parsing on the app side) — see
  `bridge/src/ipc/protocol.ts` / `app/MatterHelm/Sidecar/Protocol.cs`.
- **Token handling.** The IPC token is generated at runtime by the tray-app
  supervisor and passed to the sidecar via environment variables only. It is
  never logged, never persisted to disk, and never committed to the repo.
  If you find a code path that logs or persists it, that's a valid security
  report.
- **Matter fabric credentials.** matter.js's commissioning storage (fabric
  keys, session state) lives under the user's `%APPDATA%\MatterHelm`
  profile directory — never in the repo, never in a dist archive, never
  transmitted anywhere except over the local Matter fabric itself.
- **Diagnostics-bundle privacy.** Settings → Advanced → "Export diagnostics"
  (`app/MatterHelm/Diagnostics/DiagnosticsBundle.cs`) produces a local-only
  zip (logs, metrics, an environment manifest, `config.json`). Nothing is
  ever uploaded automatically. The manifest and bundled logs are scrubbed of
  machine name, username, and user-profile paths, and known commissioning
  secrets (setup passcode, manual pairing code, QR payload) are redacted from
  log content even if they were captured before a suppression fix landed.
  `config.json` is included verbatim since it's user-authored and may
  contain user-chosen paths. If you find PII, a token, or a credential that
  survives into an exported bundle, that's a valid security report.
- **No shell execution, no unsolicited network input.** The sidecar executes
  no shell commands and accepts no network input other than the Matter
  fabric (via matter.js) and the loopback IPC socket.

Out of scope: attacks that require local admin/physical access to the
machine MatterHelm runs on (that's a Windows security boundary, not this
app's), and issues in third-party dependencies (`@matter/main`, `zod`,
`pino`, `QRCoder`) that don't involve how MatterHelm uses them — please
report those upstream.
