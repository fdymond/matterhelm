# End-to-end validation log (hardware-in-the-loop)

Scripted checklist for the manual E2E gate in `docs/DEVELOPMENT-PLAN.md`
("E2E | manual, scripted checklist | pairing + each voice action on real
Google Home hardware, results logged in `docs/e2e-log.md`") and the S3-2
acceptance criterion ("Scripted E2E checklist executed & logged in
`docs/e2e-log.md`"). This is **not** automated — it needs a real Nest hub,
a real phone running the Google Home app, and a human. `docs/spikes/
S0-3-pairing.md` ran an earlier, narrower version of this on the throwaway
spike bridge; this is the full pass against the shipped product.

**How to use this file**: work through the rows top to bottom on a real run
(packaged dist per S3-1, or `dotnet run`/`npm start` if packaging hasn't
landed yet — note which in the run header below). Fill in **Observed** and
**Verdict** (`PASS` / `FAIL` / `MOVED` / `N/A`) and a UTC **Timestamp** as
you go; leave a row's Observed/Verdict/Timestamp blank until it's actually
been run — a blank row means "not yet executed," not "assumed fine." Add a
new **Run** section per pass (don't overwrite prior evidence); carry
forward a short note in each new run's header on what changed since the
last one.

## Run 1 header (build prepared 2026-08-23 by the integrator; checklist execution pending the human)

The v0.1.0 build originally prepared for this run was superseded before the
human pass ran, so the header now names the **v0.3.0 release build** — the
one currently installed and running on the HTPC (verified 2026-08-23: the
running process is `dist\MatterHelm.exe`, ProductVersion `0.3.0+4090e14`,
with `dist\sidecar\bridge.exe` alongside). All rows below still apply as
written; rows added since v0.1.0 are marked with their story in-section
(S8-4 repeat/tap behaviour, S9 overlay theme/opacity, S9 system commands).

| Field | Value |
|---|---|
| Run date | *(fill in when executed)* |
| Build under test | packaged `dist\` from `build.ps1` at tag `v0.3.0` (commit 4090e14), Node SEA sidecar layout (`sidecar\bridge.exe`) — identical bits to the released `matterhelm-v0.3.0-win-x64.zip` |
| App version | 0.3.0 (`MatterHelm.exe` ProductVersion `0.3.0+4090e14`) |
| Bridge version | 0.3.0 (matter.js 0.17.7, bundled) |
| Windows build | Windows 11 Enterprise 25H2, build 26200 |
| Hub model | Google Nest hub |
| Home app version | *(fill in from the phone when executed)* |
| Tester | fdymond |

## Distribution (S10-1 — first release shipping an installer)

Integrator note 2026-08-23: the installer's mechanics were verified locally
against the v0.2.0 dist (silent install → app boots and re-establishes the
hub session → silent uninstall removes files/Start-menu/Run key while
`%APPDATA%\MatterHelm` survives). These rows re-verify the **released**
v0.3.0 assets through the normal (non-silent) user path.

| Step | Expected | Observed | Verdict | Timestamp (UTC) |
|---|---|---|---|---|
| Download both assets from the v0.3.0 release; check them against `SHA256SUMS.txt` (`certutil -hashfile <file> SHA256`) | Both hashes match the manifest | | | |
| Run `MatterHelm-Setup-0.3.0.exe` normally | SmartScreen may warn (unsigned — "More info" → "Run anyway"); no admin prompt; wizard completes; Start-menu entry exists | | | |
| Tick "Start MatterHelm when you sign in", finish, let it launch | App starts; helm tray icon appears | | | |
| Sign out / sign back in (or reboot) | MatterHelm starts automatically | | | |
| Uninstall via Settings → Apps | App and Start-menu entry removed; **pairing and settings survive** (`%APPDATA%\MatterHelm` intact) | | | |
| Unzip `matterhelm-v0.3.0-win-x64.zip` elsewhere and run `MatterHelm.exe` | Runs portably against the same `%APPDATA%` state — still paired, no re-pair needed | | | |

## Pairing

| Step | Expected | Observed | Verdict | Timestamp (UTC) |
|---|---|---|---|---|
| Fresh install, no prior pairing: enable bridge (tray → Enable bridge) | Tray icon goes gray → amber within a few seconds; no crash | | | |
| Firewall prompt on first bridge start | Windows Firewall prompts for Node.js/MatterHelm network access; Allow (Private) | | | |
| Open "Pair with Google Home…" | QR code + manual pairing code render; window is legible/scannable off-screen | | | |
| Scan QR in Google Home app | Home app shows the uncertified-device consent screen, then proceeds (not a hard "Not a Matter-certified device" failure) | | | |
| Complete pairing, name devices | Home app shows tiles for HTPC Speaker, HTPC Play Pause, HTPC Next, HTPC Previous, HTPC Power (+ any configured custom commands) | | | |
| Tray state after pairing completes | Tray icon turns green | | | |

## Per-device voice + app-tile checks

| Step | Expected | Observed | Verdict | Timestamp (UTC) |
|---|---|---|---|---|
| Voice: "Hey Google, set HTPC Speaker volume to 40 %" | System volume changes to 40 % on the PC; overlay flashes "Google Home → Volume" with a fill bar at ~40 % | | | |
| **Speaker volume/slider** (S0-3 carried item): drag the HTPC Speaker tile's volume slider in the Home app | System volume tracks the slider in near-real-time while dragging; overlay flashes per step or per settle (no crash/lag pile-up) | | | |
| **Speaker volume/slider verdict**: does the bridged Speaker endpoint give a usable slider UX, not just voice? (S0-3 acceptance question, never validated on real hardware until this run) | Usable slider — drag lands within a driver step or two of the target, no visible fight-back | | | |
| Voice: "Hey Google, mute HTPC Speaker" / "…unmute…" | System mutes/unmutes; overlay flashes "mute"/"unmute" | | | |
| App tile: tap the speaker's mute control | Same mute/unmute effect as voice | | | |
| Voice: "Hey Google, turn on HTPC Play Pause" | Media play/pause toggles on the PC; tile flips on then back off within ~1 s (momentary auto-reset) | | | |
| App tile: tap HTPC Play Pause | Same play/pause toggle | | | |
| Voice: "Hey Google, turn on HTPC Next" / "…HTPC Previous" | Next/previous track fires; tile auto-resets | | | |
| App tile: tap HTPC Next / HTPC Previous | Same next/previous effect | | | |
| Voice: "Hey Google, turn off HTPC Power" | Configured power-off behavior fires (displays off / sleep / pause+displays off, per Settings) | | | |
| App tile: tap HTPC Power off | Same power-off behavior | | | |
| Voice: "Hey Google, turn on HTPC Power" (if power-on is configured/expected) | Displays wake | | | |

## Repeat commands (ADR-008 command interception)

Pre-ADR-008 a repeated identical command was silently dropped (the attribute
was already at the target value, so matter.js emitted no change event and no
action was sent). The bridge now dispatches from the OnOff command itself —
proven against a live node in `src/matter/smoke.ts`. What only hardware can
settle is the ADR-008 **Watch** item: whether Google's Home client bothers to
*send* the redundant command.

**Owner observation 2026-08-16 (real hardware, resolved the Watch item)**:
with the reset at 0 ms, Google's tile state lagged the bridge — the tile
stayed "on", so the next tap arrived as an `Off` command and did nothing;
the user had to toggle off manually before the following tap fired
(re-typing the device to "Switch" in the Home app changed the icon only).
Fixed the same day by S8-4: momentary endpoints now dispatch on **both**
OnOff commands, so any tap fires regardless of what state Google believes.
The rows below re-verify tap-tap-tap behavior on the S8-4 build.

| Step | Expected | Observed | Verdict | Timestamp (UTC) |
|---|---|---|---|---|
| Voice: "Hey Google, turn on HTPC Next" twice in a row, the second within ~1 s of the first (inside the 300 ms reset window and just after it) | Track skips **twice**. If only the first skips, note whether the Home app tile flickered on the second — that distinguishes "Google didn't send it" from a bridge-side drop | | | |
| App tile: tap HTPC Play Pause twice in rapid succession | Media toggles twice (pause then play) | | | |
| Voice: "Hey Google, turn off HTPC Power" when the tile already reads off | The configured power-off behavior fires again (pre-ADR-008 this did nothing) | | | |
| Voice: repeat a custom **key sequence** command twice in a row | The chord is sent twice | | | |
| App tile: tap HTPC Next three times in a row at natural speed (S8-4) | Track skips three times — every tap fires, whether the tile happened to show on or off when tapped | | | |
| Voice: "Hey Google, turn **off** HTPC Next" (S8-4 semantics) | Next-track fires (any command on a stateless tap endpoint is a press) | | | |

## Custom command

| Step | Expected | Observed | Verdict | Timestamp (UTC) |
|---|---|---|---|---|
| Settings → Devices → add a **media key** custom command (e.g. "Stop": mediaKey stop), save, restart bridge if prompted | New tile appears in Home app after re-pair/reload; voice + tile both dispatch the configured media key | | | |

## Key-sequence command

| Step | Expected | Observed | Verdict | Timestamp (UTC) |
|---|---|---|---|---|
| Settings → Devices (Custom devices) → add a **key sequence** custom command (e.g. `Ctrl+Shift+V` into a text field/editor open on screen), save | New tile appears; voice + tile send the exact chord to the focused window (visible effect, e.g. paste-as-plain-text) | | | |

## Macros and system commands (S8-3 / S8-5 / S9-5 / S9-8)

| Step | Expected | Observed | Verdict | Timestamp (UTC) |
|---|---|---|---|---|
| Add a **macro** (Command sequence) with a wait, e.g. launch a program → Wait 2000 ms → `F11`, and fire it | All steps run in order; the overlay reads "running N steps" at the start and the outcome when it finishes; other commands stay responsive *during* the wait (S8-6 — try a volume command mid-macro) | | | |
| Add a **system command**: Lock the PC; fire it by voice | Workstation locks | | | |
| Add a **system command**: Start screensaver, fire it; then fire a **Stop screensaver** command (S9-8 — the previous nudge implementation did nothing) | Screensaver starts; the stop command ends it within ~1 s | | | |
| Add a **launch** command targeting a **Microsoft Store app** (e.g. Spotify), fire it (S9-5 — package paths used to fail "Access is denied") | The app starts; the log shows the execution-alias redirect line | | | |

## Overlay behavior

| Step | Expected | Observed | Verdict | Timestamp (UTC) |
|---|---|---|---|---|
| Fire several commands in quick succession (e.g. volume up/down a few times fast) | Overlay updates in place without flicker, stacking, or stale text; never steals focus or blocks a click underneath it | | | |
| Move mouse / click through where the overlay is displayed | Click passes through to whatever is underneath (click-through, non-activating) | | | |
| Settings → Overlay → change position, Preview | Overlay previews at the **staged** position immediately (S9-1), and returns to the saved position after the flash unless you save | | | |
| Settings → Overlay → set theme **Light**, Preview; then **Dark**, Preview (S9-4/S9-5) | Each preview renders in the staged theme — light panel with dark text, then the dark panel | | | |
| Settings → Overlay → drag **opacity** to ~50 %, Preview (S9-7) | The preview panel is visibly translucent; text stays legible | | | |
| With theme on **Follow system**, flip Windows light/dark (Settings → Personalization → Colors), then fire a command | The next overlay flash matches the new Windows theme without restarting the app | | | |

## State reflection (local change → Home app)

| Step | Expected | Observed | Verdict | Timestamp (UTC) |
|---|---|---|---|---|
| Change system volume locally on the PC (keyboard media key or Windows volume mixer), then open the HTPC Speaker tile in the Home app | The Home app's slider/level reflects the local change (may take a moment) | | | |
| Mute locally on the PC, check the Home app tile | Tile shows muted | | | |

## App-restart recovery (hub-reconnect watch item)

Carried forward from `docs/spikes/S0-3-pairing.md` (spike run: after a
force-killed/relaunched bridge with the *same* identity, still advertising
mDNS every ~90 s, the Nest hub made **zero** reconnection attempts in over
60 minutes — logged as **FAIL — carried as product risk** and re-scoped in
`docs/adr/006-telemetry-and-diagnostics.md` §4 as Google controller-side
re-association policy, not something bridge-side subscription tuning can
fix; cross-vendor evidence there cites matter.js controllers reconnecting in
≈2 min and Apple in ≈10–15 min, for contrast). This run re-tests it against
the shipped bridge (0.17.7, with S5-1's session-observability logging) and
**times** the stall instead of just eyeballing it, using the session-event
log lines ADR-006 added for exactly this purpose.

| Step | Expected | Observed | Verdict | Timestamp (UTC) |
|---|---|---|---|---|
**Integrator evidence 2026-08-09 (pre-filled)**: after enabling the bridge at
17:25:04 (clean restart, MatterHelm build, session-resumption records
present), the log shows `matter session established` at 17:25:04.790 and
`matter subscription created` at 17:25:05.048 — hub re-association in
**under one second** via CASE resumption. Earlier the same day (16:44, after
a longer outage) a `Subscription successfully reestablished … timing: 0 - 30s`
line shows reconnection on the ~30 s scale. The >60-min spike stall has not
reproduced since; the rows below re-verify on demand.

**Integrator observation 2026-08-09 (packaged v0.1.0 dist)**: during the
S3-3 budget measurement the packaged dist ran 17:51:46–17:55 (sidecar
online in ~2.7 s from spawn, log-verified) but the hub established **no**
session in that ~4-minute window — in contrast to the sub-second CASE
resumption at 17:25 the same day. Too short a window to call a FAIL
(ADR-006 §4: controller-side policy is erratic); the timed rows below
should watch for exactly this variance on the real run.

| Step | Expected | Observed | Verdict | Timestamp (UTC) |
|---|---|---|---|---|
| With the bridge paired and green, restart the tray app normally (Exit → relaunch, or just toggle Enable bridge off/on) | Sidecar cleanly stops (stdin-tether exit logged) and restarts with the same identity; tray returns to green once the hub reconnects | | | |
| **Time the reconnect**: note the timestamp the sidecar log shows the fresh session/subscription re-established (ADR-006 §1 session-observability events — grep the app/sidecar log for "session" around the restart) minus the restart timestamp | Record the elapsed time here even if it's fast — this is the metric the spike couldn't capture (spike observed >60 min/never; a healthy run might land in seconds to a couple of minutes) | | | |
| If the hub has *not* reconnected within ~5 minutes | Apply the documented mitigation: power-cycle the Nest hub or toggle the device tile in the Home app; note whether that fixes it and how long it then took | | | |
| Repeat with a **hard kill** (End Task the tray process, not Exit) rather than a clean restart | Compare reconnect timing/behavior against the clean-restart row above — a broken pipe/hard kill is the scenario the spike actually hit | | | |

## Factory reset → re-pair

| Step | Expected | Observed | Verdict | Timestamp (UTC) |
|---|---|---|---|---|
| With the bridge paired and green, trigger **Factory reset bridge…** from the tray menu, confirm | Confirmation dialog spells out the consequences; after confirming, bridge stops, Matter storage is deleted, log/overlay shows "factory reset complete — open Pair with Google Home to re-pair" | | | |
| Check the Home app after the reset | All MatterHelm device tiles show offline/unreachable (expected — Google isn't told directly) | | | |
| Remove the offline tiles in the Home app | Tiles removed cleanly | | | |
| Tray state after the reset completes | If the bridge was enabled before the reset, it auto-restarts (icon back to amber); if it was off, it stays off | | | |
| Re-pair from scratch (repeat the Pairing section above) | A **new** QR code/manual code is offered (different from the original pairing); pairing succeeds again with no leftover state from before | | | |
| Confirm config survived the reset | Device names, custom commands, overlay settings, and other Settings values are unchanged from before the reset (only the Matter pairing was wiped) | | | |
| Repeat Factory reset with the bridge **disabled** | Storage still deletes; bridge stays disabled afterward (no unexpected auto-start) | | | |
