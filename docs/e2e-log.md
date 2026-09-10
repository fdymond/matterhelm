# Hardware-in-the-loop checklist — current v0.7.x release line

This is an executable hardware-in-the-loop checklist. Blank cells mean **not
run**; do not infer a pass. Run it against the exact installer and portable zip
that will be released, using a Google/Nest Matter hub and Google Home app.

## Run header

| Field | Value |
|---|---|
| MatterHelm release/tag | |
| Run date/time (UTC) | |
| Commit/build identifier | |
| Installer filename + SHA-256 | |
| Portable filename + SHA-256 | |
| Windows version/build | |
| MatterHelm TFM copied from `MatterHelm.csproj` | |
| Google Home app version / phone | |
| Hub model / firmware | |
| Network/adapters (including VPN/virtual) | |
| Monitor models; DDC/CI enabled? | |
| Upgrade source version | |

## Automated preflight

| Step | Expected | Observed | Verdict |
|---|---|---|---|
| `cd bridge; npm.cmd run verify` | Typecheck, lint/format and the CI-published bridge test count pass with zero warnings | | |
| `dotnet test app/MatterHelm.Tests/MatterHelm.Tests.csproj -c Release` | App builds and every discovered test passes with zero warnings; record the exact count | | |
| Compare `bridge/src/ipc/protocol.ts` with `app/MatterHelm/Sidecar/Protocol.cs` and BLUEPRINT §2.3 | Message revision 5, handshake protocol 1, same strict frame union/fields including custom `on` | | |
| Run `MatterHelm.exe --demo-overlay`, `--demo-pairing-window`, and `--demo-wired` from the Release build | Each prints an overall PASS and exits 0 | | |
| Build release artifacts and verify `SHA256SUMS.txt` | Installer and portable hashes match | | |
| Export diagnostics from Settings with identity/path/IP fixtures in config and logs | Default zip keeps config structure but redacts identity seed, launch args, profile paths, commissioning credentials, machine/user names, and IPv4/IPv6 literals | | |

## Upgrade into the current release line

Before updating, create at least one enabled custom command in 0.4.x and leave
its old config without `resetAfterActivation`. Record its key and action.

| Step | Expected | Observed | Verdict |
|---|---|---|---|
| Update the installed 0.4.x copy with **Check for updates…** | Hash is verified; installer handoff completes; app relaunches; config and pairing survive | | |
| Open the migrated custom command | Reset checkbox is clear; first user transition runs once and state is retained; the opposite transition also runs once | | |
| Tick **Reset the switch after it runs**, save, then send On | Only On runs; tile returns to Off after Tap reset delay (0 = next tick); local reset does not run again | | |
| Exercise existing Play/Pause tile | State is retained: On requests Play, Off requests Pause; no automatic reset | | |
| Exercise existing Power tile in each configured mode | Behavior matches the Power matrix below; topology/pairing remains intact | | |
| Inspect startup/IPC logs | Matching bundled sidecar connects at `v:5`; no migration action is requested from the user | | |
| In an isolated test setup, attempt a known-stale sidecar peer | Exact-v5 parser rejects it and logs a version mismatch; current tray/sidecar still connect when launched together | | |
| Send a binary, malformed-JSON, then schema-invalid frame from isolated tray peers | Each first invalid frame closes with policy code 1008; the sidecar reconnects through bounded backoff and accepts a healthy peer | | |
| Attempt HTTP/WebSocket admission from a non-loopback address | Request receives HTTP 403 before taking the client slot; only one WARN appears and contains the address family, not the address | | |

## Distribution and updater paths

| Step | Expected | Observed | Verdict |
|---|---|---|---|
| Fresh per-user installer install | No admin prompt; Start entry works; optional startup choice works | | |
| Uninstall installed copy | Program files removed; `%APPDATA%\MatterHelm` config/pairing retained | | |
| Extract portable zip to a writable folder | Published app files, declared sidecar payload, `sidecar-layout.json`, `LICENSE`, `NOTICE`, `THIRD-PARTY-NOTICES.txt`, and `README-dist.md` are present; `MatterHelm.exe` runs without installed .NET/Node | | |
| **Check for updates…** in installed mode | Verified Setup asset runs in silent mode and relaunches without reboot | | |
| **Check for updates…** in portable mode while changing SEA ↔ Node layout | Verified zip stages/copies, removes the other layout's known files, preserves unrelated user files, and relaunches | | |
| Offer a bad/mismatched hash in an isolated release fixture | Update is refused and current installation remains runnable | | |

