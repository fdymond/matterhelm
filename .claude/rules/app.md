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
- Create every WinForms object in tests inside `StaTest.Run`, which puts the STA thread on a
  private desktop so nothing appears on the maintainer's screen. Never fall back to the
  visible desktop to make a gate pass; tests must not reach real shell launches
  (`UseShellExecute = true`), message boxes, or modal dialogs.
- Start console children with `CreateNoWindow = true`; a child process lands on its parent
  process's desktop, so pass the headless choice explicitly to child UI processes.
- Run the applicable Release demo harness for UI, wiring, or configuration changes and report
  its objective output as acceptance evidence; unit tests alone do not exercise those paths.
