---
paths:
  - "app/**"
---

# App rules

- Keep nullable reference types enabled and compile with warnings as errors and
  `AnalysisLevel` `latest`; analyzer regressions are product defects.
- Do not suppress a warning without a nearby, specific justification for why the code is safe.
- Expose test seams as `internal` members ending in `ForTest`. Keep `InternalsVisibleTo` in the
  project file so production APIs do not expand for tests.
- Keep `BridgeHost` as the facade over `BridgeLifecycleCoordinator`,
  `BridgeActionDispatcher`, and `VolumeStatePublisher`; keep `MediaKeys` as the facade over
  `FocusedMediaRouter` and `WindowsForegroundMediaCommandSender`.
- Marshal WinForms work to the UI thread because controls and application context are not
  thread-safe.
- For P/Invoke, set `SetLastError = true` only when the native API defines last-error behavior,
  and represent owned native resources with the appropriate `SafeHandle`.
- Put every test that binds a real `HttpListener` in the shared non-parallel
  `IpcListenerSuite` collection to avoid HTTP.sys races.
- Run the applicable Release demo harness for UI, wiring, or configuration changes and report
  its objective output as acceptance evidence; unit tests alone do not exercise those paths.
