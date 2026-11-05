# Architecture

How Daze Control is built now. Current state only; the why lives in [DECISIONS.md](DECISIONS.md).
Rewrite sections when things change (`knowledge-log` skill).

## Overview

```
 dctl.exe (CLI, AOT console) ──pipe──┐
 DazeControl.Editor.exe (WinUI 3, on demand) ──pipe (devices, test, run, live events)──┐
        └─ reads/writes config.yaml (atomic, .bak) ─────────┐                          │
                                                            ▼                          ▼
 DazeControl.exe  (engine, Native AOT, resident, no UI framework)
   message thread: TrayIcon · GlobalHotkey · RunPopup + FlowMenu (AcrylicSurface: Win32 + DWM backdrop + D2D/DirectWrite)
   ConfigHost: FileSystemWatcher (debounced) → YAML → validate → compile → atomic swap (last-good kept)
   FlowRunner (DAG scheduler) ─► ResourceLocks ─► IStepProvider implementations (lazy COM workers)
   PipeServer (current-user-only) · RunHistory · Log
```

## Solution layout

Props conventions copied from the OSD.
```
DazeControl.slnx · global.json · Directory.Build.props (TFM net10.0-windows10.0.26100.0, nullable,
warnings as errors, <Version>) · Directory.Packages.props · dotnet-tools.json (vpk)
src/DazeControl.Core/        pure, AOT/trim-safe, no interop: Model/ (Flow, Node, StepNode, GroupNode, Edge,
                             EdgeWhen, FailurePolicy, RetryPolicy, Library*), Yaml/ (DTOs, static context,
                             mapper), Validation/, Catalog/ (provider descriptors + ParamSpec), Layout/
                             (layered auto-arrange), Runs/ (outcome + event records)
src/DazeControl.Runtime/     FlowRunner, Scheduler, ResourceLocks, RunHistory, ConfigHost, TriggerRouter,
                             IStepProvider/StepContext contracts; TimeProvider everywhere
src/DazeControl.Providers/   Windows implementations (CsWin32 NativeMethods.txt, [LibraryImport] for
                             undocumented): Audio, Bluetooth, Display, Process, Script, Http, Android,
                             Service, Wait, Notify, Flag, Credentials
src/DazeControl.Ipc/         protocol records (source-gen JSON), PipeServer, PipeClient
src/DazeControl.Engine/      AOT WinExe: Program, Shell/ (TrayIcon, GlobalHotkey, AcrylicSurface, RunPopup, FlowMenu), Lifecycle/
                             (SingleInstance, Autostart), Diagnostics/Log, composition root (hand-written)
src/DazeControl.Cli/         AOT console exe dctl.exe (pipe client; offline `validate`/`list`)
src/DazeControl.Editor.ViewModels/  CommunityToolkit.Mvvm VMs, canvas model, undo stack — no WinUI refs
src/DazeControl.Editor/      WinUI 3 exe: ShellWindow, pages, FlowCanvas control, Inspector, styles
tests/ Core.Tests · Runtime.Tests · Providers.Tests (pure parts) · Editor.ViewModels.Tests
spikes/ (throwaway probes) · scripts/ (run, stop, measure, pack, install) · docs/ · .claude/skills/
```

### Reuse from daze-media-osd

Copy + adapt, cite the OSD D-id in a comment:
`Core/Tray/TrayIcon.cs` (D-009 Shell_NotifyIcon, dark menu), `Core/Input/GlobalHotkey.cs` + `Hotkey.cs` +
`App/HotkeyRecorder` (D-020..D-022), `Core/Diagnostics/Log.cs` + `LogRotation.cs` (D-028),
`Core/Lifecycle/Autostart.cs` (D-015), `Core/Settings/SettingsStore.cs` atomic-write pattern,
`Core/Audio/CoreAudioService.cs` MTA-worker pattern + `Interop/PolicyConfig.cs` (D-019) + the
KS one-shot Bluetooth reconnect (D-023, X-005), `Core/Windowing/OverlayWindow.cs` styles (D-008),
`App/SettingsWindow.xaml` card styles + `AlwaysActiveAcrylicBackdrop` lessons (X-007), Velopack
`scripts/pack.ps1` + `release.yml` + `UpdateService` (D-029..D-031), `tests/.../TestDoubles.cs`
(`ManualTime`). From scd: `Modules/Display/CcdApi.cs` + `DisplayOutputsModule` port-switch logic,
`FireTvModule.BuildSwitchInputShellCommand`, Sunshine sc.exe exit-code idempotency (1056/1062).

## Invariants (hard rules, mirrored in CLAUDE.md)

- Fail-soft: a bad flow / library item / provider init disables only itself; the engine always starts and
  keeps the last good config on a bad reload. No unobserved task (runner awaits all; global handlers log).
- Every step has a timeout and honours cancellation; processes are killed (tree) on timeout.
  A device write (SetDisplayConfig, a BtAudio request) checks the token right before it is sent: the runner has
  released the step's lock by then, so a late write would overwrite the next step's change.
- Idle = zero: no timers, no polling, no resident COM objects; waits are event-driven
  (IMMNotificationClient, device state, WM_DISPLAYCHANGE) with a timeout; COM workers start lazily and stop
  after 60 s idle.
- Budgets (measured by `scripts/measure.ps1`, gate in M0, M3, M10): engine idle ≤ 15 MB private / ≤ 35 MB
  working set, 0 ms CPU over 10 idle minutes, back within +2 MB after a run. Editor closed = not running.
- No admin, no injection, no focus stealing (pop-up never activates; editor activates only when opened).
- Secrets never in YAML or logs.

## Threading

Engine shell (M3): one hidden top-level window `DazeControl.Tray` (`Engine/Shell/MessageWindow`) on the
main STA thread; `TrayIcon` and `GlobalHotkey` add message handlers to it; static `[UnmanagedCallersOnly]`
WndProc (D-022). Other threads reach it only through `MessageWindow.Post(Action)` (queue + `WM_APP+100`):
config reloads and RunStarted/RunFinished are posted there; the tray and `HotkeyTable` are touched nowhere else.
Run pop-up (M5): node and run events are posted to the message thread in order and fed to the pure
`Runtime/Popup/RunPopupModel` (wording, tone, duration; the run that changed last wins), which `Shell/RunPopup`
draws (D-034). Its window class `DazeControl.Popup` is made on the first run; D2D/DirectWrite exist only while it
shows. Selecting a failure opens the editor (the run page comes with M9).

engine: one STA message thread (tray, hotkeys, pop-up); FlowRunner on the thread pool;
each COM provider owns one lazy MTA worker (OSD pattern); pipe server async on the pool. Editor: UI thread +
`DispatcherQueue.TryEnqueue` for pipe events.

## Config

