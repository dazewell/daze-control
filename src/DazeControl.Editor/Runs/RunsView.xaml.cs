using System.Collections.Specialized;
using System.ComponentModel;
using DazeControl.Core.Runs;
using DazeControl.Editor.Canvas;
using DazeControl.Editor.ViewModels.Runs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using XamlCanvas = Microsoft.UI.Xaml.Controls.Canvas;

namespace DazeControl.Editor.Runs;

/// <summary>The Runs page (UX §5.5); all logic is in <see cref="RunsPageViewModel"/> and <see cref="RunDetailViewModel"/>.</summary>
public sealed partial class RunsView : UserControl
{
    /// <summary>A retry notch: the gap between a bar's tries.</summary>
    private const double NotchWidth = 2;

    private RunsPageViewModel? _viewModel;
    private RunDetailViewModel? _detail;

    public RunsView()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) =>
        {
            RunsList.ItemsSource = null; // the chips' brushes are looked up once per row
            ShowDays();
            DrawTimeline();
        };
    }

    /// <summary>"Open in editor": the shell switches to the flow and shows this run on its canvas.</summary>
    public event Action<RunRecord>? OpenRequested;

    public RunsPageViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnPageChanged;
                _viewModel.Days.CollectionChanged -= OnDaysChanged;
            }
            _viewModel = value;
            if (value is not null)
            {
                value.PropertyChanged += OnPageChanged;
                value.Days.CollectionChanged += OnDaysChanged;
            }
            ShowDays();
            ShowDetail();
        }
    }

    public static Brush ChipBackground(FlowOutcome outcome) => Theme.Brush(outcome switch
    {
        FlowOutcome.Succeeded => "SystemFillColorSuccessBackgroundBrush",
        FlowOutcome.PartiallySucceeded => "SystemFillColorCautionBackgroundBrush",
        FlowOutcome.Failed => "SystemFillColorCriticalBackgroundBrush",
        _ => "SystemFillColorNeutralBackgroundBrush",
    });

    public static Brush ChipStroke(FlowOutcome outcome) => Theme.Brush(outcome switch
    {
        FlowOutcome.Succeeded => "SystemFillColorSuccessBrush",
        FlowOutcome.PartiallySucceeded => "SystemFillColorCautionBrush",
        FlowOutcome.Failed => "SystemFillColorCriticalBrush",
        _ => "ControlStrongStrokeColorDefaultBrush",
    });

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunsPageViewModel.Detail))
            ShowDetail();
        else if (e.PropertyName == nameof(RunsPageViewModel.IsEmpty))
            ShowEmpty();
    }

    private void ShowDays()
    {
        RunsList.ItemsSource = _viewModel is null ? null : new CollectionViewSource { Source = _viewModel.Days, IsSourceGrouped = true }.View;
        ShowEmpty();
        DispatcherQueue.TryEnqueue(SelectShown);
    }

    private void ShowEmpty()
    {
        var empty = _viewModel?.IsEmpty != false;
        EmptyState.Visibility = Show(empty);
        RunsList.Visibility = Show(!empty);
        NoSelectionText.Visibility = Show(!empty && _detail is null);
    }

    /// <summary>A refresh rebuilds the rows; keep the shown run selected.</summary>
    private void OnDaysChanged(object? sender, NotifyCollectionChangedEventArgs e) => DispatcherQueue.TryEnqueue(SelectShown);

    private bool _selecting;

    private void SelectShown()
    {
        var item = _detail is null ? null : _viewModel?.Days.SelectMany(d => d).FirstOrDefault(r => r.Record.RunId == _detail.Record.RunId);
        if (ReferenceEquals(RunsList.SelectedItem, item))
            return;
        _selecting = true;
        RunsList.SelectedItem = item;
        _selecting = false;
    }

    private void OnRunSelected(object sender, SelectionChangedEventArgs e)
    {
        // A refresh clears the selection on its way; the detail stays until another run is picked.
        if (_selecting || _viewModel is null || RunsList.SelectedItem is not RunItemViewModel item)
            return;
        if (_detail?.Record.RunId != item.Record.RunId)
            _viewModel.Select(item);
    }

    private void ShowDetail()
    {
        if (_detail is not null)
            _detail.PropertyChanged -= OnDetailChanged;
        _detail = _viewModel?.Detail;
        if (_detail is not null)
            _detail.PropertyChanged += OnDetailChanged;
        ShowEmpty();
        DetailScroller.Visibility = Show(_detail is not null);
        if (_detail is null)
            return;
        DetailName.Text = _detail.FlowName;
        DetailFacts.Text = _detail.Facts;
        DetailSentence.Text = _detail.Sentence;
        OpenInEditorButton.IsEnabled = _detail.CanOpenInEditor;
        ToolTipService.SetToolTip(OpenInEditorButton, _detail.CanOpenInEditor ? "Show this run on the flow's canvas" : "This flow was deleted");
        NoTimelineText.Visibility = Show(!_detail.HasTimeline);
        TimelineHost.Visibility = Show(_detail.HasTimeline);
        NotRunList.ItemsSource = _detail.NotRun;
        NotRunPanel.Visibility = Show(_detail.NotRun.Count > 0);
        if (_detail.HasTimeline && TimelineWidth() is > 0 and var width)
            _detail.Layout(width);
        DrawTimeline();
        ShowSelectedBar();
    }

    private void OnDetailChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RunDetailViewModel.Bars):
                DrawTimeline();
                break;
            case nameof(RunDetailViewModel.SelectedBar):
                ShowSelectedBar();
                DrawTimeline();
                break;
        }
    }

    private void OnOpenInEditor(object sender, RoutedEventArgs e)
    {
        if (_detail is { CanOpenInEditor: true } detail)
            OpenRequested?.Invoke(detail.Record);
    }

    // ---- Timeline (UX §5.5) ----

    private double TimelineWidth() => TimelineHost.ActualWidth - TimelineHost.Padding.Left - TimelineHost.Padding.Right;

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_detail is { HasTimeline: true } detail && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
            detail.Layout(TimelineWidth()); // Bars changes → DrawTimeline
    }

    private void ShowSelectedBar()
    {
        var lines = _detail?.SelectedLines ?? [];
        SelectedLinesList.ItemsSource = lines;
        SelectedPanel.Visibility = Show(lines.Count > 0);
        PickBarText.Visibility = Show(_detail is { HasTimeline: true } && lines.Count == 0);
    }

    private void DrawTimeline()
    {
        var focused = XamlRoot is null ? null : (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as FrameworkElement)?.Tag as string;
        TimelineCanvas.Children.Clear();
        if (_detail is not { HasTimeline: true } detail)
            return;
        var width = Math.Max(TimelineWidth(), 100);
        TimelineCanvas.Width = width;
        TimelineCanvas.Height = detail.Height;

        foreach (var tick in detail.Ticks)
        {
            var line = new Rectangle { Width = 1, Height = detail.Height - RunDetailViewModel.AxisHeight + 4, Fill = Theme.Brush("DividerStrokeColorDefaultBrush") };
            Place(line, Math.Min(tick.X, width - 1), RunDetailViewModel.AxisHeight - 4);
            var text = new TextBlock { Text = tick.Text, Style = Theme.Style("CaptionTextBlockStyle"), Foreground = Theme.Brush("TextFillColorSecondaryBrush") };
            text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Place(text, Math.Clamp(tick.X - text.DesiredSize.Width / 2, 0, Math.Max(0, width - text.DesiredSize.Width)), 0);
        }

        foreach (var bracket in detail.Brackets)
        {
            var frame = new Border
            {
                Width = bracket.Width + 8,
                Height = bracket.Height,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = Theme.Brush("ControlStrongStrokeColorDefaultBrush"),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // hit-testable for its tooltip; bars sit above it
            };
            ToolTipService.SetToolTip(frame, bracket.Title);
            AutomationProperties.SetName(frame, bracket.Title);
            Place(frame, Math.Max(0, bracket.X - 4), bracket.Y);
        }

        foreach (var bar in detail.Bars)
        {
            var button = new Button
            {
                Style = (Style)Resources["TimelineBarStyle"],
                Tag = bar.NodeId,
                Width = bar.Width,
                Height = RunDetailViewModel.BarHeight,
                Content = BarLook(bar, ReferenceEquals(bar, detail.SelectedBar) || bar.NodeId == detail.SelectedBar?.NodeId),
            };
            AutomationProperties.SetName(button, bar.AutomationName);
            ToolTipService.SetToolTip(button, bar.AutomationName);
            button.Click += (_, _) => detail.SelectedBar = bar;
            Place(button, bar.X, bar.Y);
            if (bar.NodeId == focused)
                DispatcherQueue.TryEnqueue(() => button.Focus(FocusState.Keyboard)); // a redraw keeps keyboard focus on the bar
        }
    }

    /// <summary>A bar by state: filled, or dashed caution for a may-fail step that failed; each retry is a 2-DIP gap.</summary>
    private static Grid BarLook(TimelineBarView bar, bool selected)
    {
        var look = new Grid { Width = bar.Width, Height = RunDetailViewModel.BarHeight };
        var fill = Theme.Brush(bar.Outcome switch
        {
            NodeOutcome.Succeeded => "SystemFillColorSuccessBrush",
            NodeOutcome.Failed => "SystemFillColorCriticalBrush",
            NodeOutcome.FailedIgnored => "SystemFillColorCautionBackgroundBrush",
            _ => "ControlStrongStrokeColorDisabledBrush",
        });
        var edges = new List<double> { 0 };
        edges.AddRange(bar.Notches.Where(n => n > NotchWidth && n < bar.Width - NotchWidth));
        edges.Add(bar.Width);
        var segments = new XamlCanvas { Width = bar.Width, Height = RunDetailViewModel.BarHeight };
        for (var i = 0; i < edges.Count - 1; i++)
        {
            var left = edges[i] + (i == 0 ? 0 : NotchWidth / 2);
            var right = edges[i + 1] - (i == edges.Count - 2 ? 0 : NotchWidth / 2);
            var segment = new Rectangle
            {
                Width = Math.Max(right - left, 1),
                Height = RunDetailViewModel.BarHeight,
                RadiusX = 4,
                RadiusY = 4,
                Fill = fill,
            };
            if (bar.IsMayFailFailed)
            {
                segment.Stroke = Theme.Brush("SystemFillColorCautionBrush");
                segment.StrokeThickness = 1.5;
                segment.StrokeDashArray = Theme.Dashes(3, 2);
            }
            XamlCanvas.SetLeft(segment, left);
            segments.Children.Add(segment);
        }
        look.Children.Add(segments);

        // Colour alone doesn't say it (contrast themes, colour blindness): a mark on bars wide enough for one.
        var glyph = bar.Outcome switch
        {
            NodeOutcome.Succeeded => "",
            NodeOutcome.Failed => "",
            NodeOutcome.FailedIgnored => "",
            _ => "",
        };
        if (bar.Width >= 20)
            look.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(4, 0, 0, 0),
                Foreground = Theme.Brush(bar.Outcome is NodeOutcome.Succeeded or NodeOutcome.Failed
                    ? Theme.HighContrast ? "SystemColorWindowColorBrush" : "TextOnAccentFillColorPrimaryBrush"
                    : "TextFillColorPrimaryBrush"),
            });
        if (selected)
            look.Children.Add(new Border
            {
                Margin = new Thickness(-3),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(Theme.Emphasis),
                BorderBrush = Theme.Selection,
            });
        return look;
    }

    private void Place(UIElement element, double x, double y)
    {
        XamlCanvas.SetLeft(element, x);
        XamlCanvas.SetTop(element, y);
        TimelineCanvas.Children.Add(element);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
