---
name: build
description: Build, run, test, publish (Native AOT) and measure this repo's engine, CLI and editor on the local Windows machine, including prerequisite checks and fixes for common build failures. Use whenever you need to compile, run the engine to see a change, run tests, publish the AOT engine, measure its idle footprint, set up the dev environment, or when a dotnet/MSBuild/ILC/AOT/trim/XAML error appears — even if the user just says "try it" or "does it work".
---

# Build

## Resolve names from this checkout

Before using the examples, read the current values below and substitute the placeholders. They are examples,
not literal shell commands. Do not derive names by uppercasing the product name or appending `App`.

| Placeholder | Source of truth |
|---|---|
| `<App>` | Prefix of the root `*.slnx` and the `src/*.Engine`, `*.Cli`, `*.Editor` project folders |
| `<engine>`, `<editor>`, `<cli>` | Each project's `AssemblyName` (or project filename when absent); use `dotnet msbuild <project> -getProperty:AssemblyName` if imported or conditional |
| `<data-folder>` | `FolderName` in `src/*.Core/AppInfo.cs` |
| `<pack-id>` | `$packId` in `scripts/pack.ps1`; this is an install folder, separate from the data folder |
| `<update-source-variable>` | `SourceOverrideVariable` in `src/*.Core/Updates/UpdateClient.cs` |
| `<tray-window-class>` | `ClassName` in `src/*.Engine/Shell/MessageWindow.cs` |

Find them with `rg --files src tests scripts -g '*.csproj' -g 'AppInfo.cs' -g 'UpdateClient.cs' -g 'MessageWindow.cs' -g 'pack.ps1'`
and `Get-ChildItem -File *.slnx`. Match actual project files, not directories: old names can survive in ignored
`bin/obj` folders after a rename. Confirm each role has one project; if ambiguous, use the solution's project
entries or `git ls-files` rather than guessing. Keep product names in source files, not in these instructions.

Local builds use the `dotnet` CLI only. CI (`.github/workflows/ci.yml`) builds and tests on every PR and
push to `main`.

## Prerequisites

Run `pwsh .claude/skills/build/scripts/check-prereqs.ps1`. It checks the .NET 10 SDK, the MSVC C++ tools
Native AOT links with, the Windows build and CPU architecture, and prints how to fix what's missing:

```powershell
winget install Microsoft.DotNet.SDK.10
# Native AOT (engine, <cli>) needs the MSVC linker + Windows SDK ("Desktop development with C++"):
winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
```

Installing software changes the user's machine — ask before running it. `dotnet build` / `dotnet test` work
without the C++ tools; only `dotnet publish` of the AOT projects needs them.

## Commands (from repo root, PowerShell)

```powershell
dotnet build <App>.slnx -c Debug
dotnet test --solution <App>.slnx -c Debug --no-build      # Microsoft.Testing.Platform syntax
tests\<App>.Core.Tests\bin\Debug\net10.0-windows10.0.26100.0\<App>.Core.Tests.exe -class "<App>.Core.Tests.SomeTests"   # one xUnit v3 class

# AOT publish of the engine — the only build that tells you about AOT/trim warnings (they are errors):
dotnet publish src/<App>.Engine -c Release -r win-x64 -o artifacts/engine
dotnet publish src/<App>.Cli    -c Release -r win-x64 -o artifacts/cli
```

The engine keeps running after you start it and locks its exe → stop it before rebuilding:
`pwsh scripts/stop.ps1`. Start the published engine in the background (`Start-Process -WindowStyle Hidden`), never with a
blocking call.

Try a checkout on the user's PC: `pwsh scripts/dev-run.ps1` stops the running engine, builds this worktree and runs it on the fixed dev config `%LOCALAPPDATA%\<data-folder>\dev\config.yaml` (seeded once from `samples/dev-config.yaml`; `-NoEditor`, `-ResetConfig`); `-Sample showcase` (branchy flows, cards, failure lines, prompts) or `-Sample big` (40/60/100-step flows, the T21.8 probe) runs on `samples/dev-<name>.yaml` kept as `dev\<name>.yaml` instead; dev samples may use only read-only steps and no triggers or hotkeys (`SampleConfigTests`); `pwsh scripts/dev-run.ps1 -Restore` brings the installed app back.

## Measuring the footprint (gates M0, M3, M10)

