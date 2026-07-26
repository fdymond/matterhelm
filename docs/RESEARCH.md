# Research: connecting a Windows HTPC to Google Home (July 2026)

Goal: make the HTPC a controllable device in the Google Home ecosystem
("Hey Google…" from speakers/app, routines) with strong preference for **local,
self-hosted, low-ceremony** routes. Research conducted 2026-07-26.

## Routes evaluated

| Route | Maturity (mid-2026) | Windows/.NET fit | What it actually delivers | Local vs cloud | Long-term risk |
|---|---|---|---|---|---|
| **1. Matter virtual device (matter.js)** | Matter spec mature; Google's *consumer* support narrower than the spec | **Good** — matter.js is pure TypeScript, no native deps, documented on Windows 11 + Node 20+ | On/Off toggles (mute, power, momentary play/pause/next/prev via auto-reset switches) + real **Speaker** device type (On/Off = mute, Level Control = volume 0–100%, genuinely voice/slider controllable). **No** working play/pause/seek semantics — Matter's Media Playback / Content Launcher / Keypad Input clusters exist in the spec but Google Home's supported-cluster docs and 2025–26 release notes show no support | **Fully local**, zero cloud, zero Google account/developer ceremony — pairs with a click-through "Uncertified device" warning | Low — Matter is a multi-vendor CSA standard, not Google-controlled |
| 2. Home Assistant as bridge (HA Matter Hub or Nabu Casa) | Mature, active | Fine if HA runs elsewhere (Linux/Docker); adds an always-on service | Same voice-control ceiling as Route 1 via Matter mapping; Nabu Casa's Google Assistant integration gives real voice + routines for exposed entities | HAMH local; Nabu Casa is a paid cloud relay ($6.50/mo) | Low-moderate; heavy dependency stack if you don't already run HA |
| 3. Direct Google Smart Home Action (cloud-to-cloud) | Console migrated to Google Home Developer Console (Dec 2024); still open to individuals | High ceremony: real OAuth2 server, publicly reachable HTTPS fulfillment, GCP project, automated Home Test Suite before even personal use | No higher capability ceiling than Routes 1–2 (traits are on/off/volume-shaped) | Cloud + public hosting (exactly what we want to avoid) | Google deprecation record is poor (Conversational Actions killed 2023; Local Home SDK docs stale ~2023) |
| 4. Assistant Relay | **Dead** — repo archived by owner Aug 28 2025 | n/a | n/a | n/a | Not viable |
| 5. Cast/DIAL receiver on Windows | Native `Windows.Media.Casting`/DIAL APIs exist; OSS receivers mostly Linux (e.g. Shanocast) | Possible, niche | Wrong problem shape: makes the PC a *cast destination*, not a voice-actionable device | Local | Different use case; not the primary mechanism |

## Recommendation

**Primary: Route 1 — local Matter virtual bridge via matter.js**, driven as a
new front-end onto VoiceRemote's existing `CommandRouter`. Only route that is
simultaneously local, ceremony-free on the Google side, and Windows-native.

**Fallback:** Route 2, but only for households already running Home Assistant.

**Do not build:** Routes 3–5 (ceremony without capability / dead / wrong shape).

## What the recommended route delivers (the honest ceiling)

- **Real:** volume slider + percentage by voice, mute toggle (Speaker device
  type → Google Volume trait); on/off-style actions for transport and power via
  momentary virtual switches ("Hey Google, turn on HTPC Next" or — much nicer —
  routines: "Hey Google, movie time"). Google's Feb 2026 routine-trigger
  improvements make switch-based triggers first-class.
- **Not available (Google-side limitation, not ours):** native media grammar
  ("pause the HTPC") via Matter Media Playback clusters — Google Home does not
  surface them as of the 2025–26 release notes. Do not build toward it; revisit
  when Google's supported-clusters page changes.

## Open questions / risks (carried into Sprint 0 as spikes)

1. **Border router / hub requirement** — some sources indicate pairing a
   third-party Matter *bridge* may require a Google Nest/Home hub device as the
   fabric admin (phone-app-only households might hit friction). **Must be
   validated with real hardware before committing** — this is Sprint 0's spike.
