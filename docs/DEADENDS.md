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