```powershell
pwsh scripts/measure.ps1                       # publishes, starts, settles 30 s, samples 60 s, stops
pwsh scripts/measure.ps1 -IdleSeconds 600      # the 10-minute CPU check
pwsh scripts/measure.ps1 -NoPublish -Exe <path>
pwsh scripts/editor-memory.ps1                 # editor: Probe 100 copy/paste/undo, open/close; MB per step (F-036)
```

It prints private bytes, working set and CPU time used during the window, and a Markdown row to paste
into ARCHITECTURE → Footprint. Budget: ≤ 15 MB private, ≤ 35 MB working set, 0 ms CPU idle. Over budget →
stop and ask the user (CLAUDE.md).

## Packaging, installing and testing updates (D-043)

```powershell
pwsh scripts/pack.ps1                          # artifacts/releases: Setup.exe, portable zip, full nupkg, feed
pwsh scripts/install.ps1                       # pack this checkout and install it like a release
```

Test an install without touching the real one: `<Setup.exe> --silent --installto <scratch>\install` (no `-- <args>`:
Setup panics, X-002), then start `<scratch>\install\current\<engine>.exe <scratch>\config.yaml`. Test updates:
`pwsh scripts/pack.ps1 -Output <scratch>\feed -Version 0.x.y` (higher than installed), set
`$env:<update-source-variable> = '<scratch>\feed'`, run `<install>\current\<cli>.exe update`, then restart the engine
(or `<install>\Update.exe apply --silent --norestart` to test the clean exit). Remove with
`<install>\Update.exe uninstall --silent`. Velopack's own log: `%LOCALAPPDATA%\velopack\velopack_<pack-id>.log`.
Never `Start-Process -Wait` on Setup in PowerShell 7: it also waits for the engine Setup starts. Never push a `v` tag (that publishes a release; the user decides). When the user asks for one, use the `release` skill.

## Seeing what the engine did

Log: `%LOCALAPPDATA%\<data-folder>\logs\engine.log` (daily `engine-yyyy-MM-dd.log` archives). Ask the running
engine with `artifacts\cli\<cli>.exe status | list | history | run <flow> --wait | run <flow> --dry-run`
(publish `src/<App>.Cli` first). `scripts/measure.ps1 -RunFlow <id>` also samples after runs. Read it from PowerShell: the agent's Bash tool may run
sandboxed with a stale copy of `%LOCALAPPDATA%` (seen in the OSD repo, 2026-10-22).

Tray: the icon window used by the `Shell_NotifyIcon` implementation is class `<tray-window-class>`
(uID 1); `Shell_NotifyIconGetRect` gives its rect; right-click there for the menu. Pick items with the mouse
(`SetCursorPos` + `mouse_event`, physical pixels after `SetProcessDpiAwarenessContext(-4)`): a posted
`WM_CONTEXTMENU` can't take the foreground. On the user's PC the taskbar is vertical (left) and the icon sits
in the overflow: `Shell_NotifyIconGetRect` then returns the chevron's rect until the overflow is open — left-click
it, take the rect again, right-click that. To only *look* at the menu, post `WM_APP+1` with
`lParam = WM_CONTEXTMENU (0x7B)` and the anchor in `wParam`; keys don't reach that menu (no foreground).
Screenshots: `System.Drawing.Graphics.CopyFromScreen` from Windows PowerShell, then read the PNG (2026-11-01).

Editor (M7): run it on a throwaway file, never the user's config: `<editor>.exe --config <scratch>\config.yaml`. A dev engine for it takes the path as its first argument (`<engine>.exe <scratch>\config.yaml`, no `--config`: that would be read as the path). Its pipe is per user, so stop it before starting another engine. Screenshot helper: never UIA-invoke the name "Close", it is the window's close button. Stop your editor by its path (`Get-Process <editor> | ? Path -eq <exe> | Stop-Process`), never `taskkill /IM`: other sessions' editors run too.
Open a flow with UIA (`InvokePattern` on the card named "Open <flow name>"; Run is "Run <flow name>"). The user's installed engine owns the per-user pipe: to run a scratch flow, `scripts/stop.ps1`, start the dev engine, and restart `%LOCALAPPDATA%\<pack-id>\current\<engine>.exe` after. Key presses: check `GetForegroundWindow` is your editor before each one (other sessions' editors take the foreground). Drags need **absolute
`mouse_event` moves** (`MOUSEEVENTF_MOVE|ABSOLUTE|VIRTUALDESK`, coordinates scaled to 0–65535 over the virtual
screen): `SetCursorPos` alone reaches WinUI as a single pointer move, so a drag never starts. Light theme: flip
HKCU…Personalize `AppsUseLightTheme`; high contrast: `SystemParametersInfo(SPI_SETHIGHCONTRAST)` with
`SPIF_SENDCHANGE` only (not saved), restored in a `finally`. Restore both right after the screenshot.
Keys: `keybd_event` letters and Ctrl chords work, but **Enter arrives as `VirtualKey.None`** (even with its scan
code), so an AutoSuggestBox never submits; send Enter with `SendKeys.SendWait("{ENTER}")` (2026-11-02). Toggling
an AppBarToggleButton through UIA leaves focus on the button: click into the target before sending keys.
Selecting a step through UIA doesn't work while another window holds the foreground: canvas cards have no
Invoke/Select, and the Outline (More options › Outline) TreeItem's Select/Invoke leave the inspector on the flow
(2026-11-04); cover that hop with a view-model test. UIA finds elements by Name: Pivot headers and ListView rows take the item's `ToString()`, so give item VMs one;
a Button with text content needs `AutomationProperties.Name`, or the search finds its inner TextBlock (no Invoke).

