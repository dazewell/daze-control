# Flow menu — spike results (S1, S2)

Snapshot, not maintained. Both spikes passed (or were amended by the user) and M12 was built on them: D-055, D-056, X-010, X-011.
Probes: `spikes/GamepadProbe`, `spikes/menu-window` (not in the solution). Screenshots: `s2/`.

- **S1 Controller input in AOT (T12.1).** A console AOT probe with three readers: GameInput (callbacks, focus
  policy for background input), XInput (20/60 Hz on connected slots), and device-interface arrival. With the
  user's Xbox pad (USB and Bluetooth if available), Steam running and not.
  **Gate:** (a) 0 AOT/trim warnings; (b) no pad on → 0 ms CPU over 10 min; (c) a pad on and untouched → GameInput
  0 ms over 10 min, or XInput ≤ 300 ms; (d) readings arrive while another app (a game or a full-screen video) is
  in front; (e) arrival/removal and sleep/resume restart reading without leaks; (f) no install, no admin;
  (g) with Steam Input on, list what Steam also sends (keys) for D-pad/A/B. Pass (a–f) with GameInput → GameInput;
  else XInput. Record which and the numbers in D-056 and ARCHITECTURE → Footprint.
  **Result 2026-11-04 (probe `spikes/GamepadProbe`, Steam Controller on its wireless puck; no Xbox pad that day):**
  XInput (D-056 accepted). (a) holds; GameInput fails, since its reading callbacks are E_NOTIMPL (X-011); (b) 0 ms;
  (e) arrival and removal hold (incl. the Xbox pad dropping and coming back by itself), sleep/resume not run; (f) holds.
  Xbox pad = Flydigi Vader 4 Pro on its wireless adapter (an Xbox 360 XInput pad): (c) 234 ms over 600 s untouched, but
  it slept after about 4 min; always on, 20 Hz would be about 410 ms, so T12.6 may need 10 Hz for chords only (D-056);
  (d) every button read with Steam's window in front, Steam on and off; Raw Input showed the pad sending about 500
  reports/s nonstop (D-056's "why not HID" confirmed). (g) Xbox pad: Steam injects nothing for single buttons, but
  **View+Menu becomes Alt+Tab** (Steam on: injected Alt, VK_GAMEPAD_VIEW/MENU, Tab; the task switcher opens). Steam
  off: View+Menu gives one injected VK 0x07, nothing else. The default stays `View+Menu` (user, D-057).
  (g) The Steam Controller never shows up as an XInput or GameInput pad. With Steam on, Steam injects keys
  (`LLKHF_INJECTED`, no device): A Enter, B Esc, X Space, Y nothing, D-pad arrows, View+Menu Tab+Esc, right pad
  injected mouse moves. With Steam off, the puck's own HID keyboard/mouse (VID_28DE PID_1304) sends the same keys
  except X (nothing), plus mouse moves, clicks and wheel. So the menu's arrows/Enter/Esc work with this pad in both
  states, chords can't come from it, and the §4 dedupe only matters for Xbox pads with Steam Input on.
- **S2 Menu window over apps and games (T12.2).** Extend a copy of the RunPopup spike into a no-activate menu with
  temporary hotkeys and the pass-through mouse hook, plus a throwaway D3D11 test app (no user games needed) in
  three modes: borderless window, flip-model full screen (fullscreen optimisations), and exclusive
  `SetFullscreenState(TRUE)` with optimisations off.
  **Gate:** (a) shown and navigable with keys and mouse over the first two, the foreground window unchanged before/
  after (`GetForegroundWindow`), the test app keeps rendering; (b) the third is detected through
  `SHQueryUserNotificationState` before showing; (c) every close path unregisters the keys (a second app can
  register Up afterwards); (d) DPI: correct size on 100 % and 175 % monitors; (e) mouse hook never adds input lag
  (hook time < 1 ms). Fail (a) → stop and ask the user (the menu would need focus, which they declined).
  **Gate as amended after S2 (user, 2026-11-04: "Borderless only"):** (a) applies to borderless and windowed
  games only. (b) refuses every DXGI full screen (QUNS = 3: exclusive mode and FSO) before showing anything, with
  #32 logged. Both hold, so M12 continues.
  **Result (2026-11-04, dev PC: one 2880×1800 panel at 175 %, Win 11 26300, FSO on by default; probe
  `spikes/menu-window`, not in the solution; GDI drawing, so acrylic is untested):**
  - (a) **Fails for mode 2.** Borderless (mode 1) passes. Keys (arrows, Tab/Shift+Tab, Home/End, Enter, Space,
    1–9, Esc) and the mouse (hover, wheel, click, click outside) all work. The foreground is the game before,
    during and after, and the game keeps 113–121 fps on a 120 Hz panel
    ([keys](s2/borderless-keys.png), [mouse](s2/borderless-mouse.png)). Flip-model full screen with FSO on
    (mode 2): QUNS = 3, so the check refuses it. Shown anyway, the menu works, but ~100 ms later the game is
    deactivated, the foreground goes to 0, DXGI leaves full screen and `Present` returns `DXGI_STATUS_OCCLUDED`.
    The game drops to the desktop ([shot](s2/fso-forced.png)). The same happens with a layered window, one that
    isn't topmost, and a 210×70 one in a corner. Hotkeys + hook with no visible window leave the game alone (X-010).
  - (b) Holds, and catches more than planned: QUNS = 3 for exclusive mode **and** for FSO; windowed borderless
    gives 2 (BUSY). Exclusive mode shown anyway behaves like mode 2 ([shot](s2/exclusive-forced.png)).
  - (c) Holds: every close path freed 20/20 keys, and then a second process registered all 20. Paths tested: Enter,
    Space, digit, click, click outside, Esc, close timer, step cancel, and the process killed (the OS frees them).
  - (d) Partly: at 175 % the menu is 700 px wide for 400 DIP, correct. 96 DPI was seen only once, when a test
    app's 1024×768 mode briefly switched the panel: 400 px wide, correct. No second monitor, so moving between
    100 % and 175 % monitors is untested (`WM_DPICHANGED` is handled in the probe).
  - (e) Holds: the hook's own time averages 5.6–6.4 µs, p99 ≤ 245 µs, max 245 µs (non-AOT build). The rest of the
    chain (`CallNextHookEx`: the user's AutoHotkey, PowerToys) takes 140 µs on average and up to 1 ms. That cost
    is theirs and exists without us.
  - Also: the run pop-up uses the same kind of window and doesn't check QUNS, so it may kick a full-screen game
    out too (ARCHITECTURE → Open questions).

