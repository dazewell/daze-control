using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CtrlAltDaze.Editor.Inspector;

/// <summary>
/// Left-to-right layout that starts a new line when the next item doesn't fit (the "Shows:" row, D-132). An item wider
/// than the whole line is measured at the line width, so long text wraps inside its own block instead of being cut.
/// </summary>
public sealed class WrapLayout : NonVirtualizingLayout
{
    public double HorizontalSpacing { get; set; } = 2;
    public double VerticalSpacing { get; set; } = 2;

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        double x = 0, y = 0, lineHeight = 0, maxRight = 0;
        foreach (var child in context.Children)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width)
            {
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
                child.Measure(new Size(width, double.PositiveInfinity));
                size = child.DesiredSize;
            }
            lineHeight = Math.Max(lineHeight, size.Height);
            x += size.Width + HorizontalSpacing;
            maxRight = Math.Max(maxRight, x - HorizontalSpacing);
        }
        return new Size(Math.Min(maxRight, width), y + lineHeight);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (var child in context.Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
            }
            child.Arrange(new Rect(x, y, Math.Min(size.Width, finalSize.Width), size.Height));
            lineHeight = Math.Max(lineHeight, size.Height);
            x += size.Width + HorizontalSpacing;
        }
        return finalSize;
    }
}
