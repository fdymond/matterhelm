# S0-3 spike: commission an uncertified matter.js bridge with Google Home

**Status**: completed 2026-07-26 for pairing/persistence; Speaker and transport
UX validation moved to the current product E2E checklist
**Spike code**: `bridge/spike/pairing-spike.ts` (throwaway; see BACKLOG S0-3, ADR-002)

## Purpose

Prove, on real hardware, the riskiest assumption of this product (BLUEPRINT §3):
that Google Home will commission an **uncertified** matter.js bridge whose test
VID/PID (`0xFFF1` / `0x8000`) is registered in a free Google Home Developer
Console project — and that a bridged **Speaker** endpoint gets a usable volume
UX (voice "set volume to 40 %", app slider, mute). Also confirm that fabric
storage persists across restarts (no re-pairing) and that a Nest hub is in fact
required (ADR-002).

The spike bridge exposes exactly two endpoints:

| Endpoint | Matter device type | Clusters |
| ----------------- | ------------------ | ---------------------- |
| `HTPC Speaker` | Speaker | OnOff, LevelControl |
| `HTPC Play Pause` | On/Off Plug-in Unit | OnOff |

Every incoming cluster write is printed as a line starting with `EVENT`, e.g.
`EVENT speaker level=102 (~40%)` — that is your evidence feed.

## What you need before starting

- This PC and a **Google Nest hub device** (Nest Hub / Nest Mini / Nest Audio /
  Nest Wifi Pro / Google TV Streamer) on the **same LAN / Wi-Fi network**.
  A phone alone is not enough (ADR-002).
- An Android or iPhone with the **Google Home app**, signed into the same
  Google account that will own the Developer Console project below, with
  Bluetooth and local network permissions enabled for the Home app.
- **IPv6 enabled** on this PC's active network adapter (matter.js hard
  requirement). Check: Settings → Network & internet → your adapter →
  Edit IP assignment, or run `ipconfig` and confirm the adapter shows a
  "Link-local IPv6 Address".
- Node 22 available (the repo's tooling already uses
  `C:\Users\<user>\tools\node-v22.23.1-win-x64`).

## Part A — one-time Google Home Developer Console setup

Google's console UI moves around; steps marked *(verify in UI, may have
moved)* were accurate as of 2026-07.

1. In a browser, go to <https://console.home.google.com/> and sign in with the
   **same Google account the phone's Home app uses**.
2. Click **Create a project** (or **Get started** → new project) and give it
   any name, e.g. `HTPC Bridge Spike`. *(verify in UI, may have moved)*
3. Inside the project, choose **Add integration** → **Matter**. *(verify in
   UI, may have moved)*
4. In the Matter integration setup, enter:
   - **Vendor ID (VID)**: select the **test VID** `0xFFF1`
   - **Product ID (PID)**: `0x8000`
   - **Device type**: **Control Bridge** / **Aggregator** — pick the bridge /
     aggregator option offered. *(verify in UI, may have moved; if only
     concrete device types are offered, pick any — commissioning gates on
     VID/PID, not on this field, but note what you picked in RESULTS)*
   - Development / test integration is fine; **no certification, no payment**.
5. Save the integration. You do **not** need to complete "launch" /
   certification steps — a draft/development Matter integration is enough for
   the account that owns the project.
6. Confirm the phone's Google account is the project **owner** (it is, if you
   created the project with it; otherwise add it as a member under project
   user/member settings). Commissioning only works for accounts in the
   project.

> Why: Google Home refuses uncertified Matter devices unless their VID/PID is
> registered in a Developer Console project of the commissioning account
> ("Not a Matter-certified device" otherwise). See ADR-002.

## Part B — start the spike bridge

Open a terminal:

```powershell
cd C:\Users\<user>\Repos\htpc\windows-google-home-matter\bridge
npx tsx spike/pairing-spike.ts
```

Expected within a few seconds: matter.js startup logs, then a banner with an
ASCII **QR code**, the QR payload (`MT:...`), and an 11-digit **manual pairing
code**. matter.js also logs a `QR code URL: https://project-chip.github.io/...`
line — open that URL in a browser if the terminal QR is hard to scan.

- If **Windows Firewall** prompts to allow Node.js network access: **Allow**
  (private networks). This is mDNS + Matter traffic.
- Leave this terminal visible — all evidence appears here.

## Part C — pair with Google Home

1. On the phone, open the **Google Home** app → **+** / **Add device** →
   **Matter-enabled device** (wording varies: "New device" → choose home →
   "Matter device" / scan option).
2. **Scan the QR code** printed in the terminal (scan it right off the
   monitor), or choose "Set up without QR code" and type the manual pairing
   code.
3. Expect a consent screen along the lines of **"This device isn't
   Matter-certified"** / test device warning → continue. (If you instead get a
   hard **"Not a Matter-certified device"** failure, the VID/PID or account
   doesn't match Part A — see Troubleshooting.)
4. The app connects to the device, may ask which home/room, and lets you name
   the devices. Expect **two** tiles to appear: **HTPC Speaker** and
   **HTPC Play Pause**.
5. In the terminal, expect a `SPIKE commissioned` line during pairing.

## Part D — validation script

Run each step; after each one, look at the terminal for the `EVENT` line and
record the result in the RESULTS table below.

