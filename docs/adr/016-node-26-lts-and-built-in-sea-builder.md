# ADR-016: Node 26 LTS and the built-in SEA builder

- **Status**: accepted
- **Date**: 2026-11-15
- **Story**: S13-9

## Context

Node 22 enters end-of-life on 2027-04-30, while Node 26 "Lithium" entered
Active LTS on 2026-10-28 and is supported through 2029-04-30 according to the
[Node.js release schedule](https://github.com/nodejs/Release#release-schedule).
The release build previously created a preparation blob and injected it with
`postject` 1.0.0-alpha.6, whose last npm release was in 2023. Node added the
built-in `node --build-sea` path in v25.5.0. The
[Node 26 SEA documentation](https://nodejs.org/docs/latest-v26.x/api/single-executable-applications.html)
still marks SEA Stability 1.1 (active development), but the built-in builder
now owns both blob generation and platform-specific executable injection.

## Decision

Require Node 26 for bridge development, CI, and release packaging. Pin release
builds to Node 26.13.1 through `.node-version`, and set the bridge engine floor
to `>=26.13`.

Build the sidecar with `node --build-sea` and this explicit configuration:

- `main` is the absolute path to the single esbuild-produced `bridge.cjs`.
- `mainFormat` is `commonjs`, matching esbuild's output format.
- `executable` is the resolved `process.execPath` whose version and optional
  nodejs.org SHA-256 have already been checked; `output` is `bridge.exe`.
- `disableExperimentalSEAWarning` is true because the warning is a packaging
  decision, not an actionable end-user condition.
- `useSnapshot` is false because the bridge must parse runtime environment and
  does not register a V8 snapshot deserialization entry point.
- `useCodeCache` is false until a measured cold-start benefit justifies adding
  another V8-generated artifact to the Stability-1.1 packaging path.
- `execArgv` contains `--max-semi-space-size=8`, fixing V8's maximum
  semi-space at the measured value that keeps the Node 26 sidecar inside the
  ADR-007 budget. `execArgvExtension` is `none`, so `NODE_OPTIONS` cannot
  extend or override the packaged runtime policy when `bridge.exe` is launched
  directly.

Keep strict SEA failure as the default and retain
`-AllowNodeLayoutFallback` as the explicit escape hatch to the approved
`node.exe` plus `bridge.cjs` layout. Deny the current `esbuild` and
`unrs-resolver` npm install scripts: both required platform binaries arrive as
lockfile-pinned optional dependencies, so CI does not need their postinstall
checks or fallback downloads.

## Consequences

- Release packaging no longer depends on the unmaintained `postject` tool or
  Node's legacy `--experimental-sea-config` workflow.
- SEA remains an active-development Node feature. Every Node minor update must
  re-run the real bundle build, configuration-error boot probe, packaging
  gates, and packaged private-memory measurement before the pin moves.
- On the same bridge bundle and matter.js 0.18 workload, settled for 70
  seconds, the Node 22.23.5 SEA used about 101 MB private memory. Node 26.13.1
  with V8 defaults used about 141 MB (and reached 172 MB in an earlier
  post-commissioning sample), exceeding ADR-007's 120 MB ceiling. Node 26 with
  `--max-semi-space-size=8` used about 110 MB; a 2 MiB cap measured the same,
  so 8 MiB retains more young-generation headroom without a measured memory
  penalty.
- The SEA configuration sanity check verifies the serialized `execArgv` and
  `execArgvExtension`, then boots the produced `bridge.exe` with an invalid
  inherited `NODE_OPTIONS=--require=...`. Reaching the bridge's missing-token
  configuration error proves that the SEA ignored the injected Node option
  without adding a diagnostic branch to production bridge code.
- The tray supervisor also removes `NODE_OPTIONS` from the child environment,
  covering both the SEA and approved `node.exe` plus `bridge.cjs` fallback.
- The official Node executable selected as `executable` is exactly the file
  checksum verification covers. The fallback layout copies that same file.
- Current SEA artifacts ship without a MatterHelm Authenticode signature and
  remain runnable. When signing lands, build from a temporary Node copy whose
  inherited signature is removed before SEA injection, then sign the final
  `bridge.exe`; the upstream Node signature must never be presented as the
  product signature after its PE resources change.
- If the built-in builder regresses, maintainers may opt into the existing
  fallback layout for a non-release diagnostic build. Tagged releases remain
  strict until this ADR is superseded or the regression is resolved.
