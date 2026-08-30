# ADR-004: Settings window + configurable/custom commands (protocol v2)

- **Status**: accepted; schema/UI amended by shipped follow-ups and custom
  reset semantics superseded by ADR-012
- **Date**: 2026-07-28
- **Story**: owner direction (post-S2); implemented by S4-1…S4-4

## Context

The owner wants (a) a full settings UI — categorized navigation, search
filter, editing for every option — and (b) command configurability: built-in
commands can be enabled/disabled and users can add **custom commands** that
appear as additional Google Home devices. Today endpoints are a fixed set of
five, the env contract carries names only, and the protocol's action union is
closed. This ADR fixes the cross-cutting contracts so the app side and bridge
side can be built by parallel stories without drift.

## Decision

### 1. Config schema (app, additive; `config.json`)

`deviceNames` is **superseded** by a `commands` section (Config migrates old
`deviceNames` values into it on first load, then writes the new shape):

```json
"commands": {
  "speaker":   { "name": "HTPC Speaker",  "enabled": true },
  "playPause": { "name": "HTPC Play Pause", "enabled": true },
  "next":      { "name": "HTPC Next",     "enabled": true },
  "previous":  { "name": "HTPC Previous", "enabled": true },
  "power":     { "name": "HTPC Power",    "enabled": true },
  "custom": [
    { "key": "movie-mode", "name": "Movie Mode", "enabled": true,
      "resetAfterActivation": false,
      "action": { "type": "launch", "path": "C:\\...\\kodi.exe", "args": "" } },
    { "key": "stop-media", "name": "HTPC Stop", "enabled": true,
      "resetAfterActivation": true,
      "action": { "type": "mediaKey", "keyName": "stop" } }
  ]
}
```

Custom action types (executor-side, tray app only — the sidecar never
executes anything) now are:

- `mediaKey`, whose `keyName` is
  `playPause|next|previous|stop|mute|volumeUp|volumeDown|play|pause`;
- `launch` (`path` + `args`, detached and never elevated; path must exist at
  save time);
- `keySequence` (`sequence`, canonicalized from
  `[Ctrl+][Alt+][Shift+][Win+]<Key>` and injected via SendInput);
- `system`, whose `command` is
  `startScreenSaver|stopScreenSaver|displaysOff|displaysOn|sleep|hibernate|lock|closeForegroundProgram|shutdown|restart`;
- `delay` (`milliseconds` 1–5000), permitted as a sequence step; and
- `sequence` (`steps`, at most 16, no nesting, total delay at most 10 seconds).

`key` is a unique kebab-case slug (validated), the
Matter endpoint id, and the wire identifier — **renaming a command's display
name never changes its `key`**, so re-pairing isn't needed for renames.

### 2. Env contract (BLUEPRINT §2.3 table amended)

`HTPC_BRIDGE_DEVICE_NAMES` is **replaced** by `HTPC_BRIDGE_ENDPOINTS` — a
JSON object mirroring the `commands` section minus `action` (the sidecar
needs names + which endpoints exist, never what they do):

```json
{ "speaker": {"name": "...", "enabled": true}, "playPause": {...},
  "next": {...}, "previous": {...},
  "power": {"name": "...", "enabled": true, "momentary": false},
  "custom": [ {"key": "movie-mode", "name": "Movie Mode",
                "resetAfterActivation": false} ] }
```

Disabled built-ins are present with `enabled:false` (the bridge omits the
endpoint); disabled custom commands are simply omitted. No back-compat
shimming — no shipped users; both sides land together.

### 3. Protocol v2 (additive → per the versioning rule, `v` bumps to 2)

- Every frame's `v` becomes `2`; `hello.protocol` stays `1` (no breaking
  field changes). Parsers on both sides accept **only** `v: 2` after this
  lands (both ends ship from one dist; the rule "additive bumps v" is
  satisfied and drift stays detectable).
- New action variant, both parsers: `{ v: 2, type: "action", id: uuid,
  name: "custom", key: <kebab-case slug ≤ 64> }`. Custom endpoints are
  OnOff plugs. ADR-012 later supersedes this paragraph's momentary default:
  they retain state and dispatch both edges unless `resetAfterActivation` is
  true, in which case only On dispatches and the local Off reset is inert.
- `protocol.ts` and `Protocol.cs` change in the same pair of stories; S4-R
  re-verifies mirror parity.

### 4. Settings window (`Ui/SettingsWindow`)

One resizable window (not modal), Fluent-informed within WinForms limits:
- **Left nav**: categories — General (bridge enable, IPC port, log level),
  Devices (bridge name, built-in endpoint names/enables, Power policy and tap
  reset delay), Custom devices (add/edit/remove/enable), Overlay (toggle,
  placement/theme/opacity + preview), and Advanced (mDNS interface, VID/PID,
  read-only identity seed/storage, diagnostics, config open/reload, factory
  reset).
- **Search box top**: filters visible settings across all categories by
  label/description substring; matching category auto-selected, non-matching
  controls hidden; clearing restores.
- Edits stage into a working copy; **Save** persists via `Config.Save()` and
  fires the existing `Changed` machinery. A restart-requiring save restarts an
  enabled bridge immediately. Closing with staged changes prompts to save or
  discard; there is no persistent Cancel button. Validation is inline (port
  range, slug uniqueness, path exists).
- Reachable from tray menu ("Settings…" replaces "Device names…"/"Open
  config", which move inside the window's Advanced page).
- Theme: follows the app's light/dark rendering used by TrayIcons; system
  fonts (Segoe UI Variable when present), 8-px spacing grid, DPI-safe
  layout via TableLayoutPanel/FlowLayoutPanel — no absolute pixel layouts.

## Consequences

- **Easier**: all options editable without touching JSON; commands become a
  product feature; endpoint identity survives renames (stable keys).
- **Harder**: protocol v2 had to land atomically across both sides (S4-1/S4-2
  in lockstep, S4-R parity check); Config gained a migration path. Protocol v2
  is historical shipped behavior; the current exact parser revision is v4.
- **Risk**: Google Home has to tolerate bridged endpoints appearing/
  disappearing on re-enable (matter.js supports dynamic bridged endpoints —
  ECOSYSTEMS confirms bridge support; validated at E2E).
- **Rollback**: the `commands` section degrades to the old five fixed
  endpoints if custom lists are ignored; protocol v2 revert = one literal.
