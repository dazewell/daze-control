# Backup and restore (M19) — what is left

Built in T19.1–T19.5; as-built facts moved to ARCHITECTURE (Core/Backup, Backup surfaces), FEATURES (F9, F8) and UX (§5.1, §5.9 M19) (D-053). Decisions: D-103, D-104.
Text of the original design is in git history (before the T19.6 commit).

## Not built
- **Editor layout tick** on restore: `EditorSettingsStore` can't reload a restored editor.json yet; the file is written, the running editor keeps its own view state.
- **Per-flow restore** from a backup (non-goal in v1; the Flows list's export/import is M16's).
- **Choose the automatic folder** (e.g. OneDrive): rejected for v1 (D-103 choice 5), revisit on request.

## Not checked live
The M19 row of the UX acceptance list (UX §5.9): high contrast, 225 % text, and a real back-up / change / restore run against the installed engine (ask the user first: it replaces their config; take a backup first).
