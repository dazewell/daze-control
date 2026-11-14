using CommunityToolkit.Mvvm.ComponentModel;
using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Editor.ViewModels.Inspector;

/// <summary>
/// One drawn edge at the selected step, in the inspector's Connections list: "Runs after Connect" or "Show a message
/// runs after this", with its condition as a ComboBox (UX §5.4), so a fallback never needs the edge's context menu.
/// </summary>
public sealed partial class ConnectionViewModel : ObservableObject
{
    /// <summary>The ComboBox's items, in <see cref="WhenIndex"/> order.</summary>
    public static IReadOnlyList<string> Conditions { get; } = ["When it works", "Only if it fails", "Whatever happens"];

    private readonly Action<ConnectionViewModel, EdgeWhen> _changed;
    private bool _loading = true;

    internal ConnectionViewModel(string from, string to, string text, EdgeWhen when, Action<ConnectionViewModel, EdgeWhen> changed)
    {
        From = from;
        To = to;
        Text = text;
        _changed = changed;
        WhenIndex = Index(when);
        _loading = false;
    }

    public string From { get; }

    public string To { get; }

    /// <summary>"Runs after Connect" (into this step) or "Show a message runs after this" (out of it).</summary>
    public string Text { get; }

    /// <summary>0 when it works, 1 only if it fails, 2 whatever happens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial int WhenIndex { get; set; }

    public string AutomationName => $"{Text}: {Conditions[Math.Clamp(WhenIndex, 0, Conditions.Count - 1)]}";

    public static int Index(EdgeWhen when) => when switch
    {
        EdgeWhen.Failure => 1,
        EdgeWhen.Always => 2,
        _ => 0,
    };

    public static EdgeWhen When(int index) => index switch
    {
        1 => EdgeWhen.Failure,
        2 => EdgeWhen.Always,
        _ => EdgeWhen.Success,
    };

    partial void OnWhenIndexChanged(int value)
    {
        if (!_loading && value >= 0)
            _changed(this, When(value));
    }
}
