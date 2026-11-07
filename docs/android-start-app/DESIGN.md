# Android "Start app" (M17)

User ask (2026-11-06): a step that launches an app on the Android device, picked from a searchable list of installed
apps sorted by name (the app's name, with the package id as the secondary line), with typing the package as the fallback.
Decisions: D-092 (labels), D-093 (cache), D-094 (step and picker). This file is cut once the as-built facts move to
ARCHITECTURE/FEATURES/UX (T17.3).

## 1. What adb can tell (checked read-only on the user's Android TV box, Android 14, 2026-11-06)

| Source | Gives | Verdict |
|---|---|---|
| `pm list packages -3` | package ids of user-installed apps | ids only; includes apps with no launcher icon |
| `cmd package query-activities -a MAIN -c LAUNCHER` (and `LEANBACK_LAUNCHER`) | every launchable app: `package/activity`, `labelRes=0x7f…` and, **when the app set a literal name, `nonLocalizedLabel=Name`** | the only list of *launchable* apps; ~85 KB raw, 2.5 KB after an on-device `grep` |
| `dumpsys package <pkg>`, `dumpsys activity recents` | no label at all | rejected |
| the APK's `resources.arsc` (pull + parse, what `aapt` does) | the real name | rejected: tens of MB pulled per device, a binary-resource parser to maintain, slow (D-092) |

Most apps (all system ones, many store apps) only have `labelRes`, a resource id adb cannot resolve. So a real name is
not available for them over adb alone.

## 2. Labels (D-092)

Per launchable package, in order: the literal `nonLocalizedLabel` when the app has one → a small built-in table of
well-known ids (YouTube, Netflix, Kodi, Spotify, Settings, …) → the package id turned into words (`com.vk.tv` → "Vk").
The package id is always shown as the second line, so a guessed name is never the only clue, and the step stores the
package id. Sorted by name (culture-aware, case-insensitive), ties by package id.

## 3. List and cache (D-093)

- The editor asks the engine (`android-apps` request: the device's address and optional adb path from the open
  config, `refresh` yes/no). Without `refresh` the engine answers from its file cache and never touches adb. With it
  the engine connects, runs one `adb shell` (both launcher queries, `grep`-reduced on the device) and rewrites the cache.
- Cache: `%LOCALAPPDATA%\DazeControl\android-apps\<serial>.json` (package, name), written only after a successful
  listing. A device that is off still gets its last list. Nothing runs between requests: no timer, no resident
  process (Idle = zero). The editor asks for `refresh` once when the user focuses the app box (a user action), and
  shows the cached list at once when the step is selected.
- Failure is soft: the box keeps working as free text with a one-line hint ("Couldn't read the apps: …").

## 4. The step and the picker (D-094)

- `android.start` "Start Android app": `device` (library ref, as the other Android steps), `app` (package id,
  required). Timeout 20 s, lock `adb:{device}`. Run: connect (as today), then
  `am start -a MAIN -c LAUNCHER -p <pkg>`; if that cannot resolve, the same with `LEANBACK_LAUNCHER`; if neither,
  the step fails "… isn't installed or has no icon to open." `am start` exits 0 on failure, so the answer text decides.
  The package must be `[A-Za-z0-9_.]+` (it goes into a shell line). Dry run: the generic dry provider, nothing to add.
- Picker: an AutoSuggestBox (keyboard: type, Up/Down, Enter, Esc). Typing filters the list by name or package
  (contains, case-insensitive); choosing a row writes its package id; Enter on text with no match writes the text
  as the package. Rows: name, package id underneath. Under the box, the name of the stored package when known.
- Not built: checks (`android.check`, T14.9 stays parked), launching with extras/intents, stopping apps, a Library page.

## 5. Not verifiable without the user's phone

The user's connected device is an Android TV box; a phone's launcher categories are the same, but `am start -p` and the
grep-reduced listing are only verified on that box (Android 14). The launch itself is never run live here (it changes
what is on the user's TV); unit tests use a fake adb.
