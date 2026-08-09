# ADR-002: Google-side prerequisites — Nest hub required, test-VID Developer Console setup required

- **Status**: accepted
- **Date**: 2026-07-26
- **Story**: pre-S0-3 (integrator feasibility research, 2026-07-26)

## Context

Web research against Google's current documentation (support.google.com
12391458 / 13127223 / 15765771; developers.home.google.com matter/troubleshooting,
matter/integration/create; project-chip and google-home GitHub issue reports)
contradicts two claims carried in `docs/RESEARCH.md` and the README:

1. **A Google Matter hub is required.** "Before you can add Matter-enabled
   devices to Google Home, you need a Matter-enabled hub device" — a Google
   Home/Nest speaker, display, Nest Wifi Pro, or Google TV Streamer on the
   LAN. A phone with the Home app alone cannot commission or control our
   bridge. (The spike S0-3 question is answered on paper; hardware validation
   remains scheduled.)
2. **Uncertified pairing is a hard gate, not a click-through warning.** Google
   Home only commissions a non-certified device whose VID/PID is a sanctioned
   test pair (VID `0xFFF1`–`0xFFF4`, PID `0x8000`–`0x801F`) registered in a
   **free** Google Home Developer Console project, with the commissioning
   Google account a member of that project. Attestation checks tightened over
   2023–2025; mismatches fail with "Not a Matter-certified device". No payment
   or CSA certification is needed for personal use.

Additionally, Google shipped native **Generic Switch** (button-press) routine
triggers around April 2026, which may fit our "momentary button" endpoints
better than the auto-reset On/Off pattern — but Generic Switch endpoints are
not voice-actionable targets the way On/Off endpoints are.

## Decision

- Keep Route 1 (matter.js virtual bridge). Amend the product prerequisites:
  a Google Nest hub device on the LAN and a one-time free Developer Console
  project (test VID/PID `0xFFF1`/`0x8000` by default, both configurable in
  bridge config) are documented **requirements**, not caveats. The user guide
  (S3-2) gets a step-by-step Developer Console section; S0-3's spike script
  includes creating that project before first pairing.
- README/RESEARCH wording "no Google developer account, no ceremony" is
  corrected to "no cloud, no OAuth server, no paid certification — one-time
  free Developer Console registration required".
- Momentary transport endpoints **stay On/Off Plug-in Units** (voice targets
  are a core goal, G1). S0-4's spike is extended: additionally commission one
  **Generic Switch** endpoint and record how the Home app/routines surface it.
  If it proves strictly better for routines, a follow-up story may add Generic
  Switch endpoints alongside (not replacing) the On/Off ones.

## Consequences

- **Easier**: pairing failures become diagnosable (VID/PID mismatch vs network);
  the spike no longer chases a "phone-only" configuration that cannot work.
- **Harder**: first-run UX gains a one-time Google Console step; the README
  pitch is more honest but less magical. Households without any Nest hub
  device cannot use the product at all — this is now stated up front.
- **Watch**: Google is tightening certification through 2026 (CSA Interop Test
  Lab). If the test-VID hobbyist path is ever curtailed, revisit with a new
  ADR (fallback: Home Assistant Matter Hub route per RESEARCH.md Route 2).
