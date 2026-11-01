---
name: build
description: Build, run, test, publish (Native AOT) and measure the Daze Control engine, CLI and editor on the local Windows machine, including prerequisite checks and fixes for common build failures. Use whenever you need to compile, run the engine to see a change, run tests, publish the AOT engine, measure its idle footprint, set up the dev environment, or when a dotnet/MSBuild/ILC/AOT/trim/XAML error appears — even if the user just says "try it" or "does it work".
---

# Build

Local builds use the `dotnet` CLI only. CI (`.github/workflows/ci.yml`) builds and tests on every PR and
push to `main`.

## Prerequisites

Run `pwsh .claude/skills/build/scripts/check-prereqs.ps1`. It checks the .NET 10 SDK, the MSVC C++ tools
Native AOT links with, the Windows build and CPU architecture, and prints how to fix what's missing:

```powershell
winget install Microsoft.DotNet.SDK.10
# Native AOT (engine, dctl) needs the MSVC linker + Windows SDK ("Desktop development with C++"):
winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
```

Installing software changes the user's machine — ask before running it. `dotnet build` / `dotnet test` work
without the C++ tools; only `dotnet publish` of the AOT projects needs them.

## Commands (from repo root, PowerShell)

```powershell
dotnet build DazeControl.slnx -c Debug
dotnet test --solution DazeControl.slnx -c Debug --no-build      # Microsoft.Testing.Platform syntax
tests\DazeControl.Core.Tests\bin\Debug\net10.0-windows10.0.26100.0\DazeControl.Core.Tests.exe -class "DazeControl.Core.Tests.SomeTests"   # one xUnit v3 class

# AOT publish of the engine — the only build that tells you about AOT/trim warnings (they are errors):
dotnet publish src/DazeControl.Engine -c Release -r win-x64 -o artifacts/engine
dotnet publish src/DazeControl.Cli    -c Release -r win-x64 -o artifacts/cli
```

The engine keeps running after you start it and locks its exe → stop it before rebuilding:
`pwsh scripts/stop.ps1`. Start the published engine in the background (`Start-Process`), never with a
blocking call.

## Measuring the footprint (gates M0, M3, M10)

```powershell
pwsh scripts/measure.ps1                       # publishes, starts, settles 30 s, samples 60 s, stops
pwsh scripts/measure.ps1 -IdleSeconds 600      # the 10-minute CPU check
pwsh scripts/measure.ps1 -NoPublish -Exe <path>
```

It prints private bytes, working set and CPU time used during the window, and a Markdown row to paste
into ARCHITECTURE → Footprint. Budget: ≤ 15 MB private, ≤ 35 MB working set, 0 ms CPU idle. Over budget →
stop and ask the user (CLAUDE.md).

## Seeing what the engine did

Log: `%LOCALAPPDATA%\DazeControl\logs\` (from T3.1). Read it from PowerShell: the agent's Bash tool may run
sandboxed with a stale copy of `%LOCALAPPDATA%` (seen in the OSD repo, 2026-10-22).

Tray (from the OSD build skill, same Shell_NotifyIcon code): the icon window is class `DazeControl.Tray`
(uID 1); `Shell_NotifyIconGetRect` gives its rect; right-click there for the menu. Pick items with the mouse
(`SetCursorPos` + `mouse_event`, physical pixels after `SetProcessDpiAwarenessContext(-4)`): a posted
`WM_CONTEXTMENU` can't take the foreground.

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
| `LNK1207: incompatible PDB format in …\win-x64\native\DazeControl.pdb` on publish | Stale native PDB from another linker version: delete `src/DazeControl.Engine/bin/Release/…/win-x64/native` and `obj/Release/…/win-x64/native` (2026-10-31). |
| `MSB3073 … 'vswhere.exe' is not recognized` on publish | ILC's link step needs vswhere on PATH: `$env:PATH += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"` (2026-11-01). |
| `MSB3027` / file is locked by `DazeControl` | Running engine; `pwsh scripts/stop.ps1`. |
