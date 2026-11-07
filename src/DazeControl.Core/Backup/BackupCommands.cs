using System.Globalization;

namespace DazeControl.Core.Backup;

/// <summary>Where the files of one Daze Control setup live; <c>--config</c> moves them all next to that file (like the editor).</summary>
public sealed record BackupPaths(string Config, string EditorJson, string History, string AutoFolder)
{
    public static BackupPaths For(string? configOverride)
    {
        if (configOverride is not null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(configOverride))!;
            return new(Path.GetFullPath(configOverride), Path.Combine(dir, "editor.json"), Path.Combine(dir, "history.jsonl"),
                Path.Combine(dir, "backups", "auto"));
        }
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.FolderName);
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.FolderName);
        return new(Path.Combine(roaming, "config.yaml"), Path.Combine(local, "editor.json"), Path.Combine(local, "history.jsonl"),
            Path.Combine(local, "backups", "auto"));
    }
}

/// <summary>The <c>dctl backup</c> / <c>dctl restore</c> commands (T19.3, D-103), on top of <see cref="BackupService"/> only.
/// Exit codes: 0 ok, 1 failed, 2 refused (needs <c>--yes</c>, or the editor is open).</summary>
public sealed class BackupCommands(
    BackupService service, IBackupFiles files, BackupPaths paths, TextWriter output, TextWriter error,
    bool interactive, Func<bool> editorRunning, Func<string?>? readAnswer = null)
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Refused = 2;

    public int Backup(string? path, bool editor, bool history)
    {
        byte[]? config;
        try { config = files.TryReadAll(paths.Config); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Can't read {paths.Config}: {e.Message}");
            return Failed;
        }
        if (config is null)
        {
            error.WriteLine($"There is no config.yaml at {paths.Config} to back up.");
            return Failed;
        }
        try
        {
            var contents = new BackupContents(config, editor ? files.TryReadAll(paths.EditorJson) : null,
                history ? files.TryReadAll(paths.History) : null);
            var result = path is null ? service.CreateIn(paths.AutoFolder, contents, BackupService.Cli)
                : Directory.Exists(path) || path.EndsWith('\\') || path.EndsWith('/') ? service.CreateIn(path, contents, BackupService.Cli)
                : service.CreateAt(path, contents, BackupService.Cli);
            output.WriteLine($"Backed up to {result.Path}");
            if (editor && contents.EditorJson is null) output.WriteLine("There is no editor layout on this PC yet, so it isn't in the backup.");
            if (history && contents.History is null) output.WriteLine("There is no run history on this PC yet, so it isn't in the backup.");
            foreach (var w in result.Warnings) output.WriteLine("Warning: " + w);
            return Ok;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Couldn't save the backup to {path ?? paths.AutoFolder}: {e.Message}");
            return Failed;
        }
    }

    public int Restore(string file, RestoreMode mode, bool yes, bool dryRun, bool editorLayout)
    {
        BackupArchive archive;
        try { archive = service.Open(file); }
        catch (BackupException e) { error.WriteLine(e.Message); return Failed; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Can't read {file}: {e.Message}");
            return Failed;
        }
        var target = new RestoreTarget(paths.Config, paths.EditorJson, paths.AutoFolder);
        RestorePreview preview;
        try { preview = service.Preview(archive, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Can't read {paths.Config}: {e.Message}");
            return Failed;
        }
        PrintPreview(preview, mode, editorLayout && archive.EditorJson is not null);
        if (preview.Unreadable is not null) return Failed;
        if (mode == RestoreMode.AddMissing && !preview.CanAddMissing)
        {
            error.WriteLine("The config on this PC can't be read, so nothing can be merged. Use --replace.");
            return Failed;
        }
        if (dryRun)
        {
            output.WriteLine("Dry run: nothing was changed.");
            return Ok;
        }
        if (!yes && !Confirm())
        {
            error.WriteLine(interactive ? "Nothing was restored." : "Nothing was restored. Add --yes to restore without asking.");
            return Refused;
        }
        // Checked last, right before the write, so an editor opened while the question was waiting is seen too.
        if (editorRunning())
        {
            error.WriteLine("The editor is open. Close it first (it may hold a flow with unsaved changes), then run this again.");
            return Refused;
        }
        try
        {
            var outcome = service.Restore(archive, mode, target, editorLayout);
            output.WriteLine(mode == RestoreMode.Replace ? "Restored: everything now matches the backup." : "Restored: what was missing is added.");
            if (outcome.SafetyPath is { } safety)
                output.WriteLine($"Your previous config is saved as {safety}. To undo: dctl restore \"{safety}\" --replace --yes");
            foreach (var kept in outcome.KeptCurrent) output.WriteLine($"Kept the version on this PC: {kept}");
            foreach (var note in outcome.Notes) output.WriteLine(note);
            output.WriteLine("Desktop shortcuts and Start with Windows are not restored; turn them on again.");
            output.WriteLine("If Daze Control is running, your flows reload by themselves.");
            return Ok;
        }
        catch (RestoreException e)
        {
            error.WriteLine(e.Message);
            return Failed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Every read the restore does (config, editor.json) comes before its first write, so nothing was changed.
            error.WriteLine($"Couldn't read a file for the restore: {e.Message} Nothing was changed.");
            return Failed;
        }
    }

    private bool Confirm()
    {
        if (!interactive) return false;
        output.Write("Restore now? [y/N] ");
        output.Flush();
        var answer = (readAnswer?.Invoke() ?? Console.In.ReadLine())?.Trim();
        return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private void PrintPreview(RestorePreview p, RestoreMode mode, bool editorLayout)
    {
        var m = p.Manifest;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Backup made on {m.Machine}, {m.Created:yyyy-MM-dd HH:mm} (Daze Control {m.App}, {m.Reason})"));
        if (p.Unreadable is not null)
        {
            error.WriteLine(p.Unreadable);
            error.WriteLine("Nothing can be restored from this backup.");
            return;
        }
        output.WriteLine(mode == RestoreMode.Replace
            ? "Mode: replace everything (the PC ends up like the backup; what is only here is removed)."
            : "Mode: add what's missing (new items are added; items that exist keep the version on this PC; nothing is removed).");
        foreach (var kind in p.Items.Select(i => i.Kind).Distinct())
        {
            var parts = new List<string>();
            foreach (var (state, label) in new[] { (ChangeState.New, "new"), (ChangeState.Changed, "changed"),
                         (ChangeState.OnlyHere, "only on this PC"), (ChangeState.Same, "the same") })
                if (p.Count(kind, state) is > 0 and var n) parts.Add($"{n} {label}");
            output.WriteLine($"{Plural(kind)}: {string.Join(", ", parts)}");
        }
        output.WriteLine("Settings: " + (p.SettingsChanged ? "changed" : "the same"));
        foreach (var i in p.Items.Where(i => i.State != ChangeState.Same))
        {
            var label = i.State switch { ChangeState.New => "new", ChangeState.Changed => "changed", _ => "only here" };
            output.WriteLine($"  {label,-9} {i.Kind} {i.Key}" + (i.Summary is null ? "" : " — " + i.Summary));
        }
        if (editorLayout) output.WriteLine("The editor layout is restored too (not the window position).");
        if (p.NeedsAttention.Count > 0)
        {
            output.WriteLine("Needs attention:");
            foreach (var a in p.NeedsAttention) output.WriteLine("  - " + a);
        }
        foreach (var w in p.Warnings) output.WriteLine("Warning: " + w);
    }

    private static string Plural(string kind) => kind switch { "flow" => "Flows", _ => char.ToUpperInvariant(kind[0]) + kind[1..] + " items" };
}
