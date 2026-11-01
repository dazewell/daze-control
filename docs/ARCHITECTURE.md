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
   message thread: TrayIcon · GlobalHotkey · RunPopup (Win32 + DWM backdrop + D2D/DirectWrite)
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
                             undocumented): Audio, Bluetooth, Display, Process, Script, Http, FireTv,
                             Service, Wait, Notify, Flag, Credentials
src/DazeControl.Ipc/         protocol records (source-gen JSON), PipeServer, PipeClient
src/DazeControl.Engine/      AOT WinExe: Program, Shell/ (TrayIcon, GlobalHotkey, RunPopup), Lifecycle/
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
handled its failure: its `onFailure` policy is not applied, the fallback runs, its `success` dependents are
skipped, and the flow ends PartiallySucceeded (D-024). Durations "500ms", "20s", "1m30s" (`Model/Duration`). Enums read
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
once.
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
Tests: `FakeProvider` + thread-safe `ManualTime` with `WaitForTimerAsync` (Runtime.Tests).

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
  on the next reload; Pause unregisters all (not persisted). Failures → `status` ("Alt+Z: Another app uses…").
- `Runtime/Tray/TrayMenu`: the menu as data (UX §5.6), rendered by `Engine/Shell/TrayIcon` each time it opens
  (left or right click), dark/light like the taskbar (uxtheme #135/#136). Icon drawn in code by
  `Shell/TrayGlyph` (idle / busy / paused, D-027); tooltip = running flows, else paused, else the last run's
  problem. Not yet: per-flow glyph bitmaps in the menu (need M5's glyph-font rendering).
- `Runtime/Lifecycle/Autostart`: HKCU Run value `DazeControl` → engine exe (OSD port), set through the pipe op.

## IPC

`\\.\pipe\DazeControl-<user SID>`, `PipeOptions.CurrentUserOnly`, newline-delimited JSON (source-gen
`Ipc/IpcJson`, camelCase, string enums, nulls omitted), `protocol: 1`. `Ipc/PipeServer`: any number of clients;
requests on one connection run concurrently, answers carry the request id; after `subscribe` the connection also
gets events (`PipeMessage.Event`, id 0: runStarted, nodeStarted, nodeFinished, runFinished, configReloaded). A
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
true/false/absent); `devices`, `test-step`, `capture-display-layout` answer "can't do that yet" until M4.
`dctl` (`src/DazeControl.Cli`): run/cancel/list/validate/status/history/autostart/devices, parsed strictly by
`Ipc/CommandLine` (unknown flag or wrong word count → exit 2, never ignored); `validate <file>`
and, with no engine, `list`/`validate` read the YAML themselves. Exit codes 0 ok, 1 failed, 2 usage, 3 no engine.

## Adding a provider

1. Add a descriptor in `Core/Catalog/` (id, glyph, title, params as `ParamSpec`, default timeout, resources).
2. Implement `IStepProvider` in `Providers/<Area>/` (`ValidateAsync`, `IsAlreadyDoneAsync?`, `ExecuteAsync(ctx,
   params, ct)`, optional `IEnumerator` for pickers); interop behind a small interface with a fake.
3. Register in the engine composition root. A test asserts every descriptor has exactly one implementation.
4. Tests: descriptor/param validation, YAML round-trip of a sample node, runner behaviour with the fake.
No change to Runner, YAML mapper or editor: the inspector renders from `ParamSpec`.

## Footprint (measured by `scripts/measure.ps1`)

Budget: engine idle ≤ 15 MB private, ≤ 35 MB working set, 0 ms CPU over 10 idle minutes, back within +2 MB after a run.

| Date | Build | What ran | Private MB | Working set MB | CPU ms (window) |
|---|---|---|---|---|---|
| 2026-10-31 | d7f8006 (T0.5 spike, Release AOT) | tray icon + 1 hotkey + YamlDotNet static load, 3 threads, exe 3.3 MB | 3.8 | 15.2 | 0 (60 s) |
| 2026-10-31 | d7f8006 (same) | same, 10-min idle; 5 threads | 3.9 | 15.5 | 0 (600 s) |

| 2026-11-01 | 1fbc856 (M3 engine shell, Release AOT) | user-config, tray + pipe + ConfigHost watcher, 5 threads, exe 6.2 MB | 4.6 | 19.4 | 0 (600 s) |
| 2026-11-01 | 1fbc856 (same) | after 5 `dctl run connect-technics --wait` (steps fail at once: no providers yet) | 5.1 (+0.5) | 20.2 | 0 (600 s) |

T0.5 gate passed: no AOT/trim warnings; hotkey press and tray icon checked on the desktop. T3.7 gate passed
(rows above). The post-run delta covers runner, history and pipe only; re-measure with real providers (M4, M10).

## Open questions

- A value of the wrong *shape* (a list where a scalar belongs, e.g. `timeout: [5s]`, `hotkeys: Alt+Z`) still
  rejects the whole file; only `with:` and `after:` are shape-tolerant so far (D-023 covers bad scalar values).
  YAML aliases (`*anchor`) inside `with:` also reject it. `ConfigYaml.Load` never throws either way.
- Unknown YAML keys (typos like `afer:`) are ignored silently (`IgnoreUnmatchedProperties`); reporting them
  as warnings needs a pass over the representation model or a YamlDotNet hook.
