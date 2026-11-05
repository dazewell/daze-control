using DazeControl.Editor.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DazeControl.Editor;

/// <summary>The Flows list's two row kinds: a group header and a flow card (D-087).</summary>
internal sealed partial class FlowRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GroupTemplate { get; set; }

    public DataTemplate? FlowTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is GroupItemViewModel ? GroupTemplate : FlowTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>x:Bind helpers for the member rows of a group.</summary>
internal static class RowFormat
{
    public const double IndentDips = 24;

    /// <summary>A selected card or header (D-090) gets an accent outline; others keep the card stroke.</summary>
    public static Microsoft.UI.Xaml.Media.Brush SelectBorder(bool selected) =>
        selected ? Canvas.Theme.Selection : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

    // 12 DIP in, then a 1-DIP rail and 11 DIP of padding: the card sits 24 in from the list edge.
    public static Thickness Indent(bool inGroup) => inGroup ? new Thickness(12, 0, 0, 0) : new Thickness(0);

    public static Thickness Rail(bool inGroup) => inGroup ? new Thickness(1, 0, 0, 0) : new Thickness(0);

    public static Thickness RailPadding(bool inGroup) => inGroup ? new Thickness(11, 0, 0, 0) : new Thickness(0);
}