`%APPDATA%\DazeControl\config.yaml`, schema `version: 1`; editor owns the file
(comments not preserved on editor save — accepted, user hand-edits still load); engine hot-reloads.
```yaml
version: 1
settings: { popup: { position: bottomCenter, seconds: 3 } }
library:
  audio:     { technics: { match: "Technics EAH-AZ80", flow: output } }
  bluetooth: { technics: { address: "B8:20:8E:ED:48:FC", name: "Technics EAH-AZ80" },
               sony:     { address: "80:99:E7:2A:AB:A1" } }
flows:
  connect-technics:
    name: Connect Technics
    hotkeys: [Alt+Z]
    whenRunning: ignore
    nodes:
      - { id: bt,  step: bluetooth.connect, with: { device: technics }, timeout: 20s }
      - { id: out, step: audio.setDefault,  with: { device: technics }, after: [bt] }
```
No coordinates: layout is computed (D-020); node order within a column is the order in `nodes`.
Edges are `after: [id | {id, when}]` on the target node; a card is
`{ id: g1, group: [hp, buds], after: [...] }` and its members carry no `after`. Node keys: `id`, `step` |
`group`, `name`, `with`, `after`, `onFailure` (stop/skip/ignore), `timeout`, `retry: { count, delay }`;
cards take no `with`/`timeout`/`retry`. A failed node with an outgoing `when: failure` edge has
`settings.stepDefaults` (D-080) holds per-step-type `timeout`, `retry`, `onFailure`; a node field left null inherits it (own > type > built-in, per field, `StepSettings.Resolve`; `StepDefaults.Apply` fills a flow). An explicit `onFailure: stop` is kept on save.
handled its failure: its `onFailure` policy is not applied, the fallback runs, its `success` dependents are
skipped, and if nothing else went wrong the flow ends Succeeded (D-024, D-065). `Model/FlowShape`: `Fallbacks`
(units with an incoming failure edge, or a success edge from a fallback; members follow their card),
`WithFallback` (units with an outgoing failure edge), `UnitOf`. Durations "500ms", "20s", "1m30s" (`Model/Duration`). Enums read
case-insensitively, written camelCase. Full example: `tests/DazeControl.Core.Tests/Fixtures/full.yaml`.

Pipeline (Core): `ConfigYaml.Load` → static-context DTOs (`Yaml/ConfigDocument`, all scalars strings, D-023;
`ParamValueConverter`/`AfterConverter` for the polymorphic bits) → `ConfigMapper` → `Model` records
(value equality via `ValueList`/`ValueDictionary`) + `Problem`s with YAML paths and flow ids.
`ConfigYaml.Save` maps back, omitting defaults. Then `ConfigValidator.Validate` (refs, cycles, card rules,
hotkey clashes, `with:` against `Catalog/ProviderCatalog` descriptors) adds more; `FlowsWithErrors` names the
flows that must not run as written. Problems with no flow id (library, settings) never disable a flow by
themselves: a bad library entry is dropped (mapper) or marked broken (validator), so a step that references it
fails its own flow instead. Hotkeys go only to enabled flows without errors; the later of two clashing flows fails.

## Runtime

