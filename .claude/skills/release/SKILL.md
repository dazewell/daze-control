---
name: release
description: Cut a Daze Control release (tag, CI wait, Release workflow, notes, asset check) with one script. Use ONLY when the user explicitly asks for a release in chat ("cut a release", "release it", "ship v0.x"); never on your own initiative.
---

# Release

`scripts/cut-release.ps1` does everything deterministic and prints one line per phase. You write the notes and call it.

Rules:
- Never push a `v*` tag unless the user asked for a release in chat this session (D-043). A past approval does not carry over.
- The tag is the version, PRs never bump `<Version>` (D-079). Merge commits only; never `--admin` / `--squash`.
- Don't poll CI with model turns: the script blocks. Run it with `run_in_background`; you are re-invoked when it exits.

Steps:
1. Find what merged since the last tag: `git fetch origin main --tags; git log --first-parent --format="%s%n%b" <last tag>..origin/main | Select-String '^(feat|fix)[(:]'`.
2. Write the notes file in the scratchpad (not the repo): a short bullet list, one line each, in user terms (what the user sees; no internals, no PR numbers). From v0.39 on only.
3. `pwsh scripts/cut-release.ps1 -DryRun` to confirm version and sha (default: latest tag, minor + 1, right when any title is a `feat`; for fixes only pass `-Version` with patch + 1; pass `-Version vX.Y.Z` for a patch or other number, `-Prs 130,131` to wait for PRs first).
4. `pwsh scripts/cut-release.ps1 -Notes <file> [-Prs …] [-Version …]` in the background.

Release candidate (user asks for "RC" / "pre-release"): add `-Rc` (next minor + `-rc.<n>`, e.g. `v0.52.0-rc.1`). The release is marked
pre-release, so installed apps (stable only) do not update to it; the user installs its Setup.exe by hand. Say so in the final reply, and
list what is unverified. The stable release of the same number is cut later with no `-Rc`.

Exit codes: 0 done | 2 PR closed unmerged | 3 CI red (URL printed) | 4 release run red | 5 preflight/git/gh error | 6 timeout.
On 3/4 report the URL and stop; don't retag. If a tag was pushed and the run failed, ask the user.

Final reply to the user: version, one line of CI/release status, the bullets, the release link, and the things the user said were unverified or untested (say so honestly, e.g. "not tested live: …").
