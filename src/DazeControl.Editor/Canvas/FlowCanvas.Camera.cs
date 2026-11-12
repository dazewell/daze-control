using DazeControl.Editor.ViewModels.Canvas;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DazeControl.Editor.Canvas;

/// <summary>
/// The camera (D-119 e): <see cref="Camera"/> maths applied as one render transform on <c>Host</c>, so every pan and
/// zoom is synchronous and runs no layout pass. Pointer positions read in Host's coordinates (<c>GetCurrentPoint(Host)</c>)
/// already go through the transform, so hit tests, drags, drops and menus need no camera maths of their own.
/// </summary>
public sealed partial class FlowCanvas
{
    private Camera _camera = Camera.Identity;
    private Camera _panStart; // the camera when a drag pan was pressed

    private double ViewportWidth => Viewport.ActualWidth;

    private double ViewportHeight => Viewport.ActualHeight;

    // ---- View commands ----

    /// <summary>Ctrl+0.</summary>
    public void Fit()
    {
        if (_viewModel is null || ViewportWidth <= 0)
            return;
        _fitPending = false;
        ApplyCamera(_viewModel.FitView(ViewportWidth, ViewportHeight));
    }

    /// <summary>Ctrl+1.</summary>
    public void ZoomToActualSize()
    {
        _autoFit = false;
        ZoomAround(1, ViewportWidth / 2, ViewportHeight / 2);
    }

    public void ZoomBy(double factor)
    {
        _autoFit = false;
        ZoomAround(_camera.Zoom * factor, ViewportWidth / 2, ViewportHeight / 2);
    }

    /// <summary>Zooms keeping the flow point at viewport position (x, y) still (D-074).</summary>
    private void ZoomAround(double zoom, double x, double y)
    {
        if (_viewModel is null || ViewportWidth <= 0)
            return;
        ApplyCamera(_camera.ZoomAt(zoom, x, y));
    }

    /// <summary>
    /// The one place the view changes: clamps to the pan room (48 DIP of the steps stay on screen), sets the transform,
    /// and tells the view model the zoom (semantic zoom, the zoom text).
    /// </summary>
    private void ApplyCamera(Camera camera)
    {
        if (_viewModel is not null && ViewportWidth > 0)
            camera = camera.Clamp(_viewModel.ExtentWidth, _viewModel.ExtentHeight, ViewportWidth, ViewportHeight);
        _camera = camera;
        CameraTransform.ScaleX = CameraTransform.ScaleY = camera.Zoom;
        CameraTransform.TranslateX = camera.X;
        CameraTransform.TranslateY = camera.Y;
        if (_viewModel is null || Math.Abs(_viewModel.Zoom - camera.Zoom) < 1e-9)
            return;
        var details = _viewModel.ShowDetails;
        _viewModel.Zoom = camera.Zoom;
        if (details != _viewModel.ShowDetails)
        {
            // Opacity only: pills keep their size, so crossing the threshold moves nothing (D-120).
            foreach (var view in _views.Values)
                view.ShowDetails(_viewModel.ShowDetails);
        }
    }

    /// <summary>Keyboard focus and new steps: the smallest pan that shows the step (no ScrollViewer to bring it into view).</summary>
    private void Reveal(DazeControl.Core.Layout.Rect bounds) =>
        ApplyCamera(_camera.Reveal(bounds, ViewportWidth, ViewportHeight));

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Viewport.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        if (_fitPending || e.PreviousSize.Width <= 0)
            return;
        // Refit while the user hasn't moved the view; else the flow stays where it is on screen (re-clamped).
        if (_autoFit)
            Fit();
        else
            ApplyCamera(_camera);
    }

    /// <summary>
    /// The mouse wheel and Ctrl+wheel zoom at the pointer; Shift+wheel and a tilt wheel pan sideways. A precision
    /// touchpad's two-finger scroll arrives as wheel input from a <see cref="PointerDeviceType.Touchpad"/> and pans
    /// both ways; its pinch arrives as Ctrl+wheel and zooms at the pointer (D-074, D-119 e).
    /// </summary>
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        var point = e.GetCurrentPoint(Viewport);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
            return;
        _autoFit = false;
        var horizontal = point.Properties.IsHorizontalMouseWheel;
        // KeyModifiers carries the Ctrl the system adds to a touchpad pinch; the keyboard state doesn't.
        var ctrl = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || CtrlDown();
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touchpad && !ctrl)
        {
            // The content follows the fingers: up (positive delta) moves it down, right (horizontal) moves it left.
            var pan = delta * Camera.TouchpadPanPerUnit;
            ApplyCamera(horizontal ? _camera.PanBy(-pan, 0) : _camera.PanBy(0, pan));
        }
        else if (horizontal)
        {
            ApplyCamera(_camera.PanBy(-delta * Camera.PanPerNotch / 120, 0));
        }
        else if (ShiftDown() && !ctrl)
        {
            ApplyCamera(_camera.PanBy(delta * Camera.PanPerNotch / 120, 0));
        }
        else
        {
            ZoomAround(_camera.Zoom * Camera.WheelFactor(delta), point.Position.X, point.Position.Y);
        }
        e.Handled = true;
    }

    // ---- Touch: one finger pans, two pinch, both with inertia (D-119 e). Mouse and pen are the pointer handlers'. ----

    private void OnManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        // A press that started a gesture (drag a step, connect, an edge) keeps it; only a touch on blank canvas pans.
        if (e.PointerDeviceType != PointerDeviceType.Touch || _gesture != Gesture.None || _viewModel is null)
        {
            e.Complete();
            return;
        }
        _autoFit = false;
        e.Handled = true;
    }

    private void OnManipulationInertiaStarting(object sender, ManipulationInertiaStartingRoutedEventArgs e)
    {
        e.TranslationBehavior.DesiredDeceleration = 10.0 * 96 / (1000 * 1000); // DIP/ms²: a short glide
    }

    private void OnManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (_viewModel is null || (_gesture != Gesture.None && e.IsInertial))
        {
            e.Complete(); // a new press stops the glide
            return;
        }
        var camera = _camera.PanBy(e.Delta.Translation.X, e.Delta.Translation.Y);
        if (Math.Abs(e.Delta.Scale - 1) > 1e-6)
            camera = camera.ZoomAt(camera.Zoom * e.Delta.Scale, e.Position.X, e.Position.Y);
        ApplyCamera(camera);
        e.Handled = true;
    }
}
