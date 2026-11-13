# Step values: what is still open

Built in T23.1-T23.7 (PRs 223-230); as-built facts moved to ARCHITECTURE ("Step values"), FEATURES (F10) and UX (§5.4 "Values",
§5.9 M23) (D-053). Decisions: D-129 (model, syntax, why not variables), D-130 (device values), D-131 (text inputs, outputs, Make text).
This file keeps only what is **not built or not yet checked live**.

## Not built (the design asked for it, the code doesn't do it)

- **PowerShell "Show values" link** (old §5.8): the script box has no Insert button and no link; its help line tells the person to
  read `$env:DAZE_<STEP>_<VALUE>`. Built only if people find that hard.
- **Delete confirmation sentence** (old §5.6): the editor deletes without a dialog (Undo), so "Show message uses its Device" is never shown;
  references become their fallback text silently.
- **Insert value for list settings**: `process.run` arguments take values (`Fill: Argument`) but the Insert flyout does not list them there:
  write `{p.output}` by hand. Pills are also missing for flow outputs and a call's text inputs (plain boxes; Make text gives pills).
- **A whole value in a number setting** (old §4.3: `{batt.percent}` in a number setting, range checked at run time): still an error in
  the validator; only text settings take values.
- **Asking for text when a flow starts**: needs a typing prompt, which needs focus (D-107 "text later"); `ask: true` on a text input is an error.
- **Renaming a flow output** in the editor changes only the open flow; callers show their fallback with a warning (D-131).
  `FlowOutputs.Rename` is there for a config-level rename.
- Format specifiers (`{x:0.0}`), a Duration kind, values that survive a restart (flags, D-029): not proposed.

## Not checked live

- The XAML in light, dark, high contrast and 225 % text, and Narrator: pills row, Insert value flyout (group headers are disabled menu rows and
  the help text is a tooltip, a `MenuFlyout` limit), "Values it gives", text input rows, "Values it gives back". UX §5.9 M23 lists what a human tries.
- No live run against real hardware: Bluetooth names in a message, `audio.setDefault`'s `previous` (the Core Audio default read before the
  switch), `android.open` with a value, a real engine run of `flow.run` with text inputs/outputs.
- `scripts/measure.ps1` idle gate after M23 (engine image grew 15,853,568 to 16,111,616 B; nothing resident was added).