## Verifying changes

Builds passing doesn't mean the engine works. After a shell/interop change: publish, run it, press the
hotkey / open the tray menu and check the log. If you can't verify on screen, say so explicitly.

## Troubleshooting

Grow this table when you hit something non-obvious (and record a dead end if a fix attempt failed).

| Symptom | Cause / fix |
|---|---|
| `Platform linker not found` / `link.exe` errors on `dotnet publish` | MSVC C++ tools missing: install VS Build Tools with the VCTools workload (above). Git's `usr\bin\link.exe` on PATH is not a linker. |
| `'vswhere.exe' is not recognized` then `MSB3073 … link.exe … exited with code 123` | ILC's `findvcvarsall.bat` runs `vswhere` from PATH; a shell started before Build Tools were installed lacks it. `scripts/measure.ps1` prepends `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`; elsewhere do the same or open a new shell. |
| `IL2xxx` / `IL3xxx` warnings as errors | Reflection or dynamic code reached from the AOT engine. Use source generators (YamlDotNet static context D-005, System.Text.Json source-gen) — never suppress. |
| `Testing with VSTest target is no longer supported by Microsoft.Testing.Platform` | `global.json` must keep `"test": { "runner": "Microsoft.Testing.Platform" }`; use `dotnet test --solution …`. |
| `YDNG001` / `CS0144 Cannot create an instance of the abstract type` from the YamlDotNet generator | The static context generates every public property type, abstract ones included. Box the value in a concrete class with no public properties and handle it in an `IYamlTypeConverter` (`Yaml/ParamDocument`). |
| `SYSLIB1051` on a `[LibraryImport]` with `HWND` | CsWin32 structs aren't marshallable by the source generator; declare `nint` (OSD build skill). |
| `LNK1207: incompatible PDB format in …\win-x64\native\<App>.pdb` on publish | Stale native PDB from another linker version: delete `src/<App>.Engine/bin/Release/…/win-x64/native` and `obj/Release/…/win-x64/native` (2026-10-31). |
| `MSB3073 … 'vswhere.exe' is not recognized` on publish | ILC's link step needs vswhere on PATH: `$env:PATH += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"` (2026-11-01). |
| `MSB3027` / file is locked by `<App>` | Running engine; `pwsh scripts/stop.ps1`. |
| Need a CsWin32 COM/P/Invoke signature (D2D, DWrite, DWM) | Generated code isn't on disk: `dotnet build src/<App>.Engine --no-dependencies --no-incremental -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=<scratch dir>`, then grep the `.g.cs` (2026-11-01). |
| Editor starts, no window ever shows, `OnLaunched` never runs (process idles, no top-level windows) | Stale `src/<App>.Editor/obj` after XAML/Main changes (seen 2026-11-01 adding `DISABLE_XAML_GENERATED_MAIN`): delete the editor's `obj` and `bin`, rebuild. Check this before bisecting code. |
| Editor exits at once, WER shows `Microsoft.UI.Xaml.dll` 0xc000027b | A `StaticResource` that does not exist (seen 2026-11-04: `AccentSplitButtonStyle`) fails at load, not at build. Check new XAML resource names first. |
| The user reports a released feature missing | Check which version is installed: `%LOCALAPPDATA%\<pack-id>\current\sq.version`. `<cli> update` only downloads; it installs when the engine restarts. On 2026-11-03 the dev PC still ran 0.22.0 while 0.29.0 was out. |
