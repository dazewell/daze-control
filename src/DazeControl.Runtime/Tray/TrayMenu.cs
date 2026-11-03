using DazeControl.Core;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;

namespace DazeControl.Runtime.Tray;

public abstract record TrayEntry;

/// <param name="Shortcut">Shown right-aligned ("Alt+Z").</param>
/// <param name="Enabled">False for a flow that needs fixing (greyed).</param>
/// <param name="Running">Gets the radio bullet; picking it applies "If it's already running".</param>
public sealed record TrayFlowItem(string FlowId, string Text, string? Shortcut, bool Enabled, bool Running) : TrayEntry;

public sealed record TrayFolder(string Text, IReadOnlyList<TrayEntry> Items) : TrayEntry;

public sealed record TraySeparator : TrayEntry
{
    public static TraySeparator Instance { get; } = new();
}

public enum TrayCommand
{
    OpenEditor,
    PauseShortcuts,
    Exit,
}

public sealed record TrayCommandItem(TrayCommand Command, string Text, bool Checked = false, bool Enabled = true) : TrayEntry;

/// <summary>What the tray knows besides the config when it opens its menu or redraws its icon.</summary>
/// <param name="Running">Flow ids running now.</param>
/// <param name="Shortcut">The shortcut to show for a flow id, if any.</param>
/// <param name="Paused">Shortcuts are paused.</param>
/// <param name="EditorAvailable">DazeControl.Editor.exe sits next to the engine.</param>
public sealed record TrayState(IReadOnlySet<string> Running, Func<string, string?> Shortcut, bool Paused, bool EditorAvailable);

/// <summary>How the user reached the tray icon (UX §5.6).</summary>
public enum TrayGesture
{
    /// <summary>Left click, or Enter/Space on the focused icon.</summary>
    Select,

    /// <summary>Right click, or Shift+F10/Apps on the focused icon.</summary>
    ContextMenu,
}

/// <summary>
/// The tray menu as data (UX §5.6), so its rules are tested without Win32: flows in file order, folders as submenus
/// at their first flow's place, "line above it" separators, the running bullet, a never-valid flow greyed
/// "Name (needs fixing)"; then Open Daze Control · Pause shortcuts · Exit. Hidden: turned-off flows and flows with
/// <c>tray.show: false</c>. A flow whose file version is broken but whose last valid version still runs (D-021)
/// shows normally.
/// </summary>
public static class TrayMenu
{
    /// <summary>Select opens the editor; the menu comes on a right click, or on Select when no editor is installed.</summary>
    public static bool OpensEditor(TrayGesture gesture, bool editorInstalled) =>
        gesture == TrayGesture.Select && editorInstalled;

    public static IReadOnlyList<TrayEntry> Build(ConfigSnapshot snapshot, TrayState state)
    {
        var top = new List<TrayEntry>();
        var folders = new Dictionary<string, List<TrayEntry>>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var flowState in snapshot.Flows)
        {
            var flow = flowState.Running ?? flowState.Written;
            if (flow is null || !flow.Enabled || !flow.Tray.Show)
                continue;
            var runnable = flowState.Running is not null;
            var item = new TrayFlowItem(flow.Id, runnable ? flow.Name : $"{flow.Name} (needs fixing)",
                runnable ? state.Shortcut(flow.Id) : null, runnable, state.Running.Contains(flow.Id));

            var container = top;
            if (flow.Tray.Folder is { Length: > 0 } folder)
            {
                if (!folders.TryGetValue(folder, out var items))
                {
                    folders[folder] = items = [];
                    top.Add(new TrayFolder(folder, items));
                }
                container = items;
            }
            if (flow.Tray.Separator && container.Count > 0 && container[^1] is not TraySeparator)
                container.Add(TraySeparator.Instance);
            container.Add(item);
        }

        if (top.Count > 0)
            top.Add(TraySeparator.Instance);
        top.Add(new TrayCommandItem(TrayCommand.OpenEditor, $"Open {AppInfo.ProductName}", Enabled: state.EditorAvailable));
        top.Add(new TrayCommandItem(TrayCommand.PauseShortcuts, "Pause shortcuts", Checked: state.Paused));
        top.Add(TraySeparator.Instance);
        top.Add(new TrayCommandItem(TrayCommand.Exit, "Exit"));
        return top;
    }

    /// <summary>
    /// The icon's tooltip (F6): what runs now, else that shortcuts are paused, else the last run's failure. Windows
    /// cuts tooltips at 127 characters.
    /// </summary>
    /// <param name="runningNames">Names of the flows running now, in start order.</param>
    /// <param name="lastProblem">From <see cref="Problem"/> for the last finished run.</param>
    public static string Tooltip(IReadOnlyList<string> runningNames, bool paused, string? lastProblem)
    {
        var text = runningNames.Count > 0 ? $"{AppInfo.ProductName} · Running {string.Join(", ", runningNames)}"
            : paused ? $"{AppInfo.ProductName} · Shortcuts paused"
            : lastProblem is not null ? $"{AppInfo.ProductName} · {lastProblem}"
            : AppInfo.ProductName;
        return text.Length <= 127 ? text : string.Concat(text.AsSpan(0, 126), "…");
    }

    /// <summary>What the tooltip says about a finished run (UX §5.6 wording); null when it went fine or was cancelled.</summary>
    public static string? Problem(string flowName, FlowOutcome outcome) => outcome switch
    {
        FlowOutcome.Failed => $"{flowName} didn't finish",
        FlowOutcome.PartiallySucceeded => $"{flowName}: done, with a problem",
        _ => null,
    };
}
