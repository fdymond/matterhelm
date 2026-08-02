# Natural voice phrases via Google Home routines

Google Home does not surface Matter's media clusters (research re-verified
2026-08-02: Basic/Casting Video Player and Media Playback remain absent from
Google's supported device/cluster lists, and Gemini for Home's new transport
phrases apply only to Google-recognized streaming sessions). The supported way
to get natural phrasing for the HTPC's commands is **routines with custom
starters** — a one-time, ~5-minute setup in the Google Home app.

Use multi-word starters: bare "pause"/"stop" collide with the Assistant's own
reserved global commands (they halt timers/casting and shadow custom
starters).

## Recommended routine set

Google Home app → Automations → **+ New** → *When I say to Google Assistant*
→ *Then: Adjust Home devices*:

| You say ("Hey Google, …") | Routine action |
|---|---|
| "pause the HTPC" | Turn **on** *HTPC Play Pause* |
| "play the HTPC" / "resume the HTPC" | Turn **on** *HTPC Play Pause* |
| "next on the HTPC" / "skip this track" | Turn **on** *HTPC Next* |
| "back one on the HTPC" | Turn **on** *HTPC Previous* |
| "movie time" | Turn on your custom command endpoint (e.g. *Movie Mode*), optionally + lights |
| "shut down the theater" | Turn **off** *HTPC Power* |

Notes:
- Play/pause is a single toggle on the PC, so "pause" and "play" both press
  the same button — both phrases exist purely so either feels natural.
- Starters must be unique across all routines; Google rejects duplicates.
- The direct forms keep working regardless ("set HTPC Speaker volume to
  40 %", "turn off HTPC Power").
- Custom commands you add in Settings → Devices & Commands appear as devices
  and can be routine actions the same way.
