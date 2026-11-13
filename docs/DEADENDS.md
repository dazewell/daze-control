# Dead ends

Things that were actually tried and failed, so nobody burns time on them again. Only real attempts go
here (an option rejected on paper belongs in the "Why not" of a decision). Format: `knowledge-log` skill.

Lessons from daze-media-osd that apply here (`C:\Projects\daze-media-osd\docs\DEADENDS.md`):
- OSD X-003 — defaults as property initializers on an init-only record for System.Text.Json (IPC records).
- OSD X-004 — `dotnet publish` of an unpackaged WinUI app without `EnableMsixTooling` (editor, T6.1).
- OSD X-007 — leaving `SystemBackdrop.OnDefaultSystemBackdropConfigurationChanged` to the base (editor backdrop).
- OSD X-010 — catching "no default endpoint" with CsWin32's `HRESULT.E_NOTFOUND` (audio provider).
- OSD X-011 — WMI "access denied" after CsWin32's `CoSetProxyBlanket` (any WMI use).
- OSD X-008 — a handled `gh api` 404 in a `shell: pwsh` step must reset `$global:LASTEXITCODE` (release.yml).

## X-001 · 2026-11-01 · Ending a pipe connection's pending read/write by disposing the pipe
- Tried: `PipeServer` dropped a slow subscriber (and closed connections on exit) with `pipe.Dispose()`.
- Failed because: a pending overlapped ReadFile/WriteFile holds a ref on the SafePipeHandle, so Dispose doesn't
  close it and the I/O never completes; with the default 0-byte buffer a write completes only once the client
  reads, so a client that never reads hangs the server's dispose forever.
