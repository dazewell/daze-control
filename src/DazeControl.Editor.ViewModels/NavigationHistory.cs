namespace DazeControl.Editor.ViewModels;

/// <summary>Where the editor is: a page, or (on Flows) one open flow (D-095).</summary>
public readonly record struct EditorLocation(EditorPage Page, string? FlowId = null);

/// <summary>
/// Back/forward through the places the editor has shown (D-095): bounded, one per window, no duplicates in a row.
/// A new place made by the user drops what was "forward", like a browser.
/// </summary>
public sealed class NavigationHistory
{
    public const int Limit = 50;

    private readonly List<EditorLocation> _back = [];
    private readonly Stack<EditorLocation> _forward = new();

    public EditorLocation Current { get; private set; } = new(EditorPage.Flows);

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>The user went somewhere new.</summary>
    public void Visit(EditorLocation location)
    {
        if (location == Current)
            return;
        _back.Add(Current);
        if (_back.Count > Limit)
            _back.RemoveAt(0);
        _forward.Clear();
        Current = location;
    }

    /// <summary>The place a Back would go to, skipping places that are gone; null when none.</summary>
    public EditorLocation? PeekBack(Func<EditorLocation, bool> exists)
    {
        while (_back.Count > 0)
        {
            var target = _back[^1];
            if (exists(target))
                return target;
            _back.RemoveAt(_back.Count - 1);
        }
        return null;
    }

    public EditorLocation? PeekForward(Func<EditorLocation, bool> exists)
    {
        while (_forward.Count > 0)
        {
            var target = _forward.Peek();
            if (exists(target))
                return target;
            _forward.Pop();
        }
        return null;
    }

    /// <summary>Back was done to the place <see cref="PeekBack"/> returned.</summary>
    public void CommitBack()
    {
        _forward.Push(Current);
        Current = _back[^1];
        _back.RemoveAt(_back.Count - 1);
    }

    public void CommitForward()
    {
        _back.Add(Current);
        Current = _forward.Pop();
    }

    /// <summary>The open flow was renamed (D-075): its places follow.</summary>
    public void Rename(string oldId, string newId)
    {
        EditorLocation Fix(EditorLocation l) => l.FlowId == oldId ? l with { FlowId = newId } : l;
        for (var i = 0; i < _back.Count; i++)
            _back[i] = Fix(_back[i]);
        var forward = _forward.Reverse().Select(Fix).ToArray();
        _forward.Clear();
        foreach (var l in forward)
            _forward.Push(l);
        Current = Fix(Current);
    }
}
