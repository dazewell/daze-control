# UI findings 2026-11-12 (DRAFT, raw notes from live test round 1; final ranked table follows in the last commit)

# findings notes (raw)
- N1 drag existing step above top of flow content clamps at y~top of content (pointer far above, node stays at top) while library drop to same empty spot works; also dropped step overlapped neighbour 'Check internet' ~8px and was shifted right off column. (Morning check, showcase, 84%)
- N2 drag moves (Undo enabled) leave Save/Cancel disabled (not dirty); after library add Save enabled. Possibly by design (positions per PC).
- N3 library drop at pointer OK at 84%: node centre landed at pointer.
- N4 marquee over whole flow (17 steps+card+lines) -> right inspector pane completely blank (expected 'n steps selected' header per M15). Marquee selects by intersection (Miro-like) OK
- N5 Layout menu: geometry/width changes with state (Arrange again row only when placed by hand), submenu moves; 'Reset layout' vs 'Arrange again' unclear; radio dot shows current arrangement only after arrange. Tree/LTR/TTB layouts OK; orphan step (no edges) placed far right under inspector after Tree (clipped), view not refit after Arrange.
- N6 Problems flyout text: 'Volume (%) is missing (percent: ...)' leaks param key.
- N7 Outline view: flat list only, no failure-ignored/problem markers, no 'waits for' text, uses only a narrow left column of a wide area. Zero unnamed interactive UIA elements on canvas page; node ListItems have 'Waits for X' names but ambiguous when titles repeat ('Waits for Show message'). 'Icon' button (GlyphButton) weak name. 
- N8 Cancel discards all unsaved edits (6 changes) instantly: no confirm, Undo cleared.
- N9 Runs page: list items wrap '0 s' leaving orphan 's' on its own line ('Event · network disconnected · 20:54 · 0\ns'); detail pane empty-state tiny text top-left on a 3780px window; list column ~300px; page title 'Runs' while rail/tooltip call it History? (verify).
- N10 My click on 'Yes' in Ask prompt (mouse click at prompt) -> run ended 'Stopped' 14s; verify whether prompt click works.
- N11 Library Audio: long name clipped mid-word with no ellipsis ('behavic'); audio/BT library items have no rename (layouts/Android do, per UX §); disabled delete explains via tooltip only (hover; not keyboard-reachable?) UIA name of delete is good ('Remove X from Library').  Enter in Add-by-hand flyout adds (good). Dev items 'Not found on this PC' for hand-added.
- N12 Bind devices flyout: combos empty with no placeholder; validation 'Pick a Bluetooth device.' stays after a device is chosen (stale); name auto-fills with BT name.
- N13 Bound device row: Rename flyout edits internal slug ('dev-buds-fake') while list/Bind show display name 'Dev Buds (fake)' -> inconsistent; Rename icon-only (UIA name ok) next to labelled 'Devices' button; subtitle repeats the name 'Dev Buds (fake) + Dev Headset (fake)'. Programs tab has no Add by hand (other tabs do).
- N14 Settings: no Appearance/theme/text-size/animation controls in editor Settings (follows OS); 'Start with Windows' toggle is shared with real install (did not touch).
- N15 edge auto-pan: holding a dragged step ~6px (half-scale) from canvas right edge for 1.8s panned >1500 DIP (whole flow out of view); no cap/ease; drop at edge left node half under inspector. Undo then restores position but not the pan (expected).
