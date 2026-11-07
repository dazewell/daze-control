using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Backup;
using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.BackupCard;

public enum BackupMessageKind { None, Success, Warning, Error }

/// <summary>One line of the recent list. <paramref name="Path"/> is a .dcbackup, or a config-*.yaml for an earlier save (config only).</summary>
public sealed record BackupRowViewModel(string Title, string Subtitle, string Path, bool IsEarlier, bool IsReadable = true);

/// <summary>
/// Settings › Backup and restore (T19.4, D-103): Backup now, Restore from file, the recent list and Undo. All logic is
/// <see cref="BackupService"/>'s; this adds the editor's rules: refuse while a flow draft is open, reload the session after a
/// write, keep the outcome for Undo until the editor closes. Files stay on the UI thread: a backup is tens of KB.
/// </summary>
public sealed partial class BackupCardViewModel : ObservableObject
{
    public const int ListLimit = 10;

    private readonly ConfigSession _session;
    private readonly BackupService _service;
    private readonly IBackupFiles _files;
    private readonly RestoreTarget _target;
    private readonly string _earlierFolder;
    private readonly TimeProvider _time;
    private readonly Action _flushEditorSettings;
    private readonly string _machine;
    private RestoreOutcome? _undo;

    /// <param name="target">Config, editor.json and the safety-copy folder (the automatic folder); a test passes a temp folder.</param>
    /// <param name="earlierFolder">The per-session config-*.yaml saves of <see cref="ConfigStore"/>.</param>
    /// <param name="flushEditorSettings">Writes editor.json's pending change so the backup holds it.</param>
    public BackupCardViewModel(ConfigSession session, BackupService service, IBackupFiles files, RestoreTarget target,
        string earlierFolder, TimeProvider? time = null, Action? flushEditorSettings = null, string? machine = null)
    {
        _session = session;
        _service = service;
        _files = files;
        _target = target;
        _earlierFolder = earlierFolder;
        _time = time ?? TimeProvider.System;
        _flushEditorSettings = flushEditorSettings ?? (() => { });
        _machine = machine ?? Environment.MachineName;
        Refresh();
    }

    public ObservableCollection<BackupRowViewModel> Backups { get; } = [];

    public ObservableCollection<BackupRowViewModel> EarlierSaves { get; } = [];

    [ObservableProperty]
    public partial string AutomaticText { get; private set; } = "";

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial BackupMessageKind MessageKind { get; private set; }

    [ObservableProperty]
    public partial bool CanUndo { get; private set; }

    public bool HasMessage => Message is not null;

    public bool HasBackups => Backups.Count > 0;

    public bool HasEarlierSaves => EarlierSaves.Count > 0;

    /// <summary>Why a restore (or Undo) can't start now; null when it can. A draft is an unsaved edit of an open flow (D-047).</summary>
    public string? RestoreRefusal => _session.Drafts.Count > 0 ? "Save or discard the open flow first." : null;

