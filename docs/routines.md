# Natural voice phrases via Google Home routines

Google Home does not surface Matter media-playback clusters for this bridge.
Use Google Home automations with custom starter phrases to map natural speech
to MatterHelm's Speaker and On/Off Plug-in Unit endpoints.

Use multi-word starters: bare "pause" or "stop" can collide with Assistant's
own timers/casting commands.

## Recommended routine set

Google Home app → Automations → **+ New** → *When I say to Google Assistant*
→ *Then: Adjust Home devices*:

| You say ("Hey Google, …") | Routine action | Shipped meaning |
|---|---|---|
| "play the HTPC" / "resume the HTPC" | Turn **on** *HTPC Play Pause* | Requests absolute Play through the current SMTC session; fails/logs if none is usable |
| "pause the HTPC" | Turn **off** *HTPC Play Pause* | Requests absolute Pause through the current SMTC session; fails/logs if none is usable |
| "next on the HTPC" / "skip this track" | Change *HTPC Next* to its other state | Next fires on either user transition and retains the new state |
| "back one on the HTPC" | Change *HTPC Previous* to its other state | Previous fires on either user transition and retains the new state |
| "movie time" | Activate a custom command endpoint (e.g. *Movie Mode*), optionally plus lights | Retained custom commands fire on either transition by default |
| "shut down the theater" | Turn **off** *HTPC Power* | Runs the configured Power Off behavior |
| "wake the theater" | Turn **on** *HTPC Power* | Reverses displays-off/screensaver modes; does not resume playback |

For Next/Previous or other repeated one-shot actions, the most predictable
automation target is a custom command with **Reset the switch after it runs
(momentary button)** enabled. The routine always sends On; MatterHelm executes
once and locally returns the tile to Off after the configured delay (default
0 ms). The local reset never executes the command again.

## Tiles and retained state

The built-in Play/Pause, Next, Previous, and reversible Power endpoints are
real retained switches in 0.5.0; their shown state is meaningful and they do
not auto-reset. Next and Previous use both edges as triggers, while Play/Pause
uses On=Play and Off=Pause. Custom commands are also retained/both-edge unless
their reset checkbox is enabled.

Generic Switch is not a replacement: it is a device-to-controller event source
that Google may accept as a routine starter, not a voice-targetable/tappable
MatterHelm control.

Notes:

- Starters must be unique across routines.
- Direct forms still work (for example, "set HTPC Speaker volume to 40 %" and
  "turn off HTPC Power").
- Custom commands live in **Settings → Custom devices** and each enabled
  command publishes its own Google Home device.
