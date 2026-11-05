# Daze Control

A Windows 11 tray app that runs **flows**: named sets of steps (connect Bluetooth headphones, switch the default
audio device, change the display layout, start a program, switch the TV input…) started by a global hotkey, the tray
menu or the command line. Steps can run one after another or together, and you decide which ones may fail.

- **Engine** (`DazeControl.exe`): stays in the tray, costs next to nothing while idle (Native AOT, no UI framework).
- **Editor** (`DazeControl.Editor.exe`): opens from the tray and closes when you're done. Edit flows on a canvas,
  manage the library of devices, look at past runs.
- **`dctl`**: the same from a terminal.

Per-user install, no admin rights. Windows 11 24H2 or later, x64.

## Install

Download `DazeControlApp-win-Setup.exe` from [Releases](https://github.com/dazewell/daze-control/releases) and run
it. It installs to `%LOCALAPPDATA%\DazeControlApp`, adds Daze Control to the Start menu and starts it. The builds
aren't signed yet, so SmartScreen may ask first ("More info" › "Run anyway").

Turn on **Start with Windows** in the editor's Settings (or `dctl autostart on`). Updates download in the background
once a day and install the next time Daze Control starts; Settings › Updates checks now and offers **Restart now**.
Uninstall from Settings › Apps; your flows and history stay.

## Use it

Click the tray icon for your flows, **Open Daze Control** (the editor) and **Pause shortcuts**. A run shows a pop-up with
each step's progress; a failure stays until you close it, and clicking it opens the editor.

Your configuration is one YAML file, `%APPDATA%\DazeControl\config.yaml`. The editor saves it as you go and keeps
backups next to it; you can also edit it by hand, and the engine reloads it when it changes. A flow with a mistake
is turned off and its problems listed; all other flows keep working. Example:

```yaml
version: 1
library:
  audio:
    headphones: { match: "Technics EAH-AZ80" }
  bluetooth:
    headphones: { address: "B8:20:8E:ED:48:FC" }
flows:
  connect-headphones:
    name: Connect headphones
    hotkeys: [Alt+Z]
    nodes:
      - { id: bt, step: bluetooth.connect, with: { device: headphones } }
      - { id: out, step: audio.setDefault, with: { device: headphones }, after: [bt] }
```

Steps: `audio.*`, `bluetooth.connect`/`disconnect`, `display.*`, `process.run`, `script.powershell`,
`http.request`, `android.*`, `service.start`/`stop`, `wait`, `notify`, `flag.*`. The editor's step palette lists them
with their settings ([docs/FEATURES.md](docs/FEATURES.md) has the whole model).

### Menu with a controller

A `menu.show` step pops up a menu of flows over every window. A flow with that step is started like any other (shortcut,
tray, `dctl run`); while the menu is open an Xbox pad (or a Steam Controller's Xbox mode) drives it: D-pad or left stick
to move, A to run, B to close. Buttons already held when the menu opens are ignored until you let go. Daze Control reads
the pad only while a menu is open. Starting a flow from a pad button is done with a shortcut: map the button to a key in
Steam Input and give the flow that key as its shortcut.

### Command line

```
dctl run <flow> [--wait] [--dry-run]   start a flow; --dry-run shows what would happen
dctl list | status | history [flow]
dctl validate [file]                   problems in config.yaml
dctl devices audio|bluetooth|displays|adb
dctl capture-display-layout <name>     the screens as they are now, as YAML for the library
dctl autostart [on|off] · dctl update
```

Exit codes: 0 ok, 1 failed, 2 usage, 3 Daze Control isn't running.

Logs: `%LOCALAPPDATA%\DazeControl\logs` (7 days). Run history: `%LOCALAPPDATA%\DazeControl\history.jsonl`.

## Build from source

.NET 10 SDK and, for the Native AOT engine and `dctl`, the MSVC C++ build tools ("Desktop development with C++").

```powershell
dotnet build DazeControl.slnx
dotnet test --solution DazeControl.slnx
pwsh scripts/install.ps1   # pack this checkout and install it like a release
```

More in [.claude/skills/build/SKILL.md](.claude/skills/build/SKILL.md). The project is written by AI agents; the
docs are their shared memory: [GOALS](docs/GOALS.md), [ARCHITECTURE](docs/ARCHITECTURE.md),
[DECISIONS](docs/DECISIONS.md), [DEADENDS](docs/DEADENDS.md), [PLAN](docs/PLAN.md), [UX](docs/UX.md).
