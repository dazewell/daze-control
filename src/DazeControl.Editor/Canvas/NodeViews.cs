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
        // Subscribed for the view's whole life, not on Loaded/Unloaded: when an element leaves and re-enters the
        // tree in one frame, Unloaded can arrive after Loaded and the view stopped following its node (X-004).
        node.PropertyChanged += OnNodeChanged;
    }

    /// <summary>The canvas dropped this view for good.</summary>
    public void Detach() => Node.PropertyChanged -= OnNodeChanged;

    protected override AutomationPeer OnCreateAutomationPeer() => new NodePeer(this);

    public CanvasNodeViewModel Node { get; }

    public bool IsCard => this is CardView;

    public abstract void Refresh(bool showDetails);

    /// <summary>
    /// Semantic zoom (D-120): summary and badges fade by opacity, never collapse, so a pill keeps its size and
    /// zooming moves nothing.
    /// </summary>
    public abstract void ShowDetails(bool showDetails);

    protected abstract void OnNodeChanged(object? sender, PropertyChangedEventArgs e);

    /// <summary>A move or resize only places the view; anything else redraws its content.</summary>
    protected static bool IsGeometry(string? property) =>
        property is nameof(CanvasNodeViewModel.X) or nameof(CanvasNodeViewModel.Y)
            or nameof(CanvasNodeViewModel.Width) or nameof(CanvasNodeViewModel.Height);

    /// <summary>No drawing depends on these (the layout's grid cell).</summary>
    protected static bool IsUnseen(string? property) =>
        property is nameof(CanvasNodeViewModel.Column) or nameof(CanvasNodeViewModel.Row);

    /// <summary>Puts the view where its node is laid out (also called for every box a re-layout moved, T21.6).</summary>
    public virtual void Place()
    {
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(this, Node.X);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(this, Node.Y);
        Width = Node.Width;
        AutomationProperties.SetName(this, Node.AutomationName);
        Opacity = Node.IsDimmed ? 0.35 : 1; // failure-policy preview (UX §5.4)
    }
}

