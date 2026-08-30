# ADR-005: Bump to .NET 10 Windows; modernization baseline

- **Status**: accepted
- **Date**: 2026-07-28
- **Story**: S4-0/S4-4 (owner-directed quality pass; integrator research 2026-07-28)

## Context

Research against current Microsoft docs (2026-07-28): **.NET 8 (LTS) and
.NET 9 both leave support on 2026-11-10**; .NET 10 is the current LTS
(supported to Nov 2028) and this machine's SDK is already 10.0.x. Staying on
net8.0-windows means shipping v0.1 onto a dying TFM. .NET 10 also stabilizes
WinForms **dark mode** (`Application.SetColorMode`, experimental-gate removed)
— directly needed by the ADR-004 settings window — plus `System.Threading.Lock`,
C# 14 (`field`), and `JsonSchemaExporter`.

## Decision

1. **TFM**: both C# projects use
   `net10.0-windows10.0.17763.0`. The explicit Windows API floor supports the
   WinRT/SMTC projection while allowing Windows 10 version 1809 and later.
   CI's
   setup-dotnet moves to `10.0.x`. WinForms trimming remains forbidden;
   publish flags from ADR-003 item 7 unchanged.
2. **Modernization baseline** (S4-4 refactor applies; new code follows now):
   - `TimeProvider` injected for supervisor/HUD timers (FakeTimeProvider in
     tests where it beats hand-rolled fakes).
   - `LibraryImport` replaces `DllImport` for non-COM P/Invoke. CoreAudio
     `[ComImport]` interop stays as-is (GeneratedComInterface has documented
     gaps; convert only opportunistically).
   - System.Text.Json **source generation** (`JsonSerializerContext`) +
     `required`/`init` DTOs + `JsonUnmappedMemberHandling.Disallow` for
     protocol/config parsing where it can replace hand-rolled JsonElement
     walking without loosening strictness (rejection-reason quality must not
     regress — protocol drift bar still applies).
   - Collection expressions, file-scoped namespaces (already), selective
     primary constructors (DI-style only), pattern-matching cleanups,
     `System.Threading.Lock` for new/touched lock fields (IDE0330).
   - `async void` handlers audited: top-level try/catch mandatory.
   - Analyzers: `AnalysisLevel=latest`, `AnalysisMode=Recommended`,
     `CodeAnalysisTreatWarningsAsErrors=true` (S4-4 triages the fallout;
     per-rule exceptions via .editorconfig, never category-wide disables).
   - Dark mode: `Application.SetColorMode(SystemColorMode.System)` at
     startup once S4-3 verifies the overlay/pairing/settings windows render
     correctly under it.
3. **Deferred, tracked**: xunit v3 + Microsoft.Testing.Platform migration
   (build/CI-shaped — own story, post-0.1.0); FlaUI UI-automation smoke
   layer (prefer unit-testing settings view-model logic first).

## Consequences

- **Easier**: supported TFM through 2028; first-party dark mode; testable
  time; compile-time marshalling and JSON contracts.
- **Harder**: end users need no .NET install (self-contained publish), so the
  bump costs only build-machine SDK ≥ 10 (present locally; CI updated).
- **Risk**: WinForms behavior deltas 8→10 (anchoring/DPI changes landed in 8;
  10 is incremental) — the demos (`--demo-overlay`, `--demo-wired`) re-run
  after the bump as regression evidence.
