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
/// The configuration being edited. Library and settings edits go through <see cref="Update"/>; the file is written
/// <see cref="SaveDelay"/> after the last one. Edits to a flow open in the editor go through <see cref="UpdateDraft"/>
/// and stay a draft until <see cref="SaveDraft"/> (D-047): <see cref="Current"/> shows drafts, the file never has them.
/// UI thread only: timer callbacks come back through <c>post</c>. A file that can't be read at all is never
/// overwritten (<see cref="CanEdit"/>).
/// </summary>
public sealed partial class ConfigSession : ObservableObject, IDisposable
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);

    private readonly ConfigStore _store;
    private readonly ITimer _saveTimer;
    private bool _dirty;

    /// <summary>What the file has, or will have once the pending save runs: <see cref="Current"/> without drafts.</summary>
    private Configuration _saved = Configuration.Empty;
    private readonly HashSet<string> _drafts = new(StringComparer.Ordinal);

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

    /// <summary>Edits not in the file yet (pending, conflicting, failed, or a draft): the window must not just close.</summary>
    public bool HasUnsavedChanges => _dirty || _drafts.Count > 0;

    /// <summary>Edits not in the file yet apart from drafts: pending, conflicting or failed.</summary>
    public bool HasUnwrittenChanges => _dirty;

    /// <summary>Flows with a draft (edited, not saved), in no order.</summary>
    public IReadOnlyCollection<string> Drafts => _drafts;

    public bool HasDraft(string flowId) => _drafts.Contains(flowId);

    /// <summary>The flow as saved (null for a flow that exists only as a draft, or not at all).</summary>
    public Flow? SavedFlow(string flowId) => _saved.Flows.FirstOrDefault(f => f.Id == flowId);

    public void Load()
    {
        _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _dirty = false;
        var drafts = _drafts.Select(id => Current.Flows.FirstOrDefault(f => f.Id == id)).OfType<Flow>().ToList();
        ClearDrafts();
        try
        {
            var load = _store.Load();
            var mappingError = load.Problems.FirstOrDefault(p => p.Severity == Severity.Error);
            if (load.Config is { } config && mappingError is null)
            {
                LoadError = null;
                _saved = config;
                // A reload (another program wrote the file) keeps the editor's drafts on top of the new version.
                Current = drafts.Aggregate(config, WithFlow);
                foreach (var draft in drafts.Where(d => SavedFlow(d.Id) != d))
                    _drafts.Add(draft.Id);
                Problems = [.. load.Problems, .. ConfigValidator.Validate(Current, load.Problems)];
                CanEdit = true;
                if (_drafts.Count > 0)
                    OnPropertyChanged(nameof(Drafts));
            }
            else if (load.Config is { } partial)
            {
                // Mapping dropped what it couldn't read; saving would delete it from the file.
                LoadError = $"config.yaml has errors, so the editor won't change it until they're fixed by hand. {mappingError!.Path}: {mappingError.Message}";
                Current = _saved = partial;
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

    /// <summary>
    /// Applies an edit now and schedules the save. It applies to the saved version and to <see cref="Current"/> alike,
    /// so a Library rename reaches a draft too, while the draft's own edits stay out of the file.
    /// </summary>
    public void Update(Func<Configuration, Configuration> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!CanEdit)
            return;
        var next = change(Current);
        if (next == Current)
            return;
        var saved = _drafts.Count == 0 ? next : change(_saved);
        var gone = _drafts.Where(id => next.Flows.All(f => f.Id != id)).ToList(); // a removed flow has no draft left
        foreach (var id in gone)
            _drafts.Remove(id);
        if (saved != _saved)
        {
            _saved = saved;
            _dirty = true;
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
        Show(next);
        if (gone.Count > 0)
            OnPropertyChanged(nameof(Drafts));
    }

    /// <summary>An edit to one flow in the editor: shown everywhere, written only by <see cref="SaveDraft"/>.</summary>
    public void UpdateDraft(string flowId, Func<Flow, Flow> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!CanEdit || Current.Flows.FirstOrDefault(f => f.Id == flowId) is not { } flow)
            return;
        var next = change(flow);
        if (next == flow)
            return;
        // Undoing back to the saved version leaves nothing to save.
        if (next == SavedFlow(flowId))
            _drafts.Remove(flowId);
        else
            _drafts.Add(flowId);
        Show(WithFlow(Current, next));
        OnPropertyChanged(nameof(Drafts));
    }

    /// <summary>Save: the draft goes to the file now (no debounce: the user asked).</summary>
    public void SaveDraft(string flowId)
    {
        if (!_drafts.Remove(flowId) || Current.Flows.FirstOrDefault(f => f.Id == flowId) is not { } flow)
            return;
        _saved = WithFlow(_saved, flow);
        _dirty = true;
        OnPropertyChanged(nameof(Drafts));
        _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Flush();
    }

    /// <summary>Cancel / Discard: the flow goes back to its saved version.</summary>
    public void DiscardDraft(string flowId)
    {
        if (!_drafts.Remove(flowId))
            return;
        Show(SavedFlow(flowId) is { } saved
            ? WithFlow(Current, saved)
            : Current with { Flows = [.. Current.Flows.Where(f => f.Id != flowId)] });
        OnPropertyChanged(nameof(Drafts));
    }

    private void Show(Configuration next)
    {
        Current = next;
        Problems = ConfigValidator.Validate(next);
    }

    private void ClearDrafts()
    {
        if (_drafts.Count == 0)
            return;
        _drafts.Clear();
        OnPropertyChanged(nameof(Drafts));
    }

    /// <summary>The configuration with <paramref name="flow"/> in place of the one with its id (added at the end if none).</summary>
    private static Configuration WithFlow(Configuration config, Flow flow) =>
        config.Flows.Any(f => f.Id == flow.Id)
            ? config with { Flows = [.. config.Flows.Select(f => f.Id == flow.Id ? flow : f)] }
            : config with { Flows = [.. config.Flows, flow] };

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
            _store.Save(_saved);
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
        if (!HasLoadError) // a failed reload shows its own error instead
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
