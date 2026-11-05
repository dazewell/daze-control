# Flow menu (M12) — what is still open

M12 shipped (v0.40.0 and after; chords removed by D-086, Narrator dropped by D-091). The as-built design lives in
ARCHITECTURE (Menu core/runtime/window, Controller input), FEATURES, UX (§5.6 Flow menu, §5.4 Menu step, §5.7
strings #28–#44) and D-053..D-057, D-061, D-086, D-091. Spike results: [SPIKES.md](SPIKES.md). This file keeps only
what is not built or not yet checked live (D-053).

## Not built (polish, no task)
- Open animation scale 0.96→1, tile scale 1.03 and the pick flash (focus slide and fades are built).
- Battery level in `devices gamepads` beyond what XInput reports for wireless pads.
- Reopening the parent menu on B in a submenu (needs an "opened from" chain in the trigger). v1: B closes.
- A per-flow "show in menus as" label. v1: the flow's name.

## Open checks (the user, at the Xbox or Steam pad; T12.10 could not run them)
- A real pad drives an open menu: D-pad and left stick move (repeat 400/120 ms), A runs, B closes; buttons held at
  open are ignored until released.
- Pad-on cost with a menu open (XInput 60 Hz) and 0 ms CPU after it closes; reads while a borderless game is in front.
- Steam Input on an Xbox pad: one press = one move (the 100 ms key/pad dedupe); View+Menu still becomes Alt+Tab (D-057).
- The Steam Controller's desktop layout (arrows, Enter, Esc) navigates the menu.
- Menu over a real borderless game: foreground unchanged, game keeps rendering; a real exclusive/FSO game gets #32
  and stays in full screen.
- Readable from 3 m: Large size on a TV, light/dark/high contrast, 225 % text size; animation effects off = no motion.
- Sleep/resume with a menu open, and a pad plugged in while it is open.
