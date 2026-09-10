# Security Policy

## Supported versions

MatterHelm is pre-1.0. The latest tagged release and `main` are supported;
older releases do not receive separate fixes.

| Version | Supported |
|---|---|
| v0.7.1 (latest as of 2026-09-10) | Yes |
| `main` | Yes |
| v0.7.0 and older | No |

## Reporting a vulnerability

Please report suspected security issues **privately** via
[GitHub Security Advisories](https://github.com/fdymond/matterhelm/security/advisories/new)
for this repository ("Report a vulnerability" under the Security tab) rather
than opening a public issue. Private advisories keep the report and any fix
contained until a patch is ready.

Include what you'd include for any report: affected component (`bridge/` or
`app/`), version/commit, reproduction steps, and impact.

## Response expectations

MatterHelm has one maintainer working in spare time, so there is no SLA. The
maintainer aims to acknowledge a report within about seven days, then will
triage, coordinate disclosure, and prepare a fix on a best-effort basis.
Severity, reproducibility, and maintainer availability determine the fix and
release timeline.

## Scope notes — what "secure" means for this project

MatterHelm's threat model is deliberately narrow (see
`docs/ENGINEERING-STANDARDS.md` "Security" and `docs/BLUEPRINT.md`):

- **Loopback-only IPC.** The tray app hosts a `localhost` WebSocket server and
  the bridge sidecar connects to it as a client. Because HTTP.sys can accept a
  broader socket binding than that URL prefix suggests, the app also checks
  every request's remote endpoint: non-loopback requests receive HTTP 403
  before they can take the single-client slot, and the one diagnostic WARN
  records only the address family. The first sidecar frame must authenticate
  with the session token. Both sides validate inbound frames at their
  boundaries (`zod` on the bridge side, typed-record parsing on the app side).
  The sidecar uses `ws` so its first malformed, binary, or schema-invalid tray
  frame closes the connection with policy code 1008; bounded reconnect then
  recovers against a healthy tray. The tray likewise closes a sidecar
  connection whose first or subsequent frame is invalid — see
  `bridge/src/ipc/client.ts` and `app/MatterHelm/Sidecar/IpcServer.cs`.
- **Token handling.** The IPC token is generated at runtime by the tray-app
  supervisor and passed to the sidecar via environment variables only. It is
  never logged, never persisted to disk, and never committed to the repo.
  If you find a code path that logs or persists it, that's a valid security
  report.
- **Matter fabric credentials.** matter.js's commissioning storage (fabric
  keys, session state) lives under the user's `%APPDATA%\MatterHelm`
  profile directory — never in the repo, never in a dist archive, never
  transmitted anywhere except over the local Matter fabric itself.
- **Diagnostics-bundle privacy.** Settings → Advanced → **Export diagnostics**
  (`app/MatterHelm/Diagnostics/DiagnosticsBundle.cs`) produces a local-only
  zip; nothing is uploaded automatically. Its `config.json` preserves the
  useful structure but redacts the identity seed, launch arguments, and
  user-profile paths. Raw config requires an explicit API opt-in and has no UI
  control. Every bundled log/metrics line is streamed through case-insensitive
  redaction for commissioning passcodes, manual pairing codes, text/JSON-key
  discriminator values, QR payloads, and IPv4/IPv6 literals; standalone/long
  QR block-art lines become `<qr-art>`. Machine/user replacement is word-
  boundary anchored, while full profile paths are replaced first so identity
  path segments remain covered. The manifest also
  omits machine/user identity and paths. Still review a bundle before sharing
  it; surviving credentials or identifying values are valid security reports.
- **No shell execution, no unsolicited network input.** The sidecar executes
  no shell commands and accepts no network input other than the Matter
  fabric (via matter.js) and the loopback IPC socket.

## Release integrity

Every tagged release publishes `SHA256SUMS.txt` beside the installer and
portable zip. Compare a downloaded artifact with that manifest before running
it. From the next release onward, GitHub build-provenance attestations will
also cover both packages and the checksum manifest; verify them with
`gh attestation verify` against `fdymond/matterhelm`.

Release packaging fails if strict SEA construction or required notice
generation fails. `THIRD-PARTY-NOTICES.txt` covers bundled npm packages,
Node.js, QRCoder, the .NET runtime, WindowsDesktop runtime, and Windows SDK
projection; review that generated coverage before a public tag.

The built-in updater downloads the package and checksum manifest from the same
GitHub release, verifies the package with SHA-256 using a constant-time
comparison, deletes a mismatch, and rechecks the hash immediately before the
installer or portable update is applied. A read handle protects the downloaded
package across verification and handoff/application, and the optional
`MATTERHELM_UPDATE_TOKEN` is removed from both sidecar and update-helper child
environments. `SHA256SUMS.txt` is integrity evidence, not an independent
authenticity root; Authenticode or a separately signed manifest remains a
maintainer decision.

Out of scope: attacks that require local admin/physical access to the
machine MatterHelm runs on (that's a Windows security boundary, not this
app's), and issues in third-party dependencies (`@matter/main`, `zod`,
`pino`, `ws`, `QRCoder`) that don't involve how MatterHelm uses them — please
report those upstream.
