using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DazeControl.Editor.Inspector;

/// <summary>
/// Value controls (combo boxes, number boxes, sliders, switches) ignore the mouse wheel and pass it to the
/// surrounding ScrollViewer, so scrolling the Configuration never changes a value by accident. Switched on
/// centrally by implicit styles in InspectorPane.xaml (no per-control markup, no reflection).
/// An open combo box keeps the wheel for its own list.
/// </summary>
public static class WheelGuard
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(WheelGuard), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;
        element.RemoveHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnWheel));
        if (e.NewValue is true)
            element.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnWheel), true);
    }

    private static void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ComboBox { IsDropDownOpen: true })
            return;
        // A wheel from inside a popup or a nested list that has its own scrolling stays with it.
        var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
        e.Handled = true;
        if (delta != 0 && FindScroller((DependencyObject)sender) is { } scroller)
            scroller.ChangeView(null, Math.Clamp(scroller.VerticalOffset - delta, 0, scroller.ScrollableHeight), null, true);
    }

    private static ScrollViewer? FindScroller(DependencyObject from)
    {
        for (var parent = VisualTreeHelper.GetParent(from); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer { ScrollableHeight: > 0 } scroller)
                return scroller;
        }
        return null;
    }
}