## Contextual first run, pairing, and tray states

| Step | Expected | Observed | Verdict |
|---|---|---|---|
| Start with no Matter fabric | Tray uses its taskbar-theme monochrome icon; menu shows **Pair with Google Home…** and hides **Enable bridge**; Factory reset and global items remain present | | |
| Choose **Pair with Google Home…** | Bridge is enabled/persisted and pairing window opens; icon is amber while starting | | |
| Wait for healthy advertisement | Icon becomes blue with "running, not paired yet"; QR/manual code and active VID/PID are on-screen, scannable, and DPI-safe | | |
| Make the commissionable advertisement unobservable in an isolated network test | Icon becomes red and the pairing status/log identifies missing advertisement | | |
| Restore network path and pair through Google Home | Uncertified/test-device consent proceeds; default five built-ins plus each enabled custom command appear | | |
| Complete commissioning | Icon becomes green; **Pair with Google Home…** hides and **Enable bridge** appears | | |
| Toggle **Enable bridge** off/on | Taskbar-theme monochrome while disabled; amber during start; green after connection; setting persists | | |
| Exercise a controlled repeated-sidecar-failure fixture | Icon becomes red for crash loop; no running process is force-killed as part of this checklist | | |

## Speaker, overlay, and media semantics

| Step | Expected | Observed | Verdict |
|---|---|---|---|
| Set Speaker volume to 40% by voice and slider | Windows volume reaches ~40%; Home slider reflects it; overlay header is **MatterHelm** above one volume bar | | |
| Mute/unmute by voice, tile, then locally | Windows and Home state agree; overlay uses one command/result pill | | |
| With Spotify paused and exposed through SMTC, turn Play/Pause On twice | First and repeated On request Play; playback never pauses; log identifies SMTC success | | |
| With Spotify playing, turn Play/Pause Off twice | First and repeated Off request Pause; playback never resumes; log identifies SMTC success | | |
| Repeat absolute Play/Pause against YouTube in a browser with a current SMTC session | On=Play and Off=Pause; no toggle inversion | | |
| Focus Kodi while Chrome has a stale paused SMTC session; send Pause | Kodi receives Pause; Chrome is untouched; log says different-app/unverifiable rather than short-circuiting | | |
| Send the same dedicated Pause to focused sessionless Kodi twice within two seconds, then once after | Immediate repeat is logged/suppressed; the later command is delivered with the documented toggle-risk warning | | |
| Make a focused window time out once, then accept the retry | MatterHelm waits 200 ms, retries once, and keeps the whole route within four seconds | | |
| Transition Next On→Off→On at natural speed | Next fires once per transition; switch retains each state; no auto-reset | | |
| Transition Previous On→Off→On | Previous fires once per transition; switch retains each state | | |
| Fire rapid commands and click through the overlay | One in-place HUD, no stack/flicker/focus theft; clicks pass through | | |

## Power matrix

Save each Power mode in Settings. An enabled bridge should restart immediately
when required; allow it to reconnect before the row.

| Mode / step | Expected | Observed | Verdict |
|---|---|---|---|
| **Displays off**, Power Off | Every DDC/CI-capable monitor receives hardware-off. If none accept DDC, Windows global blanking fallback runs; log states path | | |
| **Displays off**, Power On | Only displays MatterHelm powered off are restored; harmless wake nudge/fallback hold cleanup runs | | |
| Mixed DDC/non-DDC setup | Compatible display powers off; unsupported panel stays on (no global fallback when any physical monitor accepts DDC) | | |
| **Pause, then displays off**, Power Off | Dedicated Pause is requested, then the same DDC-first display policy runs | | |
| **Pause, then displays off**, Power On | Displays restore; playback remains paused (no resume) | | |
| **Screensaver**, Power Off / On with a player focused first | Off records the player and starts the configured saver; On stops it and restores the validated foreground window when Windows permits | | |
| Dismiss the saver manually with mouse/keyboard | Saver closes, but MatterHelm does not run its stop route and therefore does not restore focus | | |
| **Sleep**, Power Off | Sleep dispatches once; tile is promptly written back to On without another action | | |
| **Sleep**, controller On | No action | | |

