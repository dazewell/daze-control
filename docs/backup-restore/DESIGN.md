# Backup and restore (M19) — design

Proposed 2026-11-06, **not built, waiting for the user's answers** (§12). Decisions: D-103 (all proposed). Tasks: [PLAN.md](../PLAN.md) → M19.
Inventory of user state is from ARCHITECTURE (Config, Editor) and docs/steps-review/REVIEW.md §5. A task that builds a part moves the
as-built facts into ARCHITECTURE/UX/FEATURES and cuts them here (D-053). User text never says node, DAG or OSD; no Narrator work (D-091).

## 1. Goal and scope
User (2026-11-06): "we want to build backup/restore feature". Reading: *I can save everything I set up in one file, and get it back after a
mistake, a broken edit, a reinstall or on a new PC.*
- **In:** one portable file; Backup now and Restore from file (editor, About › Backup); optional automatic backups with a retention count; `dctl backup` /
  `dctl restore`; preview before restore; safety copy before restore; per-flow fail-soft after restore; a list of what will not work on this PC.
- **Out (non-goals):** cloud sync or scheduling (no timers, D-009); encrypting the file (it holds no secrets, so nothing to encrypt); backing up secrets
  (D-014); partial restore of single flows from the file in v1 (the Flows list's own export/import is a separate "Out" item of M16); undo history of
  edits (editor Undo is per session); backing up logs, run history or the installed app; restoring Windows-side things (pairing, autostart).

## 2. What a backup contains
| Item | Source | Default | Note |
|---|---|---|---|
| Flows, library (audio, Bluetooth, displays, Android), `settings` (pop-up, `stepDefaults`), tray order and groups | `%APPDATA%\DazeControl\config.yaml` | **in** | Everything the engine needs. Tray order and folders are inside it (D-087..D-090). Copied **byte for byte** (comments a user hand-wrote survive). |
| Editor view state | `%LOCALAPPDATA%\DazeControl\editor.json` (window place, nav pane, per-flow layout, list sort and collapsed groups) | **in, restored only if "Editor layout" is ticked** | Per-PC by nature (D-068); window rectangle and DPI are meaningless on another screen setup, so the restore skips `window` and keeps the rest. |
| Run history | `history.jsonl` | **out**, tick "Include run history" to add | Regenerable, personal. |
| Logs, `android-apps\` cache, `backups\` | – | **out** | Regenerable. |
| Credentials | Windows Credential Manager | **never** | D-014. |
| Flags | memory only (D-029) | – | Nothing to save. |
| Desktop shortcuts, autostart | `.lnk` files, HKCU Run | **out** | Rebuildable from flow ids (D-049) and the Settings toggle; the restore result says "Desktop shortcuts and Start with Windows are not restored; turn them on again" and offers the actions if the flow had one. |

**Secrets.** config.yaml holds only credential *names* (`credential: github-token`), so the file is safe by construction. The backup *verifies* it
instead of trusting it: the writer runs the same check as the log redactor (no `Authorization`/`Bearer` header values, no `password`-like keys with
literal values in `http.request.headers`/`body`); a hit is a warning in the result ("this header looks like a secret, put it in Credentials") and the
file is still written, because the user typed it into config.yaml, not us (D-014 forbids it anyway). On restore, `credentials` names used by the flows
are compared with Credential Manager (names only; the existing `credentials` query): missing ones are listed under **Needs attention** ("Re-save the
credential `github-token` in Library › Credentials"). Nothing is ever read from or written to Credential Manager by backup/restore.

## 3. File format
One file `DazeControl-backup-2026-11-06-2130.dcbackup` (a plain zip; extension is only a label, also opens as `.zip`; written with `System.IO.Compression`,
AOT-safe, no new dependency).
```
manifest.yaml        # always first entry
config.yaml          # byte for byte
editor.json          # optional
history.jsonl        # optional
```
```yaml
format: 1                       # the backup layout, not the config schema
app: 0.46.0                     # DazeControl version that wrote it
configVersion: 1                # config.yaml `version:`
created: 2026-11-06T21:30:00+01:00
machine: DAZE-PC                # shown in the preview ("made on DAZE-PC")
reason: manual                  # manual | auto-save | before-update | before-restore | cli
contents:
  config.yaml: { bytes: 18234, sha256: "…" }
  editor.json: { bytes: 912,   sha256: "…" }
counts: { flows: 14, library: 9 }   # for the preview, recomputed on read, never trusted
```
- Human-readable and diff-able: unzip and read config.yaml. No binary blobs beyond what config.yaml already has (display layouts are base64 in it today).
- **Integrity:** SHA-256 per entry in the manifest, checked on open; a mismatch or a missing entry is "This backup is damaged" and nothing is restored.
  A zip-slip guard: only the four known names are read, never extracted to disk by path.
- **Versions:** `format` newer than this build knows → "Made by a newer Daze Control (0.5x). Update, then restore." No guessing. `configVersion` is handled by the
  ordinary config loader (§7): today only `1` exists; a future migration lives in the mapper (as aliases do, REVIEW §4) and is exercised by the backup tests
  with a stored old fixture, so an old backup always loads. Old backups are forever supported for restore.
- File name pattern is sortable; never overwritten (a counter is added).

## 4. Where it lives and how it is triggered
Default folder **`%LOCALAPPDATA%\DazeControl\backups\auto`** for automatic backups; manual ones go wherever the user picks (the Save dialog starts in
`Documents`). The old per-session `.bak` files in `%APPDATA%\DazeControl\backups` (`ConfigStore`, newest 10) stay: they are the cheap "last saves"
and are folded into the restore list as "Earlier saves" (config only, no manifest), so there is one place to restore from (§6).

| Trigger | Where | Notes |
|---|---|---|
| **Backup now** | editor, About › Backup (Settings page, new card "Backup and restore") | Save dialog; file written by the editor process. |
| **Restore from file…** | same card | Open dialog, or drag a file onto the card. |
| **Automatic backup** | engine side-effect of existing events, **no timer** | Off by default? see choice 4. Taken at: (a) *before an update is applied* (the installed engine's update step already runs once a day and at start; a backup first costs one zip), (b) *after the editor saves* a flow/library/settings change if the newest auto backup is older than 24 h (checked at that save, an `Update`d timestamp from file names, no poll), (c) *before a restore* (always, §6). Retention: newest **N** (default 10, 3–50) auto backups, older deleted when a new one is written. |
| **CLI** | `dctl backup [path] [--history] [--editor]`, `dctl restore <file> [--merge\|--replace] [--yes] [--dry-run] [--editor]` | Scripting. `--dry-run` prints the preview. Without `--yes`, `restore` prints the preview and asks on a terminal, refuses when not interactive (exit 2). `backup` with no path writes to the auto folder. Exit codes: 0 ok, 1 failed, 2 refused/needs `--yes`. |

All triggers go through one class, `Core/Backup/BackupService` (pure: reads/writes `Stream`s and a `IBackupFiles` abstraction, `TimeProvider`, no engine
types), so the editor, the CLI and the engine call the same code and the tests need no process.

## 5. Engine vs editor vs CLI (footprint)
- **Heavy work is not in the engine.** Zipping and unzipping run in the **editor** (Backup now, Restore) and the **CLI** (`dctl`), both short-lived
  processes. The engine never links a UI and keeps no zip state.
- The only engine involvement is **hot reload** (it already watches config.yaml, D-021) and the automatic triggers (a) and (c)-via-update. The update step
  is already a hidden `dctl update` run (ARCHITECTURE › Updates): it calls `BackupService.Create(reason: before-update)` itself, so the engine process does
  nothing new. Trigger (b) is run by the **editor** at save time (it knows it just saved; an engine config change from a hand edit is not backed up, accepted).
  So: **no engine code path, no timer, no resident object, no new thread**; idle stays 0 ms CPU and the +2 MB-after-run gate is untouched.
- `System.IO.Compression` is part of the BCL: the engine AOT image is unchanged because the engine does not reference the Backup namespace (a project
  reference to Core is already there; the namespace is trimmed). The T-task measures the engine exe size before/after and fails the build on growth > 50 KB.
- Footprint of the tools: a backup is 20–60 KB; zip in memory, written atomically (temp + move), under 100 ms.

## 6. Restore: preview, modes, safety, undo
Flow of **Restore from file…** (one dialog, three steps; the same data feeds `dctl restore --dry-run`):
1. **Open and check.** Integrity and version (§3). Failure → InfoBar, nothing changed.
2. **Preview — "What will change".** Compares the file's config.yaml with the current one, using the normal load + validate on both:
   - Counts and names: *Flows: 3 new, 2 changed, 1 only on this PC (kept / removed)*; library items the same; settings changed yes/no.
   - A list per flow with a state: **New**, **Changed** (a one-line "step added/removed/edited" summary from the flow's own `ValueList` equality), **Same**,
     **Only here**.
   - **Needs attention** (never blocks): flows that would be disabled by validation (unknown step from a newer build, missing library reference, hotkey taken by
     another restored flow); machine-specific values (§8); credential names not saved here (§2).
3. **Choose and confirm:** radio **Replace everything** (recommended: the PC ends up exactly like the backup, what is only here is removed) or **Add what's
   missing** (merge: file's items that are new are added, items that exist keep the **current** version, nothing is removed; id clash on flows = keep current and
   list it; library items by kind+id the same). Checkbox "Editor layout" (default off). A per-flow tick list is **not** in v1 (choice 3). Button **Restore**.

Doing it:
1. **Safety backup first**, always, even from the CLI (`reason: before-restore`, auto folder, not counted against the retention count for the first 24 h so a quick
   second try can't push it out). If this fails, the restore stops.
2. Write the result as **one atomic config.yaml replace** (`ConfigStore` temp + move, same as any editor save); editor.json after it.
3. **Running engine:** no restart. The engine's hot reload handles it exactly like a hand edit: per flow the new version if valid, else the last valid one, else
   disabled (D-021) — this is the same fail-soft path, so a restore can never stop the engine. A flow running now finishes with the library it was triggered with
   (D-021). A restore never needs a restart; the dialog says "Done. Your flows reloaded." and shows problems from the engine's `status`.
   The editor's open flow draft (D-047): restore is refused while a flow has unsaved edits ("Save or discard the open flow first").
4. **Undo:** after a restore the result card shows **Undo restore** (restores the safety backup in the same way, no new safety copy) for as long as the editor
   stays open; afterwards it is "Restore from file…" and the safety backup in the list ("Before restore, 21:30"). The list under the card shows the last backups
   (auto, before-restore, earlier saves) with **Restore** and **Delete** per row — the same preview and safety flow.
5. Manual restore from `Earlier saves` (the old `.bak` files) is config-only, replace mode, same dialog.

## 7. Validation after restore (fail-soft, D-021)
Restore copies text and lets the **normal loader** judge it. Nothing is rewritten, repaired or dropped by restore itself:
- Unknown top-level or unknown keys: kept in the file as the loader keeps them today (the file is copied byte for byte).
- A newer config than this build knows: the mapper's per-flow problems (unknown step id) disable only those flows; the preview shows them **before** the user
  confirms; Replace is still allowed (the user may update later and they come back).
- A file that does not parse at all: the preview says "config.yaml in this backup can't be read: <line>" and **Restore is disabled**; the live config is never
  replaced by something the loader cannot read. (Different from hand edits, where an unreadable file only changes the problems: here we have the choice.)
- Merge mode validates the merged text before writing; if the merge itself would not load, nothing is written.

## 8. New PC, portable use, things that cannot come along
- **Fresh install on a new PC:** install, open the editor, Settings › Backup and restore › *Restore from file…* (or `dctl restore file.dcbackup --replace --yes`
  right after install). On a PC with no config yet the safety copy is skipped (nothing to save).
- **Portable:** the product has no portable mode (installs per user, Velopack, D-015); `--config <path>` redirects config.yaml, `backups\` and `editor.json` (ARCHITECTURE).
  `dctl backup`/`restore` obey `--config`, so a copy of the app folder with its own config can be backed up and restored. A "copy this zip and unzip" move is the
  same restore.
- **What restores but will not work** (listed under Needs attention, found by the existing validation and library checks, never rewritten):
  | Value | Why | What the user sees |
  |---|---|---|
  | Bluetooth `address` | the device is not paired with this PC | "Pair `technics` in Windows first (Bluetooth & devices); the flow then works." |
  | Display layouts (base64 CCD) | monitor set differs | the existing "n screen(s) aren't connected" at run; the preview only says "layout `tv` was captured on DAZE-PC" when the machine name differs |
  | Audio `match` text | device names differ | step fails with its normal message; no rewrite |
  | Android `adb` path, device IP | platform-tools elsewhere, adb pairing | "adb not found at …" at run; `Connect Android device` flows still work if adb is there |
  | `process.run`/`file.check`/`cwd` absolute paths | different folders | a count: "5 steps use fixed paths. They will fail if the file isn't at the same place." (heuristic: absolute path that doesn't exist now) |
  | Credentials, shortcuts, autostart | not in the file (§2) | explicit lines, not warnings |
  This is a read-only pass in `BackupService.Preview` that reuses `ProviderCatalog` descriptors, the library and the existing credential/path queries. Existence checks
  of paths run only in the preview, once, at user action (no polling).

## 9. Accessibility
Standard WinUI controls only: the card, the restore dialog (radio buttons, check box, list of flows with text states, not colour alone), default focus on the
file picker then on the safer option, Esc cancels, the result is an InfoBar (not a toast). Contrast/high-contrast/225 % text use the editor's resources; checked
by the UX acceptance list. Keyboard: every action reachable by Tab/Enter; no drag-only path (drag-drop of a file is an extra). No Narrator-specific work (D-091).

## 10. Errors (all plain sentences, one InfoBar)
| Case | Message |
|---|---|
| Not a zip / no manifest | "This isn't a Daze Control backup." |
| Hash mismatch / missing entry | "This backup is damaged. Nothing was changed." |
| `format` too new | "Made by a newer Daze Control (x). Update it, then restore." |
| config.yaml unreadable | see §7 |
| Disk full / path not writable | "Couldn't save the backup to <folder>: <reason>." The editor never loses the live config (atomic write). |
| Safety backup failed | "Couldn't make a safety copy first, so nothing was restored." |
| Draft open | "Save or discard the open flow first." |
| Engine not running | restore still works (files only); the next start loads it; the result says so. |

## 11. Testing (throwaway config)
- **Core.Tests** (no process): round trip create→open→restore text equal; hash tamper; zip with a path-traversal name; `format` 2 refused; old-version fixture
  (a stored backup with `version: 1` and the newest feature keys) loads; merge vs replace matrices (new/same/changed/only-here, id clash); secret-looking header
  warning; credential names diff; unreadable config disables Restore; `reason`/retention pruning with `TimeProvider`; editor.json window skipped.
- **Cli tests**: `backup`/`restore --dry-run`, exit codes, non-interactive refusal, `--config` throwaway folder.
- **Engine integration** (existing hot-reload harness): restore into a running engine with one bad flow keeps the engine up, the bad flow last-valid/disabled.
- **Editor VM tests**: preview model, "draft open" refusal, undo; screenshots of the card and dialog (light/dark/high contrast/225 %).
- Every test uses a temp `--config` directory and never touches `%APPDATA%`. Live check by the user: back up the real config, change a flow, restore, compare.
- Footprint: `scripts/measure.ps1` unchanged for the engine; the AOT publish size diff is checked (§5).

## 12. Open choices for the user (recommended answer first)
1. **What goes in by default**: config.yaml (flows, library, settings, tray) always; editor layout opt-in; run history opt-in. Rec: **yes**. Alt: also include editor layout by default.
2. **Format**: one `.dcbackup` zip, manifest + byte-for-byte config.yaml, SHA-256 per entry. Rec: **yes**. Alt: a folder of files.
3. **Restore choice**: Replace everything (recommended) or Add what's missing, whole-backup only (no per-flow ticks). Rec: **yes**. Alt: per-flow ticks in v1 (bigger dialog).
4. **Automatic backups**: ON by default, newest 10, taken before an update and after a save (at most one per 24 h), no timers. Rec: **yes (on)**. Alt: off by default, user turns it on in Settings.
5. **Where**: automatic in `%LOCALAPPDATA%\DazeControl\backups\auto`; manual anywhere via the Save dialog; the old per-session saves shown as "Earlier saves". Rec: **yes**. Alt: let the user choose the automatic folder (e.g. OneDrive) — extra Settings field.
6. **Restore into a running engine without restart**, refusing while a flow draft is open, with Undo until the editor closes. Rec: **yes**. Alt: require a restart.
7. **CLI**: `dctl backup` and `dctl restore` (needs `--yes` to apply, `--dry-run` to preview). Rec: **yes**. Alt: editor only.
8. **Check for secrets**: warn when a header or body looks like a secret (the file is still written). Rec: **yes**. Alt: no check.
9. **Unreadable backup config**: Restore disabled (never replaces a working config with an unreadable one). Rec: **yes**. Alt: allow it and let fail-soft keep the last good flows.
