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