2. "Uncertified device" warning at pairing — cosmetic; optionally removable
   later with a free VID/PID registration in the Google Home Developer Console
   (no OAuth/webhook — still ceremony-light).
3. matter.js API stability — active project with evolving APIs; pin versions,
   wrap in a thin adapter layer (see BLUEPRINT).
4. Momentary-switch UX in the Home app — switches show as stateful toggles;
   auto-reset (~800 ms) makes taps behave as presses. Verify Google doesn't
   debounce/flag the rapid state change.

## Addendum 2026-07-26 (integrator feasibility pass — supersedes conflicting text above)

Deep-dive research (see ADR-002 for sources) corrected two claims in this doc:

1. **Hub requirement (open question 1): answered — REQUIRED.** Google's docs
   are unambiguous: commissioning and controlling any Matter device in Google
   Home requires a Google/Nest Matter-hub device on the LAN. No phone-only
   path exists. S0-3 validates on hardware but no longer treats this as open.
2. **"Uncertified device" pairing (open question 2): NOT a click-through
   warning.** It is a hard attestation gate. Working recipe: test VID
   `0xFFF1`–`0xFFF4` / PID `0x8000`–`0x801F` registered in a free Google Home
   Developer Console project whose member is the commissioning account. "Zero
   Google account/developer ceremony" above is therefore wrong; the correct
   claim is "no cloud, no OAuth, no payment — one-time free console setup".
3. **New since April 2026**: Google Home natively supports Matter **Generic
   Switch** button-press routine triggers. S0-4's spike now also evaluates a
   Generic Switch endpoint alongside the momentary On/Off pattern (ADR-002).
4. Speaker (OnOff+LevelControl) volume mapping is documented by Google but has
   no confirmed real-world sighting for bridged endpoints — S0-3 must verify
   voice + slider explicitly before Sprint 1 leans on it.

## Sources

- Google Home Developers — [Supported Matter clusters](https://developers.home.google.com/matter/clusters) · [Supported device types](https://developers.home.google.com/matter/supported-devices) · [Matter release notes](https://developers.home.google.com/matter/release-notes) · [Virtual-device codelab](https://developers.home.google.com/codelabs/matter-device-virtual)
- [matter.js](https://github.com/project-chip/matter.js/) · [matter-node.js-examples](https://www.npmjs.com/package/@project-chip/matter-node.js-examples) · [matterjs-server](https://github.com/matter-js/matterjs-server) · [python-matter-server (archived; Windows unsupported)](https://github.com/matter-js/python-matter-server)
- Home Assistant — [HA Matter Hub](https://riddix.github.io/home-assistant-matter-hub/) · [Exposing HA entities as Matter devices](https://smarthomescene.com/guides/exposing-home-assistant-entities-as-matter-devices/) · [Google Assistant via HA Cloud](https://www.home-assistant.io/cloud/google_assistant/)
- Google cloud route — [Smart home Actions migration](https://developers.home.google.com/cloud-to-cloud/project/migration) · [OAuth 2.0 server requirement](https://developers.home.google.com/cloud-to-cloud/project/authorization) · [Local Home SDK](https://developers.home.google.com/local-home/overview) · [Conversational Actions sunset](https://developers.google.com/assistant/ca-sunset)
- [assistant-relay archived](https://github.com/greghesp/assistant-relay/releases) · [Shanocast](https://github.com/rgerganov/shanocast) · [Matter Casting (Amazon docs, for contrast)](https://developer.amazon.com/docs/fire-tv/overview-of-matter-casting.html)
- Google Home UX changes — [smart-button routine triggers](https://automatedhome.com/google-finally-adds-smart-button-triggers-to-home-routines-after-years-of-waiting/) · [Forbes on Google Home routines fix (Feb 2026)](https://www.forbes.com/sites/paullamkin/2026/02/04/google-home-finally-fixes-its-biggest-smart-home-flaw/) · [Matter device setup help](https://support.google.com/googlehome/answer/13127223?hl=en) · [Uncertified-device pairing thread](https://www.googlenestcommunity.com/t5/Smart-Home-Developer-Forum/How-does-Home-App-determine-what-is-a-Matter-certified-device/m-p/367053)