`Runtime/Scheduling/Scheduler` (pure, D-025): one per run; `Start()` and `Complete(step, StepResult)` return
the steps to start next; `IsStopped` tells the runner to cancel what's running; `Outcomes` per node and
`Result` (`Outcomes.Summarize`). Units are steps outside cards and cards; a card hands out all its members at
once. Fallbacks (D-065): a fallback unit that would be skipped ends `NotNeeded` (fires `always` like Skipped); a
Failed/FailedIgnored unit with a failure edge is *handled*, and a skip whose dead edges all come from handled
failures (or such skips) is *excused*; `Summarize((outcome, handled))` is Succeeded when every node is
Succeeded, NotNeeded, handled or excused. `Outcomes.Fix(flow, results)` names the first Failed step (not may-fail: its success edges fired too) and the
first fallback step that started (`Fixes` also drives the Runs timeline's dashed bar), for #48 in the pop-up (`RunPopupModel`, whose "n of m" counts a fallback once it
starts), the Runs sentence (`RunWords.Sentence(run, title, flow)`) and the canvas InfoBar.
`Runtime/FlowRunner.RunAsync(flow, library, ct)` (one runner per engine) drives it: each step runs on the pool through its `IStepProvider`
(`Runtime/Providers`) with `1 + retry.count` attempts `retry.delay` apart, each with a timeout (node's, else the
descriptor's) on the injected `TimeProvider`. `WaitAsync` ends an attempt on time even if a provider ignores
its token (the abandoned task is observed). A stopped or cancelled flow cancels the running steps; the runner
awaits them all. Returns `RunResult` (flow outcome + `NodeResult` per node: attempts, error text, duration).
`ResourceLocks` (one per engine, shared by all runners): around each attempt the runner takes the descriptor's
`Resources` with `{param}` filled in (a Bluetooth ref becomes the device's address, so aliases share a lock), in ordinal order (no deadlocks). Waiting counts
toward the attempt's timeout. The lock is let go when the attempt ends, even if a timed-out provider is
still running in the background.
`FlowLauncher.TriggerAsync(flow, library)` is the one entry for triggers: one slot per flow id applies `whenRunning`
(ignore → null result; queue → one coalesced run afterwards; restart → cancel, wait for it to wind down, run the
latest version, each with the library captured at its trigger). `CancelAllAsync` drops queued runs, ignores
later triggers and awaits running ones (engine exit).
Events (`Core/Runs/RunEvents`): the runner raises RunStarted, NodeStarted (per attempt), NodeFinished (every
node, skipped and cancelled too) and RunFinished through an `Action<RunEvent>` sink; a throwing listener is
ignored. `RunHistory` keeps the last 100 `RunRecord`s and appends them to `history.jsonl` (STJ source-gen
`HistoryJson`, camelCase, string enums), skipping corrupt lines. Once the file holds 200 lines it is rewritten
atomically back to the last 100. The launcher records each finished run with its trigger text.
`ConfigHost` owns config.yaml: load → validate → per flow the file's version if valid, else the last valid
one (D-021, `FlowState.IsStale`), else disabled. Each `FlowState` carries the `Library` its version was validated
with (runs get that one) and `LastValid` as written; a retained version runs without any hotkey a current flow
took (warning) and gets it back once it's free. An unreadable file changes only the problems; a missing file is
empty; a watcher that can't start leaves loading on (warning). It reloads 300 ms after the last change
(`IFileChangeSource`, FileSystemWatcher on the folder) and raises `Changed` with a `ConfigSnapshot`
(`RunnableFlows` = valid and enabled) in reload order; a throwing handler goes to `ListenerFailed`.
Flow calls (D-061..D-063, T13.8): `flow.run` with `wait` on (default) never reaches a provider: the runner sees
`FlowRunner.Flows` (`IFlowStarter`, the engine's `Runtime/Control/FlowCalls` over `ConfigHost.Current`) and runs the
called flow's runnable version nested per attempt (`RunNestedAsync`: own Scheduler and RunId, events carry
`ParentRunId`/`ParentNodeId`, no launcher slot so `whenRunning` doesn't apply, no lock held by the call step; its
steps lock as at top level). Outcome mapping: Succeeded/Partial → Succeeded (Partial sets
`Scheduler.MarkPartialCall`, so the caller ends PartiallySucceeded at best, no failure edge); Failed → Failed "X didn't
finish: <its first failure>" (#56); timeout → #57 and the nested run is cancelled and awaited; ended quietly →
Cancelled and the caller cancels too. `NodeResult.Called` (`CalledRun`, recursive) keeps the nested results in the one
history record. Every run carries its call chain (flow ids from the top); `FlowCalls.Guard` fails a call whose flow is
on it (#53) or past 8 deep (#54). `wait: false` goes to `FlowProvider`, which starts the flow through the launcher with
trigger "Flow · <caller>" and passes the chain on (`StepContext.Chain` → `TriggerAsync(…, chain)`); triggers by a person
start a fresh chain. With `Flows` null (the dry runner) a call is an ordinary step: dry runs don't descend.
`ConfigValidator.CheckCalls` builds the call graph from every `flow.run` (both modes): own flow #49, a loop #50 on every
step in it, unknown #51 (errors); turned off #52, called flow with errors (warnings).
Menu core (T12.3, DESIGN flow-menu §2.5/§3.5): `menu.show` is a catalog step (`items` TextList with `RefersTo = Flow`, so
Menu runtime (T12.4, flow-menu DESIGN §3.2): `Providers/MenuProvider` shows the menu through `IMenuPresenter` (Engine fills it; wrap it in `GatedMenuPresenter` so one menu is on screen and the gate is held only around `ShowAsync`) and launches the pick with `IFlowStarter.Start` (trigger "Menu · <flow>", fresh chain, so menus may point at each other); `wait` runs it nested through `StepContext.RunNested` (the runner's `CallAsync` with the step's own token). Contract: `StepContext.SetSummary` → `NodeResult.Summary`; a provider throwing `StepEndsFlowException` makes its node Cancelled with the message, cancels the run and sets `RunResult.Quiet` / `RunFinished.Quiet` (`RunPopupModel` shows nothing; a flow of only `menu.show` steps has no pop-up). A test-step (flow id `test:`) shows the menu and runs nothing; the dry run says "Would show N choices". `IFlowStarter.IsRunning` and `CalledFlow.Why` feed the captions.
Menu window (T12.7, DESIGN flow-menu §3.3/§4): `Shell/AcrylicSurface` is the abstract window both `RunPopup` and `Shell/FlowMenu` stand on (WS_POPUP + NOACTIVATE/TOOLWINDOW/TOPMOST/LAYERED, transient-window backdrop, software D2D HWND target, d2d1/DWrite loaded only while shown, layered-alpha fades, `WM_MOUSEACTIVATE` → no activate; a static hwnd → surface map feeds one `WndProc`). `FlowMenu` is the engine's `IMenuPresenter`: `ShowAsync` posts to the message thread, builds a `MenuModel`, lays out list/grid in DIPs (scrolls under work-area height − 48, focus kept in view with a row of context), and draws focus fill (slides 120 ms), two-tone ring (3-DIP system colours in high contrast), hint row (pad badges / "Enter Run · Esc Close" / none for mouse), "Closing in N s" for the last 5 s (one timer, then 1 s ticks, nothing otherwise), the empty state and the shake for a choice that is off. `Shell/MenuKeys` registers Up Down Left Right Tab Shift+Tab Home End Enter Space Esc 1–9 (`Runtime/Menus/MenuKeyMap`) only while open; `MenuMouseHook` is a pass-through `WH_MOUSE_LL` only while open (press outside → cancel, wheel over → scroll). Every close path (pick, cancel, timeout, step cancel, engine exit) runs `Teardown` (keys, hook, pad feed, model). Placement: monitor of the foreground window else the cursor, work area (whole monitor under a covering borderless window), centred. Presenter chain in `EngineHost`: `GatedMenuPresenter(FullScreenGuardPresenter(FlowMenu, FullScreen.OverGame))`; the guard asks `SHQueryUserNotificationState` (QUNS 3, FSO too) before anything is drawn: the step fails with UX #32 and one log line (D-055, X-010). `IMenuPadFeed` is `Runtime/Menus/GamepadMenuFeed` (a 60 Hz `GamepadService` listener while open; B and A act on a press seen after the buttons were up, T12.6). A quiet run finish hides the pop-up (`EngineHost.ShowPopup`: `Apply` true with no `Current` → `Hide`). Not built: open scale 0.96→1, tile scale 1.03 and the pick flash (motion polish), Narrator announcements (T12.9).
`FlowRefs.Callers/Rename` also read list items); A v0.40.0 `gamepad:` key is read into `FlowDocument` only to give one warning (D-086). `CheckMenus`: own flow
#38 error, unknown/duplicate items warnings, 0 or >12 items and `closeAfter` outside 5 s..10 min errors, explicit
`timeout` must exceed `closeAfter`; `CallEdges` adds `wait: true` menu items to the call graph (#39 = the loop error).
Editor for calls and inputs (T13.10, `Editor.ViewModels/Inspector`): flow settings list `Flow.Inputs` as
`FlowInputViewModel` rows (label, kind, default picker); the key is made once from the kind (`FlowInputs.NewName`) and
never changes, so renaming can't break a `$name`. Removing an input, or changing its kind, writes its default into the
steps first (`FlowInputs.Detach`, one undo step, tip #61). Device pickers get a "This flow's inputs" group
(`DeviceSource.Input` rows store `$name`; `DeviceSource.Header` rows are never picked). A `flow.run` step's `inputs`
param renders as `CallInputViewModel` pickers per input of the called flow ("Its default: …", the caller's own inputs
of that kind, then the Library); picking another flow drops entries it doesn't take. The flow picker lists its own flow
and loop-making ones off with `ConfigValidator.CallProblem` (#49/#50). Pill: called flow's glyph, "Run <name>", passed
devices or "Doesn't wait". Live: `RunOverlayViewModel` tracks nested runs from `ParentRunId` (the call's pill shows the
nested current step, #59; a flow running inside another says so, #64). Runs page: a call bar's chevron
(`RunDetailViewModel.ToggleCall`) adds the nested lanes under a bracket (ids are the call and step joined by U+001F; one level, deeper calls stay
bars), and "Open in editor" on a nested bar opens the called flow with that nested run.
Flow inputs (D-064, T13.9): `Flow.Inputs` (name → `FlowInput(kind, label, default)`, YAML `inputs:`); a `LibraryRef`
setting holds `$name`. `FlowInputs.Apply` runs first in `RunCoreAsync` and replaces every `$name` (step settings and the
`inputs` map its own calls pass) with the caller's value, else the default, so steps, `ResourcesOf` locks and nested
calls only see Library keys (two calls with one device share its lock). A call checks what it passes against the
called flow's library first (`FlowInputs.Check`: "Output (from Headphones: sony) isn't in your Library anymore.");
start mode applies the inputs before `TriggerAsync`. Validation: input names `[a-z][a-z0-9-]*`, default required and in
the library; `$name` unknown / other kind / a flow's input outside a device setting; `CheckPassedInputs` for calls
(unknown input, key not in the library, pass-through of the caller's input of the same kind). `LibraryEdits.Rename`
rewrites defaults and passed values; `LibraryEdits.Uses` (Library "Used by", Remove) counts both.
Tests: `FakeProvider` + thread-safe `ManualTime` with `WaitForTimerAsync` (Runtime.Tests); flow calls end to end on
a scratch config: Providers.Tests `FlowCallScenarioTests`.

## Engine shell

`Engine/Program`: rolling log (`engine.log`, `Runtime/Diagnostics/LogRotation`, 7 days), handlers for unhandled
and unobserved exceptions (log only), `Lifecycle/SingleInstance` (mutex `Global\DazeControl.Engine-<user SID>`, taken
before the log: a second engine of the same user, in any session, exits 0 without writing anything, D-028), then `EngineHost`, the hand-written composition root: ConfigHost
(`%APPDATA%\DazeControl\config.yaml`, or argv[0]) · RunHistory (`%LOCALAPPDATA%\DazeControl\history.jsonl`) ·
one FlowRunner + ResourceLocks + FlowLauncher · `Runtime/Control/EngineController` (D-026) · PipeServer · tray ·
hotkeys. Exit (tray or WM_CLOSE): unregister hotkeys, pipe stops accepting (no new clients or requests), cancel
all runs, drain the pipe (waiting `run --wait` clients get their answers), close it; ≤ 5 s grace in all.
`FlowLauncher.RunCompleted` (after history + slot) is what publishes `runFinished` (pipe, tray, log), not the
runner's RunFinished, so a client reacting to it sees the run in history and the flow free (or running its queued
run, which starts only after that event, so a flow's completions arrive in order). Autostart keeps a config path
given on the command line as the Run value's argument.
- `Runtime/Triggers/HotkeyTable`: chord → flow from `RunnableFlows`, applied as a diff on each reload (unchanged
  chords stay registered), first flow wins a chord, a chord that fails (another app) fails alone and is retried
  on the next reload and on `RetryFailures` (tray menu opens), Resume and the end of recording (D-072, no timer);
  Pause and `Recording` (pipe `record-hotkey`, the editor's shortcut box has focus) each unregister all; failures
  stay known while either is on. `FailuresChanged` → `ShellStatus` + a `shortcutsChanged` pipe event. Failures → `status` ("Alt+Z: Another app uses…").
  Keys (`Core/Input/Hotkey`, T13b.1, D-071): A–Z, 0–9, F1–F24, punctuation (VK_OEM_*, US names in YAML) and
  Num0–Num9 (alone allowed, no Shift, Num Lock on); `Hotkey.Check` → `HotkeyRejection` for the recorder;
  `Core/Input/WindowsShortcuts` lists Windows 11's own Win chords: 1409 on one → H5 "Windows uses…", else H6
  (`HotkeyTable.TakenMessage`, D-073). Still `RegisterHotKey` only: no keyboard hook.
- `Runtime/Tray/TrayMenu`: the menu as data (UX §5.6), rendered by `Engine/Shell/TrayIcon` each time it opens
  (left or right click), dark/light like the taskbar (uxtheme #135/#136). Icon drawn in code by
  `Shell/TrayGlyph` (idle / busy / paused, D-027); tooltip = running flows, else paused, else the last run's
  problem. Not yet: per-flow glyph bitmaps in the menu (need M5's glyph-font rendering).
- `Runtime/Lifecycle/Autostart`: HKCU Run value `DazeControl` → engine exe (OSD port), set through the pipe op.
- Desktop shortcuts (M11, D-049): `Runtime/Lifecycle/EngineArguments` parses `[--run <flow id>] [config]` before the
  single-instance check; a second engine sends `run` (`exactId`, trigger "Desktop shortcut") through `PipeClient`
  and exits without logging; the first runs it right after the constructor's (synchronous) first config load. The
  rules live in `Runtime/Lifecycle/DesktopShortcuts` (scan = our target + `--run` + same config; names via
  `ShortcutNames`; `IconFile` writes the .ico) behind two seams that `Engine/Shell/FlowShortcuts` fills: `ShellLinkStore`
  (IShellLinkW + IPersistFile, each call on a fresh MTA thread) and `FlowIconRenderer` (glyph coverage from a software
  D2D DC target over `TrayGlyph.PaintTile`; d2d1/DWrite loaded and freed per call). Target = the Velopack stub when
  `current\..\Update.exe` and `Daze Control.exe` exist, else this exe. `EngineController.RunRefused` → "Didn't start"
  pop-up (#24 for a gone flow). The uninstall hook calls `RemoveAll` (any config, plus the icon folder). Editor:
  `Engine/DesktopShortcutStore`, shared by the Flows card menu and the flow settings row.

Controller input (T12.6, D-056, D-086): `Runtime/Triggers/GamepadService` ref-counts listeners (only an open menu, via `GamepadMenuFeed`) and starts the `IPadBackend` on the first, halts it on the last; none → `xinput1_4` is never loaded and no thread exists. `Engine/Input/GamepadSource` is the backend: one thread running the pure `PadPoller` at 60 Hz (scan once, poll only connected slots, merge pads, LT/RT hysteresis; no pad → `Step` returns null and the thread waits on an event), `DeviceArrival` (CM_Register_Notification for XUSB and `IG_` HID nodes, registered only while the source runs) wakes it for a rescan. Readings go to `MenuModel`; the menu's "released since open" rule is in the model. `devices gamepads` enumerates once ("Controller N", battery) and never starts the reader. No chord trigger, no suspend/resume wiring (D-086).

## Install and updates

D-043. `scripts/pack.ps1` → one folder (editor self-contained + AOT `DazeControl.exe` + AOT `dctl.exe`, no .pdb) →
`vpk pack` (packId `DazeControlApp`, installs to `%LOCALAPPDATA%\DazeControlApp\current`, Start-menu shortcut).
`scripts/install.ps1` packs and installs this checkout the same way; `release.yml` publishes on a `v<Version>` tag.
- `Core/Updates/VelopackUpdateClient` (GitHub Releases, stable, no token; `DAZECONTROL_UPDATE_SOURCE` = local feed):
  shared by the three exes; sets Velopack's locator itself in the editor and dctl (only the engine runs the hooks).
- `Engine/Lifecycle/Installer`: Velopack hooks before the single-instance check and the log (a hook runs while the
  real engine runs). `Program` then applies a downloaded update (exit; the updater restarts the engine with the same
  args). `Engine/Lifecycle/UpdateCheck`: installed copies only, runs `dctl update` hidden at +1 min and daily, logs
  its output.
- `dctl update`: find + download, never apply; exit 0 / 1. Editor: `UpdatesViewModel` (Settings › Updates card).

## IPC

`\\.\pipe\DazeControl-<user SID>`, `PipeOptions.CurrentUserOnly`, newline-delimited JSON (source-gen
`Ipc/IpcJson`, camelCase, string enums, nulls omitted), `protocol: 1`. `Ipc/PipeServer`: any number of clients;
requests on one connection run concurrently, answers carry the request id; after `subscribe` the connection also
gets events (`PipeMessage.Event`, id 0: runStarted, nodeStarted, nodeFinished, runFinished, configReloaded, shortcutsChanged). A
client that goes away cancels its waiting requests. Writes go through a bounded queue per connection
(`QueueLimit` 256 lines) drained by one write loop; a line that can't be queued drops that client (logged).
Requests are read by a bounded line reader (`MaxLineBytes` 64 KB, longer → client dropped); more than
`MaxPendingRequests` (16) unanswered on one connection are refused. Pending pipe I/O ends only by cancellation
(X-001). `Ipc/PipeClient`: one request at a time; events read while waiting for an
answer go to `EventReceived`, or (no handler) a queue `ReadAsync` returns first.
`cancel` and `status` use `FlowLauncher.RunningFlowIds` (a run outlives its flow's removal from the config);
`history <text>` matches a recorded flow id, then the config's id/name, then a recorded name.
Ops (EngineController): `run` (flow id or name; `wait` → the RunRecord; `dryRun` → a runner whose steps do
nothing, no history), `cancel`, `list`, `status`, `validate`, `history` (flow, limit), `autostart` (enable
true/false/absent), `shortcuts` / `create-shortcut` / `remove-shortcut` (flow id, M11, D-049), `devices` (kind; M11 adds
`services`, `credentials` — names only, never secrets — and `DeviceInfo.Value`/`State`, D-051), `devices gamepads` (answered by `GamepadService`, not `IDeviceQueries`), `record-hotkey` (`on`;
per connection, ends when the connection does: the editor sends it on the persistent request connection, D-072) and `capture-display-layout` (key → a `library: displays:` YAML snippet)
through `Runtime/Control/IDeviceQueries` (read-only; `Providers/SystemDevices` on the real PC); `test-step`
answers "can't do that yet" until the editor. `PipeClient` copies each request to stamp its id: a new
`PipeRequest` field goes into that copy too (`PipeTests.EveryRequestFieldReachesTheServer` fails otherwise).
`dctl` (`src/DazeControl.Cli`): run/cancel/list/validate/status/history/autostart/devices/capture-display-layout/shortcut/update, parsed strictly by
`Ipc/CommandLine` (unknown flag or wrong word count → exit 2, never ignored); `validate <file>`
and, with no engine, `list`/`validate` read the YAML themselves. Exit codes 0 ok, 1 failed, 2 usage, 3 no engine.

## Editor

`DazeControl.Editor.exe` (WinUI 3, unpackaged, self-contained) runs only while its window is open; closing it
ends the process. Single instance: a named mutex; a second start sets a named event and exits, and the first
brings its window to the front. Pages (UX §5.1) are panels in one `ShellWindow` (Flows, Library, Settings,
About), cards in the OSD's settings-card style (App.xaml). Leaving the open flow (breadcrumb, any nav item, closing)
goes through `LeaveFlowAsync` (the D-047 prompt) then `CloseFlow`; nav Flows while a flow is open is still the
selected item, so `ItemInvoked` handles it (`SelectionChanged` doesn't fire). All logic is in `Editor.ViewModels`:
- `Config/ConfigStore` — config.yaml I/O: atomic write (temp + `File.Move` overwrite), one backup per editor
  session in `%APPDATA%\DazeControl\backups` (newest 10), and the text last read/written to tell outside changes.
- `Config/EditorSettingsStore` — this PC's view state in `%LOCALAPPDATA%\DazeControl\editor.json` (D-068; with
  `--config`, `editor.json` next to that file): STJ source-generated, read once (missing → defaults; unreadable or
  bad → defaults and a line in `logs\editor.log`), `Update(s => …)` writes it atomically 300 ms after the last change
  (`TimeProvider`) and on close; unknown keys are kept. Holds `NavPaneOpen` (`ShellViewModel.IsNavPaneOpen`, two-way
  with `NavigationView.IsPaneOpen`) and `Flows` by flow id: `FlowViewSettings` = the Layout menu's choices
  (`WrapAfter`, `Spacing`, `Align`, `Lines`; null = default, an all-default flow is removed), `ToOptions()` →
  `LayoutOptions`. `RenameFlow` on a saved rename (D-075), called by the open canvas and by the shell.
- `Config/ConfigSession` — Library/Settings edits are `Update(c => …)`, saved 300 ms after the last one
  (`TimeProvider`); the open flow's edits are `UpdateDraft` until `SaveDraft`/`DiscardDraft` (D-047): it keeps the
  saved configuration beside `Current` and writes only that.
  A file that can't be read (bad YAML, newer schema) is never written (`CanEdit` false). An outside change
  (window's FileSystemWatcher, debounced) reloads when nothing is unsaved, else waits: InfoBar Keep mine / Reload.
- `Engine/EngineClient` — one subscribed pipe connection that also tells whether the engine runs (retry every
  3 s while open) and one on-demand request connection; requests answer null when it isn't running.
- `ShellViewModel`, `FlowsPageViewModel` (cards synced in place so a toggle keeps focus; last run from
  `history`, refreshed on `runFinished`; glyph `FlowGlyph.CodeOf`; the card's `RunCommand` sends `run` (trigger
  "Editor") or `cancel`, "Stop" while a `runStarted`/`nodeStarted` of that flow has no `runFinished`, cleared when the
  engine goes; the view's `FlowCard` control is the card's Tab stop and UIA Invoke, D-070), `SettingsPageViewModel` (autostart through the engine, pop-up in config).
- Flow editor (M7): Flows › name opens `Canvas/FlowCanvasViewModel` for one flow. Layout is `Core/Layout/FlowLayout`
  (D-020): layer = longest path from a start, rows = `nodes` order, cards stack members; edges naming a member
  are drawn from/to its card; cycles and dangling ids are ignored so half-built flows still draw. Spine runs longer
  than `LayoutOptions.WrapAfter` wrap into rows (D-060; 0 = never), and columns are placed in **lines** like text
  (D-066): a line is the columns up to and including the next wrapped block, centred on its own tallest column (or
  hung from the line's top, `Align = Top`); the line after a
  block starts at the block's x, one `WrapRowGap` under its bottom, so later lines never reach left of an earlier
  block and the width stays bounded by the blocks. Wrapped boxes keep their layer as `Column`, so keys, Untangle and
  `nodes` don't see the wrap. `EdgeCurve` is the Bézier + hit-test, or orthogonal (`IsOrthogonal`): the wrapped edge
  (`EdgeRoute.WrapLaneY` set on a row break and on the edge from a block's last box into the next line), or with
  Lines › Right angles every other edge (`BendX` = the gap's middle); `EdgeCurve.For(route, lines)` picks.
  Layout menu (D-067): `LayoutOptions.Of(wrapAfter, spacing, align, lines)` (Compact 40/8, Normal 64/16, Roomy
  96/32 column/row gaps). The canvas VM reads this flow's `FlowViewSettings` at every `Sync` (`SetLayout`,
  `ResetLayout`: not flow edits, no undo); `ShellWindow` builds the flyout on `Opening` (radio items from the VM) and
  gives the canvas focus back after a pick, so Ctrl+Z undoes Untangle. Choices apply at once without the reflow
  animation (UX §5.3 motion's 200 ms reflow isn't built anywhere yet). The VM owns selection, roving focus, reorder (`MoveToRow` rewrites `nodes`/`group` order), fit
  and semantic zoom; every edit is `Edit(flow => …)`. The WinUI `Canvas/FlowCanvas` only draws (code-built pills,
  edges batched by style, D-037) and maps pointer/keys to VM calls; zoom/scroll is a `ScrollViewer` (50–200 %).
  Wheel zoom and pan room (D-074): the `ScrollViewer`'s content is a transparent `Wrapper` Grid around `Host`, padded
  per side by viewport − 48 DIP (screen) less the layout's 48-DIP margin, in content DIP at the current zoom, so any
  flow can be moved and `Host`'s coordinates never change. `Canvas/CanvasViewport` (pure, tested) holds the maths:
  pad, offsets that keep a point still across a zoom (the padding changes with it), Fit centred. `Wrapper`'s
  `PointerWheelChanged` (mouse only, handled) zooms at the pointer and does Shift+wheel sideways itself (the WinUI 3
  `ScrollViewer` scrolls it vertically); touchpad gestures stay the `ScrollViewer`'s, re-padded on
  `DirectManipulationCompleted`. A re-pad inside a layout pass clamps `ChangeView` to the old extent, so the canvas
  asks again at low priority once layout is done.
  Right-drag pan (D-069): `Canvas/RightDragPan` (pure, tested) arms on a mouse right press, pans once the pointer
  leaves the system drag rectangle (`GetSystemMetricsForDpi(SM_CX/CYDRAG)` at the XAML root's DPI, in DIP), and
  swallows the one `RightTapped` after a pan. The canvas captures only once panning, so a still click's `RightTapped`
  still arrives; an armed press is dropped on `PointerExited` (no capture = no release outside).
- Canvas edits live in Core: `FlowGraph` (connect/disconnect/`when`, refusals: exists, loop, same card) and
  `FlowEdits` (group/ungroup, take out — a card under 2 members dissolves —, run together/after on drop).
  `FlowTidy` (Layout › "Untangle lines", was Tidy up, X-006) cuts crossings with barycentre sweeps; nodes only
  trade the `nodes` slots their column already holds, so the YAML diff stays small. `FlowTidy.Check` → `UntangleState`
  (Helps / NoCrossings / NoBetterOrder) sets `CanUntangle` and the reason shown on the off item; the VM works it out
  when the menu asks, cached per flow version and Line up (the sweeps are quadratic in edges, too dear for every Sync). `FlowCommands` (add, insert on an edge, delete with bridging, copy,
  paste) backs the edit commands (D-039).
- Flow editor commands: undo/redo are whole-flow snapshots in the canvas VM (D-039). Palette and quick-add
  (`/`, double-click, the empty state's button, "Add a step…" menus) share `StepPaletteViewModel.Search`; the palette
  drags into the canvas with OLE drag-drop (text `daze-step:<id>`), the only drag that crosses controls. The
  Outline view (`FlowCanvasViewModel.Outline`, a TreeView) is the screen-reader equivalent: same selection (one row:
  showing it narrows a canvas multi-selection to the focused step, since TreeView multi-select didn't redraw its check
  boxes from code reliably), same shortcuts (`FlowCanvas.RunCommandKey`) and the same command menu (`FlowCanvas.NodeMenu`).
- Inspector (`Inspector/InspectorViewModel`, pane 320 wide right of the canvas): one selected step → header, one
  `ParamFieldViewModel` per `ParamSpec` (text, script, file + Browse, number/duration in seconds, toggle, choice,
  device picker, lists and maps as lines; M11 from `Suggest`/`Ordered`/`Examples`, D-050: editable ComboBox of
  `SuggestionItem`s, screens checklist `ScreenChoices` sharing `Core/Layout/ScreenMatch` with the provider (D-051),
  key tokens, an "Examples" menu; an editable ComboBox writes its text back on focus, so `WriteParam` drops writes
  that change nothing), "If it fails" radio buttons whose hover/focus fades what wouldn't run
  (`Affected`, via `FlowCanvasViewModel.Preview`), timing, "Test this step" (D-040); a card → name + policy; nothing
  → the flow's settings (name, icon, tray folder, line above, shortcuts with the OSD's `HotkeyRecorder`, when
  running, pop-up). The recorder's `Rejected` carries a `HotkeyRejection` (Num Lock off = a pad navigation key that
  isn't extended; H7 = Win/Ctrl/Alt let go with no key); `ShortcutViewModel.Note` shows its text
  (`ShortcutTexts`, UX §5.7) over the engine's problem until the next recorded key, and the recorder announces it
  (UIA notification); `Tip` is H8 for a number-pad key alone. Every change is one canvas `Edit` (one undo step); text commits on leaving the box or Enter;
  a value equal to the default is removed from `with:`. Device pickers (`Inspector/LibraryDevices`, shared with the
  Library page): Library items with live state from pipe `devices`, a missing key kept as "Not found on this PC",
  then "From Windows" devices — picking one adds it to the Library under a key made from its name; displays offer
  "Capture current layout". The list refreshes when a picker opens.
  M11 (D-050): `ParamSpec.Suggest`/`Examples` add an editable ComboBox (Text), a checklist or ordered token list
  (TextList) and an "Examples" menu (MultilineText/TextMap); `Devices(kind)` sources share `LibraryDevices`' pipe cache,
  `Config(kind)` sources read `ConfigSession.Current` (the draft). Bluetooth devices without audio are shown disabled.
  A param with `RefersTo = Flow` (`flow.run`'s `flow`, F2) is a device-style picker, not an editable box:
  `InspectorViewModel.FlowOptions` lists the other flows by name and glyph ("Turned off", "Needs fixing"), stores the
  id, keeps an unknown id as its own row ("Isn't there anymore"), and #69 shows when there's none; the canvas card's
  summary shows the called flow's name. A valid reference never shows its id (ids are trimmed as the validator
  does); only a target that's gone shows the id it names, so the step doesn't change by itself.
- Library page (`LibraryPage/LibraryPageViewModel`, view `Library/LibraryView`, a Pivot): Audio, Bluetooth, Displays,
  Android from `library:` plus Programs (the `process.run` paths the flows use). Cards show live state (pipe
  `devices`, refreshed when the page opens or the engine starts), "Used by n flows" (flows with a step whose
  LibraryRef param names the key), the item's Library problem, and a read-only Test (D-041). "Add from Windows" and
  "Capture current layout" reuse `LibraryDevices`; Remove only when unused. "Used by" lists the flows (`FlowLink`) and
  raises `FlowRequested`, which the shell opens. Rename (layouts, Android) = Core `LibraryEdits.Rename`, which rewrites
  every LibraryRef naming the key; a layout's Details = Core `LayoutScreens.Read`, which reads the capture's CCD bytes by
  offset (a Providers test pins them to the CsWin32 structs), with name and port from the live list (D-046).
- Problems (n) (`FlowCanvasViewModel.Problems`, `ProblemItem`): the flow's problems from `ConfigSession.Problems`,
  errors first, in canvas order; picking one selects its step (the inspector follows) or, for the flow's own
  settings, clears the selection.
- Runs on the canvas (`Runs/RunOverlayViewModel`, UX §5.5): Run / Dry run / Stop in the flow editor's CommandBar.
  A live run of the open flow (any trigger) comes from pipe events: per-step `NodeRunView` (state, text, start
  order) through `FlowCanvasViewModel.ShowRun`, re-applied after every re-layout; edges from a finished step turn
  accent (`IsLit`). While it runs `FlowCanvasViewModel.IsRunLocked` makes `CanEdit` false and `Edit` a no-op
  (inspector included), with an InfoBar; the engine going away unlocks it. A finished, dry (D-042) or recorded run
  stays on the canvas until its InfoBar is closed. Pills draw states as ring fill + glyph (`PillView.ShowRunState`);
  the ProgressRing is created on first use, and with animations off a static ¾ ring replaces it.
- Runs page (`Runs/RunsPageViewModel`, `RunDetailViewModel`; view `Editor/Runs/RunsView`, UX §5.5): the engine's
  `history` grouped by day, refreshed on open and after each runFinished. The detail's timeline is laid out by
  `RunTimeline` (greedy lanes) + `RunDetailViewModel.Layout(width)` in DIP (min bar 4, instant steps in a lane
  nudged 2 DIP apart) and drawn in code; bars are templated Buttons (focus, Invoke, UIA name), each bar has an
  outcome glyph when ≥ 20 DIP so contrast themes don't rely on colour. A bar's "log lines" are its outcome, tries
  and error from the run record: the engine log isn't split per run. "Open in editor" = `ShellViewModel.OpenRun`.
- `--config <path>` makes the editor edit another file (tests, screenshots); its backups go to `backups/<file>/`
  next to it, and its single-instance names are scoped by path.

## Adding a provider

1. Add a descriptor in `Core/Catalog/` (id, glyph, title, params as `ParamSpec`, default timeout, resources).
2. Implement `IStepProvider` in `Providers/<Area>/` (`ValidateAsync`, `IsAlreadyDoneAsync?`, `ExecuteAsync(ctx,
   params, ct)`, optional `IEnumerator` for pickers); interop behind a small interface with a fake.
3. Register in the engine composition root. A test asserts every descriptor has exactly one implementation.
4. Tests: descriptor/param validation, YAML round-trip of a sample node, runner behaviour with the fake.
No change to Runner, YAML mapper or editor: the inspector renders from `ParamSpec`. A free-text param should say
where its values come from (`Suggest`) or show `Examples` (D-050); a `Devices(kind)` source needs a kind in
`IDeviceQueries` (`Providers/SystemDevices`), a `Static` table lives in Core and is shared with the provider.

As built (M4): the composition root is `Providers/BuiltInProviders.Create`. Params are read with `StepParams`
(written value, else the descriptor default). COM providers (Audio, Bluetooth) use CsWin32 without marshalling
and run each call on a short-lived MTA thread (`ComThread`), which turns a failed HRESULT into a step failure;
waits for a device use a hand-made IMMNotificationClient vtable (`CoreAudioEndpoints.DeviceChangeClient`).
`devices bluetooth` (D-052): `Bluetooth/PairedBluetooth` reads present `BTHENUM`/`BTHLE` devnodes through CfgMgr32
(`LibraryImport`, seam `IPairedBluetooth`); `BluetoothList.Merge` adds the endpoints' Connected and `NotConnectable`.

## Footprint (measured by `scripts/measure.ps1`)

Budget: engine idle ≤ 15 MB private, ≤ 35 MB working set, 0 ms CPU over 10 idle minutes, back within +2 MB after a run
(+4.5 MB once the run pop-up has shown: its first showing costs ~+2.5 MB once, flat after that, D-036).

| Date | Build | What ran | Private MB | Working set MB | CPU ms (window) |
|---|---|---|---|---|---|
| 2026-10-31 | d7f8006 (T0.5 spike, Release AOT) | tray icon + 1 hotkey + YamlDotNet static load, 3 threads, exe 3.3 MB | 3.8 | 15.2 | 0 (60 s) |
| 2026-10-31 | d7f8006 (same) | same, 10-min idle; 5 threads | 3.9 | 15.5 | 0 (600 s) |

| 2026-11-01 | 1fbc856 (M3 engine shell, Release AOT) | user-config, tray + pipe + ConfigHost watcher, 5 threads, exe 6.2 MB | 4.6 | 19.4 | 0 (600 s) |
| 2026-11-01 | 1fbc856 (same) | after 5 `dctl run connect-technics --wait` (steps fail at once: no providers yet) | 5.1 (+0.5) | 20.2 | 0 (600 s) |

| 2026-11-01 | M5 run pop-up (Release AOT, exe 9.2 MB) | spike-config idle, pop-up never shown, 3 threads | 4.9 | 20.4 | 0 (120 s) |
| 2026-11-01 | same | after 3 runs of a 3-step flow with `popup: false` | 5.9 (+0.7) | 21.8 | 0 (60 s) |
| 2026-11-01 | same | after 10 runs with the pop-up (software D2D, d2d1/DWrite unloaded on hide, D-034) | 8.8 (+3.5) | 24.9 | 0 (60 s) |

| 2026-11-04 | 880a4b4 (T12.6 gamepad source, Release AOT, exe 12.8 MB) | no flow with a chord, no menu shown, 5 threads (chord rows superseded by D-086) | 6.1 | 22.8 | 16 (600 s; one tick, as the probe's no-pad run) |
| 2026-11-04 | D-086 (chords removed, Release AOT, 4K at 175 %) | idle, scratch config incl. an old `gamepad:` key, no menu: same baseline as before the pad work, 6 threads | 6.3 | 22.7 | 0 (60 s) |
| 2026-11-04 | same | menu open 25 s, no pad awake (XInput lists none): reader thread parked, 15 threads | 19.5 | 41.9 | 0 (20 s) |
| 2026-11-04 | same | 30 s after the menu closed (log: `gamepad: reading stopped`): no pad thread | 9.0 | 27.8 | 0 (25 s) |

| 2026-11-02 | 4328146 (M10 gate, Release AOT, exe 11.6 MB with Velopack) | no config, not installed (no update checks), 5 threads | 5.4 | 21.7 | 0 (600 s) |

| 2026-11-04 | T12.1 probe (`spikes/GamepadProbe xi`, AOT exe 1.5 MB, not the engine) | XInput reader + device-interface and suspend/resume notifications, no XInput pad (Steam Controller puck present), 7 threads | 5.4 | 15.8 | 0 (600 s; 22.7 M cycles); an earlier run 16 (one tick) |
| 2026-11-04 | same probe | Xbox pad (Flydigi Vader 4 Pro, wireless adapter, XInput) on and untouched, 20 Hz; the pad slept after ~4 min, so ~5.5 min polled (5493 polls) | 5.7 | 17.1 | 234 (600 s; 866 M cycles, ~50 µs per poll; ~410 if awake all along) |

| 2026-11-04 | T12.7 (Release AOT, dev PC 4K at 175 %) | idle, no config chords, 9 threads | 6.3 | 20.8 | — |
| 2026-11-04 | same | after 10 menus (list/grid, Esc, one pick; +20 s) | 12.7 (+6.3) | 27.6 | 0 (60 s, +80 s) |
| 2026-11-04 | same | same engine after 10 plain pop-up runs (comparison) | 10.1 (+3.7) | 25.0 | — |

M12/T12.7: flat over menus (14.1 after the first, 12.7 after ten, back to 12.6 at +80 s), 0 ms CPU idle, d2d1/DWrite unloaded.
Absolute budget holds (12.6 ≤ 15 MB, 27.5 ≤ 35 MB) but the +6.3 MB is ~1.8 MB over D-036's +4.5 line: the menu's software
surface is ~700×700 px at 175 % (≈2 MB) against the pop-up's ~600×180. Accepted by the user: the line is +6.5 MB with the menu (D-036 amended).

M5: absolute budget holds; the pop-up's first showing costs ~+2.5 MB private once (flat from 3 to 10 runs); the user
accepted it and the post-run line now allows it (D-036).

T10.4 gate passed (2026-11-02, row above). T0.5 gate passed: no AOT/trim warnings; hotkey press and tray icon checked on the desktop. T3.7 gate passed
(rows above). The post-run delta covers runner, history and pipe only; re-measure with real providers (M4, M10).

## Open questions

- Display, after Duplicate (F5 live run 2026-11-03, notes `f5-display/fix-runs/`): leaving Duplicate, the next one or
  two switches can be undone by Windows seconds later, `display.topology` included (Extend → Duplicate; "use only"
  DELL passed its check, then both came back on). The DELL's target id changes 0x1009038 → 0x9038 then, which looks
  like a hot-plug restore. Not fixed: a settle wait (WM_DISPLAYCHANGE, timeout) before `DisplayCheck` is the first
  thing to try, then a second check.
- Run pop-up over a full-screen game: spike S2 (X-010) showed that any window of ours over a DXGI full-screen game
  (FSO included) knocks the game out of full screen. T12.7: the menu asks `SHQueryUserNotificationState` first and
  `EngineHost` skips the run pop-up and refusal pop-up while QUNS = 3 (history still records the run). Untested live
  against a real full-screen game.
- Controller input for the menu: built (M12, D-086); see [flow-menu/DESIGN.md](flow-menu/DESIGN.md)
  §3.4 (D-056). Each M12 task moves its part into the sections above.
- Built in M13 (PLAN → M13): the Layout menu's `LayoutOptions` (D-067, T13.6) and the editor for flow calls and inputs (T13.10). Each task rewrites the
  section it changes. Fallback outcomes (D-065) are built (T13.7: Config, Runtime).
- Designed, not built (PLAN → M13 fixes, v0.29.0 feedback): Ctrl+S's accelerator
  tooltip hidden (X-008, F3). Flow ids are keys in: other flows'
  `flow.run` (and M12 `menu.show`), history and logs (never rewritten), desktop .lnk args and file names (D-049),
  `dctl run`, the engine's per-id launcher slots, and the per-flow layouts in `editor.json` (T13.6).
- Display writes (F5, D-077, X-009): `DisplayCheck` (pure, `Providers/Display`) gives each screen its own source per
  adapter (a saved clone keeps one shared) and compares the active paths with the request after every write; a
  mismatch fails the step (#70). `CcdDisplays.SwitchSet` asks `SDC_TOPOLOGY_SUPPLIED` first (the set's saved
  arrangement) and falls back to `USE_SUPPLIED | ALLOW_CHANGES | SAVE_TO_DATABASE`; `UseOnly` and, on error 87 from
  validation, `CcdLayout.Apply` go through it. `display.topology` is unchanged. Live matrix: `scripts/display-check.ps1`
  (read-only without `-Go`; with it, saves, runs, restores, restarts the installed engine) — only with the user's OK.
- Flow delete and rename (F4, D-075, D-076): `Core/Model/FlowIds` (slug, unique; New flow uses it) and `FlowRefs`
  (callers, rename of every `ConfigRef.Flow` param). `ConfigSession.SaveDraft` gives a renamed flow
  `Unique(Slug(name))` in its place, rewrites callers in the file and in drafts, and raises `FlowRenamed` before
  `Current` changes, so `FlowCanvasViewModel.FlowId` follows and the canvas stays open. `DesktopShortcutStore.MoveAsync`
  re-creates the old id's shortcut for the new one on the engine's `configReloaded` (create is refused until it has
  loaded the id), then removes the old one. `FlowsPageViewModel.Delete` / `UndoDelete` / `EndUndoAsync`: the removed
  flow and its index wait for Undo; the shortcut goes only when the bar ends (closing the editor waits for it).

- Library leftovers: editing an Android device's inputs and an audio match in place; renaming audio and Bluetooth keys
  (their cards show the device's name, not the key, D-046).
- Inspector leftovers (M8): headers as key/value rows + "Add header" (now "Name: value" lines), a script's "Open in
  editor", the pill ring showing a test result for 3 s, resizing the pane (280–480), and recording a chord the
  running engine already owns (RegisterHotKey eats it, so it runs that flow instead of reaching the recorder); designed in M13b: D-072).
- Not built in M7 yet: "+" on a port and on an edge midpoint (menus cover both), minimap, dot grid, F2 rename and
  Ctrl+L "Runs after…" (wait for the M8 inspector), the outline's live region for run results (M9),
  multi-select in the outline.

- Long canvas edges (skipping a column) are a straight Bézier and can cross a pill in the skipped column; layered
  layouts usually route them through dummy nodes. Fix if real flows look messy. The Layout menu's "Right angles"
  (D-067) routes orthogonally but doesn't dodge pills either.

- A value of the wrong *shape* (a list where a scalar belongs, e.g. `timeout: [5s]`, `hotkeys: Alt+Z`) still
  rejects the whole file; only `with:` and `after:` are shape-tolerant so far (D-023 covers bad scalar values).
  YAML aliases (`*anchor`) inside `with:` also reject it. `ConfigYaml.Load` never throws either way.
- Unknown YAML keys (typos like `afer:`) are ignored silently (`IgnoreUnmatchedProperties`); reporting them
  as warnings needs a pass over the representation model or a YamlDotNet hook.