1. **App volume slider**: open the HTPC Speaker tile in the Home app, drag the
   volume slider → expect `EVENT speaker level=<n> (~<pct>%)` lines while
   dragging/releasing.
2. **Voice volume**: "Hey Google, set HTPC Speaker volume to 40 %" → expect
   `EVENT speaker level=102 (~40%)` (±1 on the raw level is fine — 0–100 %
   maps onto 0–254).
3. **Mute via app**: tap the speaker's mute/power control on the tile → expect
   `EVENT speaker onOff=false (muted)`.
4. **Mute via voice**: "Hey Google, mute HTPC Speaker" (and unmute) → expect
   the matching `EVENT speaker onOff=...` lines.
5. **Voice switch**: "Hey Google, turn on HTPC Play Pause" → expect
   `EVENT playpause onOff=true`. Then "turn off ..." → `onOff=false`.
6. **App tile tap**: toggle the HTPC Play Pause tile in the app → expect the
   corresponding `EVENT playpause onOff=...` line.

## Part E — restart / persistence check

1. In the spike terminal press **Ctrl+C** → expect a clean
   `SPIKE stopped.` shutdown.
2. Start it again: `npx tsx spike/pairing-spike.ts` → expect
   `SPIKE already commissioned — storage persisted, no re-pairing needed.`
   (no new QR code).
3. In the Home app, confirm the tiles still work (repeat one step from Part D)
   **without** re-pairing.

Factory reset for a re-run from scratch: stop the spike and delete
`bridge/spike/spike-storage\` — then remove the stale device from the Home app.

## Troubleshooting

- **Phone can't find / reach the device**: Windows Firewall must allow the
  Node process **UDP 5353** (mDNS) and **UDP+TCP 5540** (Matter) on the
  Private profile. Easiest: delete the blocked "Node.js" entries under
  Windows Security → Firewall → Allow an app, then re-run and Allow the
  prompt.
- **"Not a Matter-certified device" (hard failure)**: VID/PID in the console
  project don't match `0xFFF1`/`0x8000`, or the phone's account isn't a
  member of the project. Re-check Part A; changes can take a few minutes to
  propagate.
- **No hub found / setup insists on a hub**: a Google Nest hub device on the
  same LAN is a hard requirement (ADR-002) — confirm the hub is online in
  the Home app and on the same subnet, then record this in RESULTS.
- **matter.js errors mentioning IPv6, or pairing times out immediately**:
  IPv6 is disabled on the adapter — re-enable it (adapter Properties →
  check "Internet Protocol Version 6").
- **Multiple network adapters** (VPN, Hyper-V, VMware...): pin mDNS to the
  real LAN adapter before starting:
  `$env:MATTER_MDNS_NETWORKINTERFACE = "Ethernet"` (use the adapter name from
  `ipconfig`), then run the spike in the same terminal.
- **Stale state after a failed pairing**: Ctrl+C, delete
  `bridge/spike/spike-storage\`, remove any half-added device from the Home
  app, start over.

## RESULTS (run of 2026-07-26; integrator-recorded from owner report + logs)

Run date: 2026-07-26 · Hub: Google Nest hub · Phone/app version: not recorded

| # | Command / step | Observed (terminal + app/voice response) | Verdict |
| --- | ------------------------------------ | ---------------------------------------- | ------- |
| C | Pairing (QR scan, uncertified consent screen) | Owner completed pairing via the Dev Console recipe; storage gained `commissionedFabrics`, root certs, and a session-resumption record (fabric id <fabric id>) — operational CASE session was established | **PASS** |
| D1–D6 | Validation script | Not executed on the spike: the owner's spike session was terminated before validation (integrator process-kill error) and the hub did not reconnect afterwards (see below). Validation moved to the product bridge after S1-5 landed the same day. | **MOVED → product E2E** |
| E | Restart → no re-pair needed | Relaunch printed `SPIKE already commissioned — storage persisted, no re-pairing needed.`; identical passcode/discriminator across restarts | **PASS** |
| — | **Hub requirement confirmed?** | Not falsified by test (no hub-off attempt); ADR-002's docs-based answer stands. Hub used: Nest hub. | Docs-confirmed |
| — | **Hub reconnection after abrupt bridge death** | After the spike process was force-killed and relaunched (same identity, advertising operational mDNS every ~90 s, port open, firewall allowed), the Nest hub made **zero** reconnection attempts in >60 min; device stayed "offline" in the Home app | **FAIL — carried as product risk** |

**Console recipe notes** (any UI steps that differed from Part A): none reported.

**Speaker UX verdict** (S0-3 acceptance — does Google surface volume % voice +
slider on a bridged Speaker endpoint?): **pending — validated on the product
bridge** (same Speaker device type; results to be logged in docs/e2e-log.md).

**Other observations / proposed backlog items**:
- Hub-reconnect failure above → E2E must test app-restart recovery on the
  product bridge; if reproduced, investigate (mDNS TTLs? hub needs the device
  to initiate? subscription re-establishment) and consider a backlog story.
- Pairing recipe (free Dev Console project + test VID 0xFFF1/PID 0x8000)
  confirmed working end-to-end on real hardware — ADR-002's gate is accurate.