## Custom devices and settings topology

| Step | Expected | Observed | Verdict |
|---|---|---|---|
| Settings categories/fields | General includes Enable bridge; Devices includes Bridge name; Custom devices is separate; Advanced includes VID, PID and read-only identity seed | | |
| Load over-limit hand-edited config fixtures | >1 MiB file, >64 commands, >64-character names, >2048-character launch args, and >128-character identity seed produce validation/WARN errors and defaults/dropped entries, not crashes | | |
| Add retained media/key/system/launch command in **Custom devices** and save | Enabled bridge restarts immediately; re-pair if Google needs topology refresh; action fires once on either user transition | | |
| Add reset-enabled custom command and send Off | Off does not execute | | |
| Send On to reset-enabled custom command repeatedly | Each On executes once; tile returns Off; reset delay defaults to 0 | | |
| Run macro with a Wait, then send volume during wait | Steps stay ordered; volume remains responsive; failure stops at named step | | |
| Start the same delay-bearing macro twice, then fill all eight macro slots | Repeat is nacked as `macro already running for custom command: <key>`; ninth distinct macro is nacked as `macro capacity reached (8 concurrent delay-bearing macros)` | | |
| Burst local volume changes while an IPC state send is stalled | One send remains in flight, only the latest pending state survives, and the send times out within five seconds | | |
| Add a standalone **Move the mouse** command; send On, then Off | On captures and moves to the selected virtual-desktop target; Off restores the captured position; explicit coordinates are clamped to the desktop | | |
| Add **Mouse move** through the macro's single **Add…** flow | The shared step dialog offers Mouse move; the step moves once and neither captures nor restores standalone retained state | | |
| On the HTPC, run a macro: **Stop screensaver → Wait → Play** after MatterHelm started the saver | Saver dismissal succeeds even if best-effort focus restore is refused; the macro continues through Wait to Play; successful restore targets the pre-saver player | | |
| Launch a Microsoft Store app through **Store app…** | Execution-alias path launches successfully | | |

## Network selection and recovery

| Step | Expected | Observed | Verdict |
|---|---|---|---|
| Open mDNS selector on multi-adapter host | Auto plus filtered LAN adapters/IPv4 annotations appear; Show all exposes the rest | | |
| Save a detected explicit LAN adapter | Enabled bridge restarts; advertisement is visible; pairing works | | |
| Remove/rename selected adapter | Settings shows **(not detected)** and health becomes red/missing rather than falsely green | | |
| Return to Auto | Bridge restarts and selects a usable route | | |
| Start another MatterHelm copy in a different signed-in/RDP session | Port conflict turns the active tray red, the log names the reason, and **Enable bridge** remains checked and recovers after the competing copy exits | | |
| Normal Exit/relaunch while paired | Same identity/fabric returns; record controller reconnect time from session logs | | |

## Factory reset and re-pair

| Step | Expected | Observed | Verdict |
|---|---|---|---|
| Factory reset from paired/green state | Confirmation appears; overlay/log says restart is producing a new pairing code; bridge enables/persists and restarts | | |
| Observe Google Home | Old tiles become offline and are manually removable | | |
| Observe pairing window | It opens automatically, progresses from advertisement starting, and shows a fresh code | | |
| Confirm settings | Names, custom commands, overlay and other config survive; only Matter fabric is deleted | | |
| Force the storage-directory staging rename to fail in an isolated fixture | Reset reports failure, restores the prior enabled state, and the live Matter directory remains byte-for-byte untouched | | |
| Allow staging but hold a file open during staged cleanup | Reset reports completion and fresh pairing; WARN contains `residue left at '<path>'` | | |
| Repeat reset after disabling bridge | Reset still enables/persists/restarts bridge and opens pairing; it does not remain disabled | | |
| Re-pair | New code commissions successfully; blue becomes green and contextual menu changes | | |

## Final verdict

| Gate | Result |
|---|---|
| Automated suites green with exact counts recorded | |
| Installer + portable + both updater paths | |
| Upgrade/migration behavior | |
| Pairing/contextual tray/full state legend | |
| Speaker/media/Power/custom semantics | |
| mDNS/DDC/factory-reset recovery | |
| Overall current-release verdict | |
