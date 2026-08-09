# ADR-004: Settings window + configurable/custom commands (protocol v2)

- **Status**: accepted
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
      "action": { "type": "launch", "path": "C:\\...\\kodi.exe", "args": "" } },
    { "key": "stop-media", "name": "HTPC Stop", "enabled": true,
      "action": { "type": "mediaKey", "keyName": "stop" } }
  ]
}
```

Custom action types (executor-side, tray app only — the sidecar never
executes anything): `mediaKey` (`keyName`: `playPause|next|previous|stop|mute|volumeUp|volumeDown`)
and `launch` (`path` + `args`, started detached, never elevated, path must
exist at save time). `key` is a unique kebab-case slug (validated), the
Matter endpoint id, and the wire identifier — **renaming a command's display
name never changes its `key`**, so re-pairing isn't needed for renames.

### 2. Env contract (BLUEPRINT §2.3 table amended)

`HTPC_BRIDGE_DEVICE_NAMES` is **replaced** by `HTPC_BRIDGE_ENDPOINTS` — a
JSON object mirroring the `commands` section minus `action` (the sidecar
needs names + which endpoints exist, never what they do):

```json
{ "speaker": {"name": "...", "enabled": true}, "playPause": {...},
  "next": {...}, "previous": {...}, "power": {...},
  "custom": [ {"key": "movie-mode", "name": "Movie Mode"} ] }
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
  momentary OnOff plugs; their auto-reset off-echo maps to null exactly like
  built-in momentaries.
- `protocol.ts` and `Protocol.cs` change in the same pair of stories; S4-R
  re-verifies mirror parity.

### 4. Settings window (`Ui/SettingsWindow`)

One resizable window (not modal), Fluent-informed within WinForms limits:
- **Left nav**: categories — General (bridge enable, IPC port, log level),
  Devices & Commands (built-in endpoint names + enable toggles, custom
  command list with add/edit/remove/enable), Overlay (toggle + preview
  button), Advanced (mDNS interface, storage dir display, config
  open/reload, factory-reset placeholder until S3-2).
- **Search box top**: filters visible settings across all categories by
  label/description substring; matching category auto-selected, non-matching
  controls hidden; clearing restores.
- Edits stage into a working copy; **Save** persists via `Config.Save()` +
  fires the existing `Changed` machinery (port/name/endpoint changes take
  effect on next bridge enable — the window says so inline); **Cancel**
  discards. Validation inline (port range, slug uniqueness, path exists).
- Reachable from tray menu ("Settings…" replaces "Device names…"/"Open
  config", which move inside the window's Advanced page).
- Theme: follows the app's light/dark rendering used by TrayIcons; system
  fonts (Segoe UI Variable when present), 8-px spacing grid, DPI-safe
  layout via TableLayoutPanel/FlowLayoutPanel — no absolute pixel layouts.

## Consequences

- **Easier**: all options editable without touching JSON; commands become a
  product feature; endpoint identity survives renames (stable keys).
- **Harder**: protocol v2 must land atomically across both sides (S4-1/S4-2
  in lockstep, S4-R parity check); Config gains a migration path.
- **Risk**: Google Home has to tolerate bridged endpoints appearing/
  disappearing on re-enable (matter.js supports dynamic bridged endpoints —
  ECOSYSTEMS confirms bridge support; validated at E2E).
- **Rollback**: the `commands` section degrades to the old five fixed
  endpoints if custom lists are ignored; protocol v2 revert = one literal.
