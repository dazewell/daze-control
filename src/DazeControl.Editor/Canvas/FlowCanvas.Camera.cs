using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.ComponentModel;
using DazeControl.Core.Catalog;
using DazeControl.Core.Layout;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;
using CorePoint = DazeControl.Core.Layout.Point;
using XamlCanvas = Microsoft.UI.Xaml.Controls.Canvas;

namespace DazeControl.Editor.Canvas;

public sealed partial class FlowCanvas
{
    // ---- View commands ----

    /// <summary>Ctrl+0.</summary>
    public void Fit()
    {
        if (_viewModel is null || Scroller.ViewportWidth <= 0)
            return;
        _fitPending = false;
        ApplyView(_viewModel.FitView(Scroller.ViewportWidth, Scroller.ViewportHeight));
    }

    /// <summary>Ctrl+1.</summary>
    public void ZoomToActualSize()
    {
        _autoFit = false;
        ZoomAround(1, Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
    }

    public void ZoomBy(double factor)
    {
        _autoFit = false;
        ZoomAround(CurrentView.Zoom * factor, Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
    }

    private CanvasView CurrentView =>
        _pendingView ?? new CanvasView(Scroller.HorizontalOffset, Scroller.VerticalOffset, Scroller.ZoomFactor);

    /// <summary>Zooms keeping the flow point at viewport position (x, y) still (D-074).</summary>
    private void ZoomAround(double zoom, double x, double y)
    {
        if (_viewModel is null || Scroller.ViewportWidth <= 0)
            return;
        ApplyView(CanvasViewport.Reanchor(CurrentView, _padX, _padY, CanvasViewport.Clamp(zoom), x, y,
            _viewModel.ExtentWidth, _viewModel.ExtentHeight, Scroller.ViewportWidth, Scroller.ViewportHeight));
    }

    /// <summary>Sets the pan room for the view's zoom, then the view itself.</summary>
    private void ApplyView(CanvasView view)
    {
        var padX = CanvasViewport.Pad(Scroller.ViewportWidth, view.Zoom);
        var padY = CanvasViewport.Pad(Scroller.ViewportHeight, view.Zoom);
        _padZoom = view.Zoom;
        var repadded = padX != _padX || padY != _padY;
        if (repadded)
        {
            (_padX, _padY) = (padX, padY);
            Wrapper.Padding = new Thickness(padX, padY, padX, padY);
            Scroller.UpdateLayout(); // the new extent, so ChangeView doesn't clamp to the old one
        }
        _pendingView = view;
        var seq = ++_viewSeq;
        Scroller.ChangeView(view.OffsetX, view.OffsetY, (float)view.Zoom, disableAnimation: true);
        {
            // Inside a layout pass (SizeChanged, first open) UpdateLayout doesn't run and the view above may be clamped
            // to the old extent: ask again once layout is done, unless a newer view or a press came first.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (seq == _viewSeq && (Math.Abs(Scroller.ZoomFactor - view.Zoom) > 1e-4
                    || Math.Abs(Scroller.HorizontalOffset - view.OffsetX) > 0.5 || Math.Abs(Scroller.VerticalOffset - view.OffsetY) > 0.5))
                    Scroller.ChangeView(view.OffsetX, view.OffsetY, (float)view.Zoom, disableAnimation: true);
            });
        }
    }

    /// <summary>
    /// Mouse wheel and Ctrl+wheel zoom at the pointer, Shift+wheel scrolls sideways (the ScrollViewer would scroll it
    /// vertically); a touchpad and a tilt wheel stay the ScrollViewer's (D-074).
    /// </summary>
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_viewModel is null || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
            return;
        var point = e.GetCurrentPoint(Scroller);
        if (point.Properties.IsHorizontalMouseWheel)
            return;
        _autoFit = false;
        var delta = point.Properties.MouseWheelDelta;
        var view = CurrentView;
        if (ShiftDown())
        {
            var x = Math.Clamp(view.OffsetX - delta * CanvasViewport.SideScrollPerNotch / 120, 0, Scroller.ScrollableWidth);
            _pendingView = view with { OffsetX = x };
            _viewSeq++;
            Scroller.ChangeView(x, null, null, disableAnimation: true);
        }
        else
        {
            ZoomAround(view.Zoom * CanvasViewport.WheelFactor(delta), point.Position.X, point.Position.Y);
        }
        e.Handled = true;
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_viewModel is null || e.IsIntermediate)
            return;
        _pendingView = null;
        var details = _viewModel.ShowDetails;
        _viewModel.Zoom = Scroller.ZoomFactor;
        if (details != _viewModel.ShowDetails)
        {
            // Opacity only: pills keep their size, so crossing the threshold moves nothing (D-120).
            foreach (var view in _views.Values)
                view.ShowDetails(_viewModel.ShowDetails);
        }
    }
}
