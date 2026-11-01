using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;

namespace DazeControl.Editor.ViewModels.Config;

public enum OutsideChange
{
    None,

    /// <summary>Nothing unsaved here, so the editor took the file's version.</summary>
    Reloaded,

    /// <summary>Unsaved edits here and a different file on disk: autosave waits for the user.</summary>
    Conflict,
}

/// <summary>
/// The configuration being edited. Every edit goes through <see cref="Update"/>; the file is written
/// <see cref="SaveDelay"/> after the last one (UX §5.2). UI thread only: timer callbacks come back through
/// <c>post</c>. A file that can't be read at all is never overwritten (<see cref="CanEdit"/>).
/// </summary>
public sealed partial class ConfigSession : ObservableObject, IDisposable
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);

    private readonly ConfigStore _store;
    private readonly ITimer _saveTimer;
    private bool _dirty;

    /// <param name="post">Runs an action on the UI thread (the dispatcher; inline in tests).</param>
    public ConfigSession(ConfigStore store, TimeProvider time, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(post);
        _store = store;
        _saveTimer = time.CreateTimer(_ => post(Flush), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public string Path => _store.Path;

    [ObservableProperty]
    public partial Configuration Current { get; private set; } = Configuration.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<Problem> Problems { get; private set; } = [];

    /// <summary>False when the file isn't usable YAML or has a newer schema: hand edits must not be lost.</summary>
    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    public partial string? LoadError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    public partial string? SaveError { get; private set; }

    [ObservableProperty]
    public partial OutsideChange OutsideChange { get; private set; }

    public bool HasLoadError => LoadError is not null;

    public bool HasSaveError => SaveError is not null;

    public void Load()
    {
        _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _dirty = false;
        try
        {
            var load = _store.Load();
            var mappingError = load.Problems.FirstOrDefault(p => p.Severity == Severity.Error);
            if (load.Config is { } config && mappingError is null)
            {
                LoadError = null;
                Current = config;
                Problems = [.. load.Problems, .. ConfigValidator.Validate(config, load.Problems)];
                CanEdit = true;
            }
            else if (load.Config is { } partial)
            {
                // Mapping dropped what it couldn't read; saving would delete it from the file.
                LoadError = $"config.yaml has errors, so the editor won't change it until they're fixed by hand. {mappingError!.Path}: {mappingError.Message}";
                Current = partial;
                Problems = [.. load.Problems, .. ConfigValidator.Validate(partial, load.Problems)];
                CanEdit = false;
            }
            else
            {
                LoadError = $"config.yaml can't be read, so the editor won't change it. {(load.Problems.Count > 0 ? load.Problems[0].Message : "")}".TrimEnd();
                Problems = load.Problems;
                CanEdit = false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LoadError = $"config.yaml can't be opened: {e.Message}";
            CanEdit = false;
        }
        OutsideChange = OutsideChange.None;
    }

    /// <summary>Applies an edit now and schedules the save.</summary>
    public void Update(Func<Configuration, Configuration> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!CanEdit)
            return;
        var next = change(Current);
        if (next == Current)
            return;
        Current = next;
        Problems = ConfigValidator.Validate(next);
        _dirty = true;
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Writes pending edits now (window closing, a timer firing).</summary>
    public void Flush() => Save(overwrite: false);

    /// <param name="overwrite">Keep mine: write even though the file changed since it was read.</param>
    private void Save(bool overwrite)
    {
        if (!_dirty || OutsideChange == OutsideChange.Conflict)
            return;
        // The watcher can be late (an edit 200 ms before someone else's write): check right before writing.
        if (!overwrite && _store.ChangedOnDisk())
        {
            OutsideChange = OutsideChange.Conflict;
            return;
        }
        try
        {
            _store.Save(Current);
            _dirty = false;
            SaveError = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SaveError = $"Your changes aren't saved yet: {e.Message}"; // the next edit tries again
        }
    }

    /// <summary>The file watcher saw a change: ours (ignored) or someone else's.</summary>
    public void CheckOutsideChange()
    {
        if (!_store.ChangedOnDisk())
            return;
        if (_dirty)
        {
            OutsideChange = OutsideChange.Conflict;
            return;
        }
        Load();
        OutsideChange = OutsideChange.Reloaded;
    }

    /// <summary>Conflict: take the file's version and drop the edits here.</summary>
    [RelayCommand]
    private void Reload() => Load();

    /// <summary>Conflict: write the edits here over the file.</summary>
    [RelayCommand]
    private void KeepMine()
    {
        OutsideChange = OutsideChange.None;
        Save(overwrite: true);
    }

    [RelayCommand]
    private void DismissOutsideChange()
    {
        if (OutsideChange == OutsideChange.Reloaded)
            OutsideChange = OutsideChange.None;
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }
}