/// <summary>
/// A step: shape, status ring, glyph, title, summary, one badge chip — 10 elements, 11 while running (the
/// ProgressRing is created on first use).
/// </summary>
internal sealed class PillView : NodeView
{
    private readonly Rectangle _shape = new() { RadiusX = 28, RadiusY = 28 };
    private readonly Ellipse _ring = new() { Width = 32, Height = 32, StrokeThickness = 2, Margin = new Thickness(12, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly FontIcon _glyph = new() { FontSize = 16, Width = 32, Margin = new Thickness(12, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly TextBlock _summary = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly TextBlock _badgeText = new() { FontFamily = new FontFamily("Segoe UI Variable Text, Segoe Fluent Icons"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _badge = new() { CornerRadius = new CornerRadius(10), MinHeight = 20, Padding = new Thickness(8, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
    private static readonly Windows.UI.ViewManagement.UISettings Ui = new();
    private ProgressRing? _progress;
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
        _summary.Text = Node.ShownSummary;
        _summary.Visibility = Node.ShownSummary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var skipped = Node.RunState == NodeRunState.Skipped;
        _title.Foreground = Theme.Brush(skipped ? "TextFillColorDisabledBrush" : "TextFillColorPrimaryBrush");
        _summary.Foreground = Theme.Brush(skipped ? "TextFillColorDisabledBrush" : "TextFillColorSecondaryBrush");
        ShowRunState();
        _badgeText.Text = Node.BadgeText;
        _badgeText.Foreground = Theme.Brush(Node.HasProblem ? "SystemFillColorCautionBrush" : Node.BadgesInherited ? "TextFillColorTertiaryBrush" : "TextFillColorSecondaryBrush");
        _badge.Background = Theme.Brush("ControlFillColorSecondaryBrush");
        _badge.Visibility = Node.HasBadges ? Visibility.Visible : Visibility.Collapsed;
        ShowDetails(showDetails);

        var failed = Node.RunState == NodeRunState.Failed;
        _shape.Fill = Theme.Brush(Theme.HighContrast ? "SystemColorWindowColorBrush" : "CardBackgroundFillColorDefaultBrush");
        _shape.Stroke = Node.IsSelected ? Theme.Selection
            : Theme.Brush(failed ? "SystemFillColorCriticalBrush" : Node.HasProblem ? "SystemFillColorCautionBrush" : "CardStrokeColorDefaultBrush");
        _shape.StrokeThickness = Node.IsSelected || Node.HasProblem || failed ? Theme.Emphasis : 1;
        _shape.StrokeDashArray = Node.MayFail ? Theme.Dashes(4, 2) : null;
    }

    /// <summary>Run states as shape plus glyph, never colour alone (UX §5.3).</summary>
    private void ShowRunState()
    {
        var state = Node.RunState;
        var ringThickness = Theme.HighContrast ? 3 : 2;
        _ring.StrokeThickness = ringThickness;
        _ring.StrokeDashArray = null;
        _ring.Fill = null;
        _ring.Stroke = Theme.Brush("ControlStrongStrokeColorDefaultBrush");
        _glyph.FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"];
        _glyph.Glyph = Node.Glyph;
        _glyph.Foreground = Theme.Brush("TextFillColorPrimaryBrush");
        var running = state == NodeRunState.Running;
        // Animation effects off: a static ¾ ring instead of the spinning one (UX §5.3 motion).
        var spin = running && Ui.AnimationsEnabled;
        if (spin && _progress is null)
        {
            _progress = new ProgressRing { Width = 32, Height = 32, Margin = new Thickness(12, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
            Children.Insert(Children.IndexOf(_ring) + 1, _progress);
        }
        if (_progress is not null)
        {
            _progress.IsActive = spin;
            _progress.Visibility = spin ? Visibility.Visible : Visibility.Collapsed;
        }

        string? fill = state switch
        {
            NodeRunState.Succeeded => "SystemFillColorSuccessBrush",
            NodeRunState.Failed => "SystemFillColorCriticalBrush",
            NodeRunState.FailedMayFail => "SystemFillColorCautionBrush",
            _ => null,
        };
        if (fill is not null)
        {
            _ring.Fill = _ring.Stroke = Theme.Brush(fill);
            _glyph.Foreground = Theme.Brush(Theme.HighContrast ? "SystemColorWindowColorBrush" : "TextOnAccentFillColorPrimaryBrush");
            _glyph.Glyph = state switch
            {
                NodeRunState.Failed => "\uE711",
                NodeRunState.FailedMayFail => "\uE7BA",
                _ => "\uE73E",
            };
        }
        switch (state)
        {
            case NodeRunState.Running when !spin:
                _ring.Stroke = Theme.Selection;
                _ring.StrokeDashArray = Theme.Dashes(Math.PI * 30 * 0.75 / ringThickness, 100); // ¾ of the circumference
                break;
            case NodeRunState.Running:
                _ring.Stroke = Theme.Brush("ControlStrongStrokeColorDisabledBrush");
                break;
            case NodeRunState.Skipped:
                _ring.StrokeDashArray = Theme.Dashes(2, 2);
                _ring.Stroke = _glyph.Foreground = Theme.Brush("TextFillColorDisabledBrush");
                _glyph.Glyph = "\uE893";
                break;
        }
        // Skipped by "Only if": still the Succeeded ring and tick, but dashed, so it doesn't read as work done.
        if (Node.RunConditionSkipped && state == NodeRunState.Succeeded)
            _ring.StrokeDashArray = Theme.Dashes(2, 2);
        // Dry run: the start order replaces the glyph of a step that would work.
        if (Node.OrderText.Length > 0 && state is NodeRunState.Succeeded or NodeRunState.None)
        {
            _glyph.FontFamily = new FontFamily("Segoe UI Variable Text");
            _glyph.Glyph = Node.OrderText;
        }
    }

    public override void ShowDetails(bool showDetails)
    {
        _showDetails = showDetails;
        _summary.Opacity = _badge.Opacity = showDetails ? 1 : 0;
    }

    protected override void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsGeometry(e.PropertyName))
            Place();
        else if (!IsUnseen(e.PropertyName))
            Refresh(_showDetails);
    }
}

/// <summary>A card: background, header (glyph, name, count chip) — 8 elements; members draw above it.</summary>
internal sealed class CardView : NodeView
{
    private readonly Rectangle _shape = new() { RadiusX = 12, RadiusY = 12 };
    private readonly FontIcon _glyph = new() { FontSize = 14 };
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _countText = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _count = new() { CornerRadius = new CornerRadius(10), MinHeight = 20, Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private bool _showDetails = true;

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
        _title.Style = Theme.Style("BodyStrongTextBlockStyle");
        _title.Text = Node.Title;
        _glyph.Glyph = Node.Glyph;
        _countText.Text = Node.Count;
        _countText.Foreground = Theme.Brush("TextFillColorSecondaryBrush");
        _count.Background = Theme.Brush("ControlFillColorSecondaryBrush");
        ShowDetails(showDetails);
        _shape.Fill = Theme.Brush("CardBackgroundFillColorSecondaryBrush");
        _shape.Stroke = Node.IsSelected ? Theme.Selection
            : Theme.Brush(Node.HasProblem ? "SystemFillColorCautionBrush" : "CardStrokeColorDefaultBrush");
        _shape.StrokeThickness = Node.IsSelected || Node.HasProblem ? Theme.Emphasis : 1;
        _shape.StrokeDashArray = Node.MayFail ? Theme.Dashes(4, 2) : null;
    }

    public override void Place()
    {
        base.Place();
        Height = Node.Height;
    }

    public override void ShowDetails(bool showDetails)
    {
        _showDetails = showDetails;
        _count.Opacity = showDetails ? 1 : 0;
    }

    protected override void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsGeometry(e.PropertyName))
            Place();
        else if (!IsUnseen(e.PropertyName))
            Refresh(_showDetails);
    }
}

/// <summary>Screen readers see each pill and card by its full name (the Outline view, T7.8, is the full equivalent).</summary>
internal sealed partial class NodePeer(NodeView owner) : FrameworkElementAutomationPeer(owner)
{
    protected override string GetNameCore() => owner.Node.AutomationName;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetClassNameCore() => owner.IsCard ? "Card" : "Step";

    protected override bool IsKeyboardFocusableCore() => false;
}
