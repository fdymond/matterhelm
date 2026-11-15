# ADR-007: Measured resource budgets (G6 amendment) + bundled sidecar dev path

- **Status**: accepted; pipeline bounds added by
  [ADR-014](014-public-launch-hardening.md)
- **Date**: 2026-08-04
- **Story**: S6-1 (maintainer-directed deep review/optimization; measurements on the
  target machine at 200 % DPI)

## Context

BLUEPRINT G6 predates measurement. S6-1 measured: an EMPTY WinForms
net10.0-windows exe idles at ~30 MB working set / 6.9 MB private — working
set for a .NET desktop app is dominated by shared framework/GDI+ image pages
and is the wrong budget metric. The production tray app idles at ~25.6 MB
private. The dev-mode tsx sidecar launch ran 2–3 node processes peaking at
~267 MB private; an esbuild CJS bundle (`npm run bundle` →
`bridge/dist/bridge.cjs`, 5.5 MB) runs as ONE node process at ~90 MB private
settled, booting in ~0.9–1.0 s (tsx warm: ~2.9 s). A churn probe
(`--probe-resources`: 1300 canvas re-creates/window builds/icon swaps per
block) shows flat GDI/USER counts and non-growing private bytes at steady
state — no leaks.

## Decision

1. **G6 budgets restated in private bytes** (working set reported as
   informational only):
   - Tray app: private ≤ 32 MB (measured 25.6; empty-WinForms floor 6.9).
     WS informational ~55–85 MB.
   - Sidecar: ONE process; private ≤ 120 MB with matter.js 0.17.x (measured
     ~90 settled; OS trims RSS toward ~50 over hours). RSS informational.
   - Idle CPU < 0.5 % both (measured 0 %).
   - Cold start: sidecar spawn→"bridge started" < 3 s (bundle measures
     ~0.9–1.0 s).
2. **The bundled sidecar is the preferred dev launch**: `SidecarLaunchSpec`
   prefers `bridge/dist/bridge.cjs` when fresh (no `bridge/src/**` file
   newer), falling back to tsx sources so iteration never runs stale code;
   the packaged `sidecar\node.exe` layout still wins (S3-1 wraps exactly this
   bundle). §2.6 note: esbuild bundles from the packages' ESM dist (better
   tree-shaking; `--external:bun:sqlite` for a Bun-only conditional file) —
   supersedes the earlier "bundle from shipped CJS" wording; boot smoke
   proves runtime equivalence.
3. **Perf-budget gate for releases (S3-3)** checks these numbers with the
   same measurement method (private bytes, settled ≥ 2 min idle, plus the
   churn probe PASS).

> **Public-launch amendment:** ADR-014 adds bounded state coalescing, echo
> expectations, IPC send timeouts, macro concurrency, and segmented logs. The
> 2026-09-10 post-hardening measurement used a packaged 0.7.1-line build,
> settled for 75 seconds, then measured 30 seconds idle. Tray private memory
> was 18.5 MB (same-day pre-refactor baseline 19.0 MB), working set 74 MB,
> handles 664 (baseline 673), GDI objects 43 (43), and threads 28. Sidecar
> private memory was 90.8 MB (baseline 92.4 MB), with 227 handles. Idle CPU was
> 0.00 % for the tray and 0.10 % for the sidecar. The budgets (tray ≤ 32 MB,
> sidecar ≤ 120 MB, idle CPU < 0.5 %) hold.

> **2026-11-15 Node 26 measurement:** On the same packaged bridge bundle with
> matter.js 0.18, sampled after 70 seconds settled, the Node 22.23.5 SEA used
> about 101 MB private memory. Node 26.13.1 with V8 defaults used about 141 MB;
> an earlier post-commissioning sample reached 172 MB. Capping V8 with
> `--max-semi-space-size=8` brought Node 26 to about 110 MB, back inside the
> 120 MB sidecar budget; a 2 MiB cap also measured about 110 MB. ADR-016
> therefore embeds the 8 MiB cap and requires a fresh packaged measurement on
> every Node minor update. The final release-package measurement remains an
> integrator gate.

## Consequences

- **Easier**: honest, reproducible budgets; ~100 MB private and two
  processes removed from every dev-mode session; 3× faster bridge starts.
- **Harder**: developers must re-run `npm run bundle` after dependency bumps
  (source-file staleness is auto-detected; dependency-only changes are not).
- **Watch**: live tray handle count (919 seen after long uptime vs 697
  baseline; UI churn is probe-proven bounded — observe across a long run
  with the bridge active before suspecting BridgeHost/IpcServer).

**2026-11-15 packaged measurement (Node 26.13.1 SEA, matter.js 0.18, `--max-semi-space-size=8` baked into the SEA).**
A three-minute series (18 samples, 10 s apart) settles at **106.8–108.3 MB private (median 108 MB)**,
inside the 120 MB budget; the first ~60 s after start plateau at ~124 MB until the first major GC
returns memory. Single snapshots taken in that window overstate steady state, so budget checks use
the settled median of a time series. Uncapped Node 26 measured 141–172 MB; Node 22.23.5 measured
~101 MB on the same bundle. Tray: ~18–20 MB private, unchanged.