- Evidence: `PipeTests.ASubscriberThatNeverReadsIsDropped` hung in `DisposeAsync` (PR #4, review 1 fixes).
- Retry only if: never — cancel the I/O's token (CancelIoEx) instead, then dispose.

## X-002 · 2026-11-02 · Passing app arguments through Velopack's Setup.exe (`Setup.exe --silent -- <args>`)
- Tried: a test install that starts the engine on a scratch config: `DazeControlApp-win-Setup.exe --silent
  --installto <dir> -- "<config.yaml>"`.
- Failed because: Setup 1.2.161 panics in clap ("Mismatch between definition and access of `EXE_ARGS`") and hangs;
  nothing is installed.
- Evidence: `%LOCALAPPDATA%\velopack\velopack.log`, setup pid 50748, 2026-11-02.
- Retry only if: a newer Velopack fixes it. Install without args, then start `current\DazeControl.exe <config>`.

## X-003 · 2026-11-02 · A MaxWidth grid as a ScrollViewer's direct content, to cap the pages' width
- Tried: `<ScrollViewer><Grid MaxWidth="1000" HorizontalAlignment="Stretch">` for the Flows/Library/Settings pages;
  then also `HorizontalScrollMode="Disabled"`.
- Failed because: maximized (1397 DIP viewport, 175 %), the grid was arranged ~430 DIP from the left at its full
  width, so the page ran past the right edge (user's first-use report). The left offset didn't follow the cap.
- Evidence: UIA bounds of the "Flows" title, 2026-11-02; fixed by a plain stretching Grid between the two.
- Retry only if: never — put the cap on an element inside a stretching parent.

## X-004 · 2026-11-02 · Canvas views that subscribe to their node on Loaded/Unloaded and reorder by remove + insert
- Tried: `NodeView` subscribed to its `CanvasNodeViewModel` in `Loaded` and unsubscribed in `Unloaded`; `FlowCanvas`
  kept draw order (cards under members) by removing and re-inserting a view whenever the view model's order changed.
- Failed because: re-inserting in the same frame can deliver `Unloaded` after `Loaded`, so the view stayed
  unsubscribed and drew at its old place while hit tests, ports and edges used the new layout. Linking two pills in
  one column (the target moves a column right) left them drawn on top of each other and every later drag hit the
  wrong node; deleting one of two stacked cards left the other where it was, off its edge's end.
- Evidence: user's second-round report, 2026-11-02; `CanvasRelayoutTests` show the view model was right throughout.
- Retry only if: never — subscribe for the view's life (`Detach` on removal), order by `Canvas.ZIndex`.

## X-005 · 2026-11-02 · Listing Bluetooth devices from audio endpoints (ACTIVE | UNPLUGGED | NOTPRESENT)
- Tried: `dctl devices bluetooth` and the editor's pickers grouped audio endpoints by the address in their path.
- Failed because: Windows keeps the endpoints of devices that were unpaired, so SOUNDPEATS H3, Technics EAH-AZ40M2
  and WF-C710N showed as "not connected", while paired non-audio devices (MX Master 3S, MX Keys Mini, Steam
  controller, a phone) never appeared (user's report, 2026-11-02).
- Evidence: `dctl devices bluetooth` vs `Get-PnpDevice` `BTHENUM\DEV_*` / `BTHLE\DEV_*` on the dev PC, 2026-11-02.
- Retry only if: never as the list's source (D-052). Endpoints stay right for connecting and its state (D-023).

## X-006 · 2026-11-03 · "Tidy up" as a button that only reorders steps within their columns
- Tried: `Core/Layout/FlowTidy` (M7): barycentre sweeps that swap the `nodes` order inside each column to cut edge
  crossings; the CommandBar button applied it, and did nothing at all (no tip) when no order had fewer crossings.
- Failed because: the user's flows are chains and small forks with no crossings, and D-060's wrapped chains hold one
  box per column, so there is nothing to swap; the user never saw it change the canvas (2026-11-03).
- Evidence: PLAN M13 P7; `FlowCanvasViewModel.TidyUp` returns false and `ShellWindow.OnTidyUp` ignores it.
- Retry only if: never as the only layout command. It lives on as "Untangle lines" in the Layout menu, off with its
  reason when it can't help (D-067).

## X-007 · 2026-11-03 · Telling who holds a shortcut from `RegisterHotKey`'s error
- Tried: registering Win+[, Win+], Win+E, Win+L, Win+A, Win+1, Win+; Win+. Win+, Win+= on the dev PC (thread hotkeys,
  `hWnd` null, all unregistered afterwards), to tell Windows' own shortcuts from other apps'.
- Failed because: every one fails with the same 1409 (ERROR_HOTKEY_ALREADY_REGISTERED), whoever holds it, and no
  Win32 API maps a chord to its owner. Win+[ / Win+] were AutoHotkey (`SwitchDisplay.ahk`), found only by reading
  its scripts. Also: a chord another app catches with a keyboard hook (AutoHotkey `$#]::`) still registers for us,
  but never fires.
- Evidence: scratch spike `hk.ps1`, 2026-11-03 (Win+\, Win+-, Ctrl+Alt+[, Num keys registered fine).
- Retry only if: Windows adds an owner query. Until then, use a fixed list of Windows' shortcuts (D-073).

## X-008 · 2026-11-03 · A `KeyboardAccelerator` on a page-wide Grid with the default placement
- Tried: Ctrl+S as a `KeyboardAccelerator` on `ShellWindow`'s `FlowEditorPage` Grid (so it works while Save is still
  off), `KeyboardAcceleratorPlacementMode` left at Auto.
- Failed because: WinUI shows every accelerator's keys as an automatic tooltip of the element that owns it, so
  resting the pointer anywhere on the flow editor that has no tooltip of its own popped a "Ctrl+S" tip at the
  top centre of the page (user, v0.29.0: after a double-click, closing the search, clicking elsewhere).
- Evidence: screenshot of the 0.29.0 build, 2026-11-03: the tip centred over the page right after opening a flow;
  it is the only accelerator in the editor.
- Retry only if: never with Auto on a container. Set `KeyboardAcceleratorPlacementMode="Hidden"` there; a button's own
  tooltip says its keys ("Save your changes to this flow (Ctrl+S)").

## X-009 · 2026-11-03 · `SDC_USE_SUPPLIED_DISPLAY_CONFIG` with no modes to get a screen set's saved arrangement
- Tried: `CcdDisplays.UseOnly` (ported from scd): the chosen paths with `modeInfoIdx` invalid, no mode array,
  `USE_SUPPLIED | ALLOW_CHANGES | SAVE_TO_DATABASE`, expecting "the database restores this set's last layout".
- Failed because: with supplied paths and no modes Windows picks modes and positions itself; only
  `SDC_TOPOLOGY_SUPPLIED` looks the set up in its database. On the user's PC [INT-0, HDMI-0] came out with the laptop
  primary and the DELL on its right (their Extend has the DELL primary, laptop left); [HDMI-0] from Extend left the
  laptop on and duplicated (two paths, one source), and the step reported Succeeded.
- Evidence: live matrix 2026-11-03 20:45 (PLAN F5; scripts and log in the coordinator's notes folder `f5-display/`).
- Retry only if: never alone. Ask with `SDC_TOPOLOGY_SUPPLIED` first, give each path its own source, and check the
  result (D-077).

## X-010 · 2026-11-04 · A no-activate topmost window over a DXGI full-screen game (fullscreen optimisations on)
- Tried: spike S2 (T12.2, `spikes/menu-window`): the menu (`WS_POPUP`, `NOACTIVATE|TOOLWINDOW|TOPMOST`,
  `SW_SHOWNOACTIVATE`, `MA_NOACTIVATE`) over a D3D11 test app in flip model + `SetFullscreenState(TRUE)`, FSO on
  (`GameDVR_FSEBehaviorMode` 0, the PC's default); also layered, not topmost, and a 210×70 one in a corner.
- Failed because: ~100 ms after any of them shows, the app gets `WM_ACTIVATE 0`, the foreground goes to 0, DXGI
  leaves full screen and `Present` returns `DXGI_STATUS_OCCLUDED`: the game is kicked to the desktop. Same with FSO
  off (true exclusive). Hotkeys + mouse hook with no visible window leave it untouched. And
  `SHQueryUserNotificationState` says `QUNS_RUNNING_D3D_FULL_SCREEN` for FSO too, so it can't tell the two apart.
- Evidence: [flow-menu/SPIKES.md](flow-menu/SPIKES.md) S2 result, screenshots `flow-menu/s2/`, 2026-11-04.
- Retry only if: a documented overlay band without uiAccess appears, or with a game in borderless/windowed mode
  (works: S2 mode 1). Never show any window of ours while QUNS = 3.

## X-011 · 2026-11-04 · GameInput reading callbacks for idle-free controller input
- Tried: inbox `GameInput.dll` 0.2309.26100 (v0 API, `GameInputCreate` from an AOT probe), `RegisterDeviceCallback`
  then `RegisterReadingCallback(null, Gamepad, 0.1, …)`, hoping readings arrive pushed with no polling (D-056's
  first choice).
- Failed because: `RegisterReadingCallback` returns `E_NOTIMPL` (0x80004001) on PC; device callbacks register fine.
  Readings on PC are only `GetCurrentReading`, a poll like XInput, and it adds the GameInputSvc service.
- Evidence: T12.1 probe `spikes/GamepadProbe` (`gi` mode), 2026-11-04.
- Retry only if: a newer GameInput (v1+ redist or inbox) implements reading callbacks on desktop.

## X-012 · 2026-11-12 · Investigating a red `TriggerServiceTests`
- Tried: debugging failures of `TriggerServiceTests` on PRs that never touched triggers (timing-based tests that stall
  when another run holds the per-user pipe or the runner is slow).
- Result: they pass on rerun; the time went nowhere. Rule: if the only failed tests are `TriggerServiceTests`, run
  `pwsh scripts/pr-rerun.ps1 <pr>` and move on; a different failed test, or the same one failing twice in a row, is real.
- Revisit if: the flake is fixed (then drop `KnownFlaky` in `scripts/pr-lib.ps1`).

## X-013 · 2026-11-12 · A Style based on DefaultDropDownButtonStyle (implicit or keyed) kills the editor at start
Tried in D-121: implicit `Style TargetType="DropDownButton" BasedOn="{StaticResource DefaultDropDownButtonStyle}"` with MinHeight.
Every DropDownButton then fails to load: native stowed exception (0xc000027b in Microsoft.UI.Xaml.dll), the process exits at
once, nothing in editor.log, no window (dev-run looked like it did nothing). The key exists, so `XamlStyleKeyTests` passed; a keyed
copy applied by hand crashes the same. Found by bisecting App.xaml. Don't style DropDownButton; `XamlStyleKeyTests` forbids it.

## X-014 · 2026-11-12 · A saved value shown only when the live list offers it (editable ComboBox, Choice)
Check Wi-Fi's saved network sat in the YAML but the box showed "Optional" on reopen: the editable ComboBox's `Text` was bound
alone, the live network list arrived later (rows replaced) and WinUI empties an editable ComboBox's text when its rows are
replaced; a choice stored outside `Choices` had no row either. Rule: every picker must always have a row for the stored value
(appended as "Not available right now" / "(not available)"), selection bound to a view-model property that is re-read after each
rebuild, and opening never rewrites the file. `InspectorRoundTripTests` covers every step in `ProviderCatalog` (stored value not
offered, padded value, late lists). Also in the UX §5.9 acceptance list.

## X-015 · 2026-11-13 · `vpk download` that finds nothing, in a step marked `continue-on-error`
v0.54.0-rc.11 had no `-delta.nupkg` (rc.1–rc.10 did). The run log: `No releases found at 'https://github.com/dazewell/daze-control'` and "Aborting", exit 0, while rc.10 an hour earlier found the 0.53.0 packages; nothing in the repo had changed. A transient GitHub API answer, invisible because the run stayed green. Installed apps follow stable only and a full package always updates, so nothing broke; the stable after an RC would simply have been full-size. Fix: the step retries (4 tries, 15/30/45 s) until a `-full.nupkg` is on disk and warns when none arrives; `cut-release.ps1` reports a missing delta as a note, not MISSING.
