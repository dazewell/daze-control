using DazeControl.Editor.ViewModels.Canvas;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
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
    private readonly WheelInput _wheel = new();
    private bool _pressedSinceGlide; // any press after a touch glide began: the glide stops on its next frame

    private double ViewportWidth => Viewport.ActualWidth;

    private double ViewportHeight => Viewport.ActualHeight;

    // ---- View commands ----

    /// <summary>Ctrl+0: the whole flow in view, down to 10 % (F-006).</summary>
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

    /// <summary>
    /// A step's rect for UI Automation (F-043): where the camera shows it, in the viewport's automation rect (screen
    /// pixels), so automation tools and screen readers follow pan and zoom. Computed when asked: nothing runs per frame.
    /// </summary>
    private (Windows.Foundation.Rect Bounds, bool Offscreen)? AutomationBounds(NodeView view)
    {
        if (ViewportWidth <= 0)
            return null;
        // Null when no peer can be made for the viewport: the node keeps its default rect.
        var peer = FrameworkElementAutomationPeer.FromElement(Viewport) ?? FrameworkElementAutomationPeer.CreatePeerForElement(Viewport);
        if (peer is null)
            return null;
        var port = peer.GetBoundingRectangle();
        if (port.Width <= 0)
            return null;
        var shown = BoundsOf(view.Node);
        var world = shown with { Height = view.ActualHeight > 0 ? view.ActualHeight : shown.Height };
        var viewport = new DazeControl.Core.Layout.Rect(port.X, port.Y, port.Width, port.Height);
        var screen = CanvasAutomation.ScreenRect(_camera, world, viewport, ViewportWidth);
        return (new Windows.Foundation.Rect(screen.X, screen.Y, screen.Width, screen.Height), CanvasAutomation.IsOffscreen(screen, viewport));
    }

    /// <summary>Keyboard focus and new steps: the smallest pan that shows the step (no ScrollViewer to bring it into view).</summary>
    private void Reveal(DazeControl.Core.Layout.Rect bounds) =>
        ApplyCamera(_camera.Reveal(bounds, ViewportWidth, ViewportHeight));

    /// <summary>The flow moved by (dx, dy) world DIP (a drop above or left of it): pan back, so nothing moves on screen.</summary>
    private void OnOriginShifted(object? sender, DazeControl.Core.Layout.Point shift)
    {
        _autoFit = false;
        ApplyCamera(_camera.PanBy(-shift.X * _camera.Zoom, -shift.Y * _camera.Zoom));
        if (_viewModel?.Scene.IsMoving == true)
        {
            // The camera took the uniform shift back: it isn't layout motion, so nothing slides by it (T21.8).
            _viewModel.Scene.ShiftBy(shift.X, shift.Y);
            BeginMotion();
            DrawEdges(); // lines that stopped gliding go back to the static set
        }
    }

    /// <summary>
    /// Layout › Arrange or Reset layout: the camera fits the new arrangement, so a step the shape put far off is in view
    /// (F-015), and keeps fitting while pill sizes settle, until the user moves the view.
    /// </summary>
    private void OnRearranged(object? sender, EventArgs e)
    {
        _autoFit = true;
        Fit();
    }

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
    /// touchpad's two-finger scroll pans both ways and its pinch (Ctrl+wheel) zooms at the pointer; Windows reports its
    /// vertical scroll as mouse wheel input, so <see cref="WheelInput"/> tells them apart (D-074, D-119 e).
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
        // KeyModifiers carries the Ctrl the system adds to a touchpad pinch; the keyboard state doesn't.
        var ctrl = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || CtrlDown();
        var fromTouchpad = e.Pointer.PointerDeviceType == PointerDeviceType.Touchpad || MessageFromTouchpad();
        var action = _wheel.Classify(delta, point.Properties.IsHorizontalMouseWheel, ctrl, ShiftDown(), point.Timestamp, fromTouchpad);
        // A touchpad's deltas follow the fingers (1 DIP a unit); a notched wheel moves PanPerNotch a notch.
        var pan = _wheel.LastWasTouchpad ? delta * Camera.TouchpadPanPerUnit : delta * Camera.PanPerNotch / WheelInput.Notch;
        switch (action)
        {
            case WheelAction.PanVertical:
                ApplyCamera(_camera.PanBy(0, pan)); // up (positive) moves the content down, as a scroll up does
                break;
            case WheelAction.PanSideways when point.Properties.IsHorizontalMouseWheel:
                ApplyCamera(_camera.PanBy(-pan, 0)); // right (positive) moves the content left
                break;
            case WheelAction.PanSideways:
                ApplyCamera(_camera.PanBy(pan, 0)); // Shift+wheel: a notch up moves the content right
                break;
            default:
                ZoomAround(_camera.Zoom * Camera.WheelFactor(delta), point.Position.X, point.Position.Y);
                break;
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
        _pressedSinceGlide = false;
    }

    private void OnManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (_viewModel is null || (e.IsInertial && (_pressedSinceGlide || _gesture != Gesture.None)))
        {
            e.Complete(); // any new press (blank canvas too) stops the glide
            return;
        }
        var camera = _camera.PanBy(e.Delta.Translation.X, e.Delta.Translation.Y);
        if (Math.Abs(e.Delta.Scale - 1) > 1e-6)
            camera = camera.ZoomAt(camera.Zoom * e.Delta.Scale, e.Position.X, e.Position.Y);
        ApplyCamera(camera);
        e.Handled = true;
    }

    /// <summary>True when the wheel message being handled came from a touchpad (Windows 8+; false when it can't tell).</summary>
    private static bool MessageFromTouchpad() =>
        GetCurrentInputMessageSource(out var source) && source.DeviceType == ImdtTouchpad;

    private const int ImdtTouchpad = 0x10; // INPUT_MESSAGE_DEVICE_TYPE.IMDT_TOUCHPAD

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct InputMessageSource
    {
        public int DeviceType;
        public int OriginId;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetCurrentInputMessageSource(out InputMessageSource source);
}