    public string SuggestedFileName =>
        "DazeControl-backup-" + _time.GetLocalNow().ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture) + BackupFormat.Extension;

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    public void Dismiss() => Show(null, BackupMessageKind.None);

    /// <summary>Rereads the two folders (the page opening, a backup written or deleted).</summary>
    public void Refresh()
    {
        var all = SafeList(() => _service.List(_target.SafetyFolder));
        Backups.Clear();
        foreach (var file in all.Take(ListLimit))
            Backups.Add(RowOf(file));
        EarlierSaves.Clear();
        foreach (var path in SafeList(() => _files.ListFiles(_earlierFolder)).Where(p => EarlierStamp(p) is not null)
                     .OrderByDescending(p => System.IO.Path.GetFileName(p), StringComparer.Ordinal).Take(ListLimit))
            EarlierSaves.Add(new BackupRowViewModel("Earlier save", EarlierStamp(path)!, path, IsEarlier: true));
        AutomaticText = all.Count == 0 ? "No backups in the automatic folder yet."
            : all.Count == 1 ? "1 backup in the automatic folder." : $"{all.Count} backups in the automatic folder.";
        OnPropertyChanged(nameof(HasBackups));
        OnPropertyChanged(nameof(HasEarlierSaves));
    }

    /// <summary>Backup now: writes everything to <paramref name="path"/> (the Save dialog's choice). A taken name gets a counter.</summary>
    public bool BackupNow(string path)
    {
        _session.Flush();
        _flushEditorSettings();
        try
        {
            if (_files.TryReadAll(_target.ConfigPath) is not { } config)
            {
                Show("There's no configuration to back up yet.", BackupMessageKind.Error);
                return false;
            }
            var editor = _target.EditorJsonPath is { } p ? _files.TryReadAll(p) : null;
            var result = _service.CreateAt(path, new BackupContents(config, editor), BackupService.Manual);
            Show(result.Warnings.Count == 0
                ? $"Saved {result.Path}."
                : $"Saved {result.Path}. {string.Join(" ", result.Warnings)}",
                result.Warnings.Count == 0 ? BackupMessageKind.Success : BackupMessageKind.Warning);
            Refresh();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Show($"Couldn't save the backup to {System.IO.Path.GetDirectoryName(path)}: {e.Message}", BackupMessageKind.Error);
            return false;
        }
    }

    /// <summary>Opens a backup (or an earlier save) for the restore dialog; null with a message when it can't be restored.</summary>
    public RestoreDialogViewModel? OpenForRestore(string path, bool earlier = false)
    {
        if (RestoreRefusal is { } refusal)
        {
            Show(refusal, BackupMessageKind.Error);
            return null;
        }
        _session.Flush();
        if (_session.HasUnwrittenChanges)
        {
            Show("Your latest changes aren't saved yet, so nothing was restored.", BackupMessageKind.Error);
            return null;
        }
        try
        {
            var archive = earlier
                ? BackupFormat.Open(BackupFormat.Write(new BackupContents(_files.ReadAll(path)), _time.GetLocalNow(), "", _machine, "earlier-save", out _))
                : _service.Open(path);
            var preview = _service.Preview(archive, _target);
            return new RestoreDialogViewModel(this, archive, preview);
        }
        catch (BackupException e)
        {
            Show(e.Kind switch
            {
                BackupFailure.NotABackup => "This isn't a Daze Control backup.",
                BackupFailure.TooNew => e.Message,
                _ => "This backup is damaged. Nothing was changed.",
            }, BackupMessageKind.Error);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Show($"Couldn't read the file: {e.Message}", BackupMessageKind.Error);
            return null;
        }
    }

    /// <summary>Called by the dialog's Restore. Writes, then reloads the session so every page shows the new state.</summary>
    internal bool Apply(BackupArchive archive, RestoreMode mode)
    {
        if (RestoreRefusal is { } refusal)
        {
            Show(refusal, BackupMessageKind.Error);
            return false;
        }
        try
        {
            var outcome = _service.Restore(archive, mode, _target);
            _session.Load();
            _undo = outcome;
            CanUndo = true;
            var safety = outcome.SafetyPath is null ? "" : " A safety copy was saved first.";
            var notes = outcome.Notes.Count == 0 ? "" : " " + string.Join(" ", outcome.Notes);
            Show("Done. Your flows reloaded." + safety + notes, BackupMessageKind.Success);
            Refresh();
            return true;
        }
        catch (RestoreException e)
        {
            Show(e.Message, BackupMessageKind.Error);
            return false;
        }
    }

    /// <summary>Puts back what the last restore replaced, from its safety copy.</summary>
    public bool Undo()
    {
        if (_undo is not { } outcome)
            return false;
        if (RestoreRefusal is { } refusal)
        {
            Show(refusal, BackupMessageKind.Error);
            return false;
        }
        try
        {
            _service.Undo(outcome, _target);
            _session.Load();
            _undo = null;
            CanUndo = false;
            Show("Restore undone. Your flows are back as they were.", BackupMessageKind.Success);
            Refresh();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BackupException)
        {
            Show($"Couldn't undo the restore: {e.Message}", BackupMessageKind.Error);
            return false;
        }
    }

    public void Delete(BackupRowViewModel row)
    {
        try
        {
            _files.Delete(row.Path);
            Refresh();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Show($"Couldn't delete the backup: {e.Message}", BackupMessageKind.Error);
        }
    }

    private void Show(string? text, BackupMessageKind kind)
    {
        MessageKind = kind;
        Message = text;
    }

    private BackupRowViewModel RowOf(BackupFile file)
    {
        try
        {
            var m = _service.Open(file.Path).Manifest;
            var title = m.Reason switch
            {
                BackupService.BeforeRestore => "Before restore",
                BackupService.BeforeUpdate => "Before update",
                BackupService.AutoSave => "Automatic backup",
                _ => "Backup",
            };
            return new BackupRowViewModel(title, m.Created.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture), file.Path, IsEarlier: false);
        }
        catch (Exception e) when (e is BackupException or IOException or UnauthorizedAccessException)
        {
            return new BackupRowViewModel("Backup", "Can't be read", file.Path, IsEarlier: false, IsReadable: false);
        }
    }

    // config-yyyyMMdd-HHmmss.yaml (ConfigStore) -> "7 Nov 2026, 21:30"; null = not one of ours.
    private static string? EarlierStamp(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (!name.StartsWith("config-", StringComparison.Ordinal) || !name.EndsWith(".yaml", StringComparison.Ordinal))
            return null;
        return DateTime.TryParseExact(name[7..^5], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture)
            : null;
    }

    private static IReadOnlyList<T> SafeList<T>(Func<IReadOnlyList<T>> list)
    {
        try { return list(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
