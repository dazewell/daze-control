using System.ComponentModel;
using DazeControl.Editor.ViewModels.Canvas;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DazeControl.Editor.Canvas;

/// <summary>Theme brushes and styles looked up in code; <see cref="FlowCanvas"/> re-applies them on a theme change.</summary>
internal static class Theme
{
    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();

    public static bool HighContrast => Accessibility.HighContrast;

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>The accent fill vanishes against a contrast theme's background; use the system highlight there.</summary>
    public static Brush Selection => Brush(HighContrast ? "SystemColorHighlightColorBrush" : "AccentFillColorDefaultBrush");

    /// <summary>Selected/problem outline; 3 px in a contrast theme (UX §5.3).</summary>
    public static double Emphasis => HighContrast ? 3 : 2;

    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static DoubleCollection Dashes(params double[] values)
    {
        var dashes = new DoubleCollection();
        foreach (var value in values)
            dashes.Add(value);
        return dashes;
    }
}

/// <summary>A pill or card drawn in code: element counts stay explicit (UX §5.8 #1, pill ≤ 12).</summary>
internal abstract class NodeView : Grid
{
    protected NodeView(CanvasNodeViewModel node)
    {
        Node = node;
        IsHitTestVisible = true;
        // Reordering removes and re-adds a view, so it subscribes whenever it is in the tree.
        Loaded += (_, _) =>
        {
            node.PropertyChanged -= OnNodeChanged;
            node.PropertyChanged += OnNodeChanged;
            OnNodeChanged(node, new PropertyChangedEventArgs(null));
        };
        Unloaded += (_, _) => node.PropertyChanged -= OnNodeChanged;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new NodePeer(this);

    public CanvasNodeViewModel Node { get; }

    public bool IsCard => this is CardView;

    public abstract void Refresh(bool showDetails);

    protected abstract void OnNodeChanged(object? sender, PropertyChangedEventArgs e);

    protected void Place()
    {
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(this, Node.X);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(this, Node.Y);
        Width = Node.Width;
        AutomationProperties.SetName(this, Node.AutomationName);
    }
}

/// <summary>A step: shape, status ring, glyph, title, summary, one badge chip — 10 elements.</summary>
internal sealed class PillView : NodeView
{
    private readonly Rectangle _shape = new() { RadiusX = 28, RadiusY = 28 };
    private readonly Ellipse _ring = new() { Width = 32, Height = 32, StrokeThickness = 2, Margin = new Thickness(12, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly FontIcon _glyph = new() { FontSize = 16, Width = 32, Margin = new Thickness(12, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly TextBlock _summary = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly TextBlock _badgeText = new() { FontFamily = new FontFamily("Segoe UI Variable Text, Segoe Fluent Icons"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _badge = new() { CornerRadius = new CornerRadius(10), MinHeight = 20, Padding = new Thickness(8, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
    private bool _showDetails = true;

    public PillView(CanvasNodeViewModel node)
        : base(node)
    {
        MinHeight = 56;
        _badge.Child = _badgeText;
        var text = new StackPanel { Margin = new Thickness(56, 8, 20, 8), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(_summary);
        text.Children.Add(_badge);
        Children.Add(_shape);
        Children.Add(_ring);
        Children.Add(_glyph);
        Children.Add(text);
        Refresh(true);
    }

    /// <summary>Title + glyph + padding, clamped to 232–320 (UX §5.3).</summary>
    public double DesiredPillWidth(TextBlock measurer)
    {
        measurer.Style = _title.Style;
        measurer.Text = Node.Title;
        measurer.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Clamp(56 + measurer.DesiredSize.Width + 20 + 2, 232, 320);
    }

    public override void Refresh(bool showDetails)
    {
        _showDetails = showDetails;
        Place();
        _title.Style = Theme.Style("BodyStrongTextBlockStyle");
        _summary.Style = Theme.Style("CaptionTextBlockStyle");
        _title.Text = Node.Title;
        _summary.Text = Node.Summary;
        _summary.Foreground = Theme.Brush("TextFillColorSecondaryBrush");
        _summary.Visibility = showDetails && Node.Summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _glyph.Glyph = Node.Glyph;
        _glyph.Foreground = Theme.Brush("TextFillColorPrimaryBrush");
        _ring.Stroke = Theme.Brush("ControlStrongStrokeColorDefaultBrush");
        _badgeText.Text = Node.BadgeText;
        _badgeText.Foreground = Theme.Brush(Node.HasProblem ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        _badge.Background = Theme.Brush("ControlFillColorSecondaryBrush");
        _badge.Visibility = showDetails && Node.HasBadges ? Visibility.Visible : Visibility.Collapsed;

        _shape.Fill = Theme.Brush("CardBackgroundFillColorDefaultBrush");
        _shape.Stroke = Node.IsSelected ? Theme.Selection
            : Theme.Brush(Node.HasProblem ? "SystemFillColorCautionBrush" : "CardStrokeColorDefaultBrush");
        _shape.StrokeThickness = Node.IsSelected || Node.HasProblem ? Theme.Emphasis : 1;
        _shape.StrokeDashArray = Node.MayFail ? Theme.Dashes(4, 2) : null;
    }

    protected override void OnNodeChanged(object? sender, PropertyChangedEventArgs e) => Refresh(_showDetails);
}

/// <summary>A card: background, header (glyph, name, count chip) — 8 elements; members draw above it.</summary>
internal sealed class CardView : NodeView
{
    private readonly Rectangle _shape = new() { RadiusX = 12, RadiusY = 12 };
    private readonly FontIcon _glyph = new() { FontSize = 14 };
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _countText = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _count = new() { CornerRadius = new CornerRadius(10), MinHeight = 20, Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

    public CardView(CanvasNodeViewModel node)
        : base(node)
    {
        _count.Child = _countText;
        // Glyph · title (trims within the card) · count chip.
        var header = new Grid { ColumnSpacing = 8, Height = 32, Margin = new Thickness(12, 4, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(_title, 1);
        SetColumn(_count, 2);
        header.Children.Add(_glyph);
        header.Children.Add(_title);
        header.Children.Add(_count);
        Children.Add(_shape);
        Children.Add(header);
        Refresh(true);
    }

    public override void Refresh(bool showDetails)
    {
        Place();
        Height = Node.Height;
        _title.Style = Theme.Style("BodyStrongTextBlockStyle");
        _title.Text = Node.Title;
        _glyph.Glyph = Node.Glyph;
        _countText.Text = Node.Count;
        _countText.Foreground = Theme.Brush("TextFillColorSecondaryBrush");
        _count.Background = Theme.Brush("ControlFillColorSecondaryBrush");
        _count.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        _shape.Fill = Theme.Brush("CardBackgroundFillColorSecondaryBrush");
        _shape.Stroke = Node.IsSelected ? Theme.Selection
            : Theme.Brush(Node.HasProblem ? "SystemFillColorCautionBrush" : "CardStrokeColorDefaultBrush");
        _shape.StrokeThickness = Node.IsSelected || Node.HasProblem ? Theme.Emphasis : 1;
        _shape.StrokeDashArray = Node.MayFail ? Theme.Dashes(4, 2) : null;
    }

    protected override void OnNodeChanged(object? sender, PropertyChangedEventArgs e) => Refresh(_count.Visibility == Visibility.Visible);
}

/// <summary>Screen readers see each pill and card by its full name (the Outline view, T7.8, is the full equivalent).</summary>
internal sealed partial class NodePeer(NodeView owner) : FrameworkElementAutomationPeer(owner)
{
    protected override string GetNameCore() => owner.Node.AutomationName;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetClassNameCore() => owner.IsCard ? "Card" : "Step";

    protected override bool IsKeyboardFocusableCore() => false;
}
