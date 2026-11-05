using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DazeControl.Engine.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DazeControl.Engine.Shell;

/// <summary>
/// The window both the run pop-up (D-012) and the flow menu (D-055) are: a Win32 <c>WS_POPUP</c> with the DWM acrylic
/// backdrop, drawn with Direct2D/DirectWrite. Topmost, never activated (<c>WM_MOUSEACTIVATE</c> → no activate, no
/// <c>SetForegroundWindow</c>), not in Alt+Tab, fades through the layered alpha. Message thread only. The window is
/// made on the first show; the d2d1/DWrite modules, render target and text layouts exist only while it is visible.
/// What to draw is the subclass's job.
/// </summary>
internal abstract unsafe class AcrylicSurface : IDisposable
{
    private const nuint FrameTimer = 2;
    private const int RecreateTarget = unchecked((int)0x8899000C); // D2DERR_RECREATE_TARGET

    private static readonly Dictionary<nint, AcrylicSurface> s_windows = [];
    private static readonly HashSet<string> s_registered = [];

    private readonly string _className;
    private readonly bool _clickThrough;
    private HWND _hwnd;
    private ID2D1HwndRenderTarget* _target;
    private ID2D1SolidColorBrush* _brush;
    private nint _d2dModule, _dwriteModule;
    private bool _clickable;
    private (int Width, int Height) _size;

    private double _alpha;
    private double _fadeFrom, _fadeTo;
    private long _fadeStart;
    private TimeSpan _fadeLength;

    /// <param name="className">Window class (one per subclass).</param>
    /// <param name="clickThrough">Starts click-through (<c>WS_EX_TRANSPARENT</c>, the pop-up); the menu takes clicks.</param>
    protected AcrylicSurface(string className, bool clickThrough)
    {
        _className = className;
        _clickThrough = clickThrough;
    }

    protected HWND Hwnd => _hwnd;
    protected ID2D1Factory* D2d { get; private set; }
    protected IDWriteFactory* DWrite { get; private set; }
    protected PopupTheme Theme { get; set; } = PopupTheme.Read();
    protected bool Visible { get; private set; }
    protected uint Dpi { get; set; } = 96;
    protected (int Width, int Height) PixelSize => _size;

    /// <summary>True while something besides a fade needs 16 ms frames (a spinning ring, a sliding focus).</summary>
    protected virtual bool Animating => false;

    /// <summary>Draw into the cleared target; <paramref name="brush"/> is the shared solid brush (set its colour per use).</summary>
    protected abstract void Render(ID2D1RenderTarget* target, ID2D1SolidColorBrush* brush);

    protected abstract void ReleaseLayouts();

    /// <summary>One 16 ms frame while <see cref="Animating"/> or fading: advance state, invalidate if anything changed.</summary>
    protected virtual void OnFrame()
    {
    }

    /// <summary>Light/dark, accent or text size changed while showing; <see cref="Theme"/> is already re-read.</summary>
    protected virtual void OnSettingsChanged()
    {
    }

    protected virtual void OnHidden()
    {
    }

    /// <summary>Subclass messages (clicks, timers, keys); return true with <paramref name="result"/> when handled.</summary>
    protected virtual bool OnMessage(uint message, WPARAM wParam, LPARAM lParam, out LRESULT result)
    {
        result = (LRESULT)0;
        return false;
    }

    /// <summary>Makes the window visible (topmost, no activate) from transparent and starts the fade in.</summary>
    protected void ShowWindow(TimeSpan fadeIn)
    {
        if (!Visible)
        {
            Visible = true;
            _alpha = 0;
            ApplyAlpha();
            PInvoke.SetWindowPos(_hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
                | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        }
        StartFade(1, fadeIn);
        UpdateFrameTimer();
    }

    /// <summary>Fades out (or vanishes at once with Animation effects off), then frees the drawing resources.</summary>
    protected void HideWindow(TimeSpan fadeOut)
    {
        if (!Visible)
            return;
        StartFade(0, fadeOut);
        UpdateFrameTimer();
    }

    protected void Invalidate() => PInvoke.InvalidateRect(_hwnd, (RECT?)null, false);

    /// <summary>The theme is read once per showing: the window outlives a light/dark switch between shows.</summary>
    protected void BeginShow()
    {
        if (!Visible)
            Theme = PopupTheme.Read();
        EnsureWindow();
        if (!Visible)
            ApplyTheme();
    }

    private void EnsureWindow()
    {
        if (_hwnd != HWND.Null)
            return;
        var instance = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value;
        fixed (char* className = _className)
        fixed (char* title = "Daze Control")
        {
            if (s_registered.Add(_className))
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = instance,
                    lpszClassName = className,
                    hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
                };
                if (PInvoke.RegisterClassEx(&windowClass) == 0)
                {
                    s_registered.Remove(_className);
                    throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
                }
            }
            // Click-through needs WS_EX_TRANSPARENT on a layered window; the layered alpha is also the fade.
            var ex = WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST
                | WINDOW_EX_STYLE.WS_EX_LAYERED;
            if (_clickThrough)
                ex |= WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
            _hwnd = PInvoke.CreateWindowEx(ex, className, title, WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, HWND.Null, HMENU.Null, instance, null);
        }
        if (_hwnd == HWND.Null)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        s_windows[(nint)_hwnd.Value] = this;
        _clickable = !_clickThrough;
        ApplyAlpha();

        // The whole client area is "frame", so the backdrop shows through wherever we draw nothing.
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        Cosmetic("frame", PInvoke.DwmExtendFrameIntoClientArea(_hwnd, &margins));
        var corners = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        Cosmetic("corners", PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corners, sizeof(DWM_WINDOW_CORNER_PREFERENCE)));
        ApplyTheme();
        // DWM draws an inactive window's backdrop as a flat fill; this window is never active, so tell its frame it is.
        PInvoke.SendMessage(_hwnd, PInvoke.WM_NCACTIVATE, 1, 0);
    }

    protected void ApplyTheme()
    {
        // A contrast theme gets a solid system-colour background drawn by Render instead of acrylic (UX §5.3).
        var backdrop = Theme.HighContrast ? DWM_SYSTEMBACKDROP_TYPE.DWMSBT_NONE : DWM_SYSTEMBACKDROP_TYPE.DWMSBT_TRANSIENTWINDOW;
        Cosmetic("backdrop", PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE, &backdrop, sizeof(DWM_SYSTEMBACKDROP_TYPE)));
        BOOL dark = !Theme.Light;
        Cosmetic("dark mode", PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, (uint)sizeof(BOOL)));
    }

    private static void Cosmetic(string what, HRESULT result)
    {
        // Square corners or a flat fill are no reason to show nothing.
        if (result.Failed)
            Log.Warn($"popup: {what} 0x{result.Value:X8}");
    }

    protected void SetClickable(bool clickable)
    {
        if (clickable == _clickable)
            return;
        _clickable = clickable;
        var ex = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        ex = clickable ? ex & ~WINDOW_EX_STYLE.WS_EX_TRANSPARENT : ex | WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        _ = PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)ex);
    }

    protected bool Clickable => _clickable;

    /// <summary>Moves and sizes the window (physical pixels), topmost again, and follows the size with the render target.</summary>
    protected void SetBounds(int x, int y, int width, int height)
    {
        PInvoke.SetWindowPos(_hwnd, HWND.HWND_TOPMOST, x, y, width, height, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        var size = (width, height);
        if (_target is not null)
            ((ID2D1RenderTarget*)_target)->SetDpi(Dpi, Dpi); // the monitor may have changed with the size unchanged
        if (size == _size)
            return;
        _size = size;
        if (_target is not null)
        {
            var pixels = new D2D_SIZE_U { width = (uint)width, height = (uint)height };
            _target->Resize(&pixels);
        }
    }

    // ---- fades and frames ----

    private void StartFade(double to, TimeSpan length)
    {
        if (!Theme.Animations)
        {
            _alpha = to;
            _fadeTo = to;
            _fadeLength = TimeSpan.Zero;
            ApplyAlpha();
            if (to == 0)
                Hidden();
            return;
        }
        _fadeFrom = _alpha;
        _fadeTo = to;
        _fadeStart = Stopwatch.GetTimestamp();
        _fadeLength = length * Math.Abs(to - _alpha);
    }

    private bool Fading => _fadeLength > TimeSpan.Zero;

    /// <summary>16 ms frames only while fading or animating; no timer at all once the window is still or gone.</summary>
    protected void UpdateFrameTimer()
    {
        if (_hwnd == HWND.Null)
            return;
        if (Fading || Visible && Animating)
            PInvoke.SetTimer(_hwnd, FrameTimer, 16, null);
        else
            PInvoke.KillTimer(_hwnd, FrameTimer);
    }

    private void Frame()
    {
        if (Fading)
        {
            var t = Math.Clamp(Stopwatch.GetElapsedTime(_fadeStart) / _fadeLength, 0, 1);
            // Decelerate in, accelerate out (the OSD's FadeThrough easing).
            var eased = _fadeTo > _fadeFrom ? 1 - Math.Pow(1 - t, 3) : t * t * t;
            _alpha = _fadeFrom + (_fadeTo - _fadeFrom) * eased;
            ApplyAlpha();
            if (t >= 1)
            {
                _fadeLength = TimeSpan.Zero;
                if (_fadeTo == 0)
                {
                    Hidden();
                    return;
                }
            }
        }
        OnFrame();
        UpdateFrameTimer();
    }

    private void ApplyAlpha() =>
        PInvoke.SetLayeredWindowAttributes(_hwnd, new COLORREF(0), (byte)Math.Round(_alpha * 255), LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);

    protected void Hidden()
    {
        PInvoke.KillTimer(_hwnd, FrameTimer);
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
        Visible = false;
        _fadeLength = TimeSpan.Zero;
        OnHidden();
        // Back to the idle footprint: no factory, target or layout outlives a showing.
        ReleaseGraphics();
    }

    // ---- drawing resources ----

    /// <summary>
    /// d2d1 and DWrite are loaded here, not through P/Invoke, so they can be unloaded on hide: P/Invoke keeps a
    /// library for good, and with it d3d11/dxgi (+3.8 MB private after the first pop-up, measured T5.2).
    /// </summary>
    protected void EnsureFactories()
    {
        if (D2d is null)
        {
            _d2dModule = NativeLibrary.Load("d2d1.dll");
            var create = (delegate* unmanaged[Stdcall]<D2D1_FACTORY_TYPE, Guid*, D2D1_FACTORY_OPTIONS*, void**, HRESULT>)
                NativeLibrary.GetExport(_d2dModule, "D2D1CreateFactory");
            void* factory;
            var iid = ID2D1Factory.IID_Guid;
            create(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, &iid, null, &factory).ThrowOnFailure();
            D2d = (ID2D1Factory*)factory;
        }
        if (DWrite is null)
        {
            _dwriteModule = NativeLibrary.Load("DWrite.dll");
            var create = (delegate* unmanaged[Stdcall]<DWRITE_FACTORY_TYPE, Guid*, void**, HRESULT>)
                NativeLibrary.GetExport(_dwriteModule, "DWriteCreateFactory");
            void* factory;
            var iid = IDWriteFactory.IID_Guid;
            // Isolated: a shared factory registers with the font cache service and keeps DWrite busy after release.
            create(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_ISOLATED, &iid, &factory).ThrowOnFailure();
            DWrite = (IDWriteFactory*)factory;
        }
    }

    /// <summary>One icon glyph from Segoe Fluent Icons, centred in a <paramref name="box"/>-DIP square.</summary>
    protected IDWriteTextLayout* IconText(string glyph, float size, float box)
    {
        IDWriteTextFormat* format;
        fixed (char* family = "Segoe Fluent Icons")
        fixed (char* locale = "en-us")
            DWrite->CreateTextFormat(family, null, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL,
                DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, size, locale, &format);
        try
        {
            format->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
            format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            IDWriteTextLayout* layout;
            fixed (char* text = glyph)
                DWrite->CreateTextLayout(text, (uint)glyph.Length, format, box, box, &layout);
            return layout;
        }
        finally
        {
            format->Release();
        }
    }

    protected IDWriteTextLayout* Text(string text, float size, DWRITE_FONT_WEIGHT weight, float width, bool wrap, float height = 1000)
    {
        IDWriteTextFormat* format;
        fixed (char* family = "Segoe UI Variable Text")
        fixed (char* locale = "en-us")
            DWrite->CreateTextFormat(family, null, weight, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, size, locale, &format);
        try
        {
            format->SetWordWrapping(wrap ? DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_WRAP : DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);
            // "…" where it doesn't fit: the name on one line, the reason on two.
            IDWriteInlineObject* ellipsis;
            DWrite->CreateEllipsisTrimmingSign(format, &ellipsis);
            var trimming = new DWRITE_TRIMMING { granularity = DWRITE_TRIMMING_GRANULARITY.DWRITE_TRIMMING_GRANULARITY_CHARACTER };
            format->SetTrimming(&trimming, ellipsis);
            ellipsis->Release();
            IDWriteTextLayout* layout;
            fixed (char* chars = text)
                DWrite->CreateTextLayout(chars, (uint)text.Length, format, width, height, &layout);
            return layout;
        }
        finally
        {
            format->Release();
        }
    }

    protected ID2D1Brush* Brush => (ID2D1Brush*)_brush;

    protected void SetColour(D2D1_COLOR_F colour) => _brush->SetColor(&colour);

    private void Paint()
    {
        if (_target is null)
            CreateTarget();
        var target = (ID2D1RenderTarget*)_target;
        target->BeginDraw();
        var clear = Theme.Background; // transparent: the acrylic shows through (solid in a contrast theme)
        target->Clear(&clear);
        Render(target, _brush);
        var result = target->EndDraw(null, null);
        if (result.Value == RecreateTarget)
        {
            ReleaseTarget();
            Invalidate(); // nothing else may paint again soon
        } // D2DERR_RECREATE_TARGET, device lost (driver update, GPU reset): the next paint makes a new one
        else
            result.ThrowOnFailure();
    }

    private void CreateTarget()
    {
        EnsureFactories();
        var properties = new D2D1_RENDER_TARGET_PROPERTIES
        {
            // Software: a hardware target loads the GPU driver, whose DLLs, heaps and threads stay after release
            // (+13.5 MB private and idle wake-ups, measured T5.2). A pop-up or menu needs no GPU.
            type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE,
            pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
        };
        var hwndProperties = new D2D1_HWND_RENDER_TARGET_PROPERTIES
        {
            hwnd = _hwnd,
            pixelSize = new D2D_SIZE_U { width = (uint)Math.Max(_size.Width, 1), height = (uint)Math.Max(_size.Height, 1) },
        };
        ID2D1HwndRenderTarget* target;
        D2d->CreateHwndRenderTarget(&properties, &hwndProperties, &target);
        _target = target;
        var renderTarget = (ID2D1RenderTarget*)target;
        renderTarget->SetDpi(Dpi, Dpi);
        // ClearType needs an opaque background; on acrylic it fringes.
        renderTarget->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        var white = new D2D1_COLOR_F { r = 1, g = 1, b = 1, a = 1 };
        ID2D1SolidColorBrush* brush;
        renderTarget->CreateSolidColorBrush(&white, null, &brush);
        _brush = brush;
    }

    private void ReleaseTarget()
    {
        Release(ref _brush);
        Release(ref _target);
    }

    protected void ReleaseGraphics()
    {
        ReleaseLayouts();
        ReleaseTarget();
        var d2d = D2d;
        Release(ref d2d);
        D2d = null;
        var dwrite = DWrite;
        Release(ref dwrite);
        DWrite = null;
        Unload(ref _d2dModule);
        Unload(ref _dwriteModule);
    }

    private static void Unload(ref nint module)
    {
        if (module == 0)
            return;
        NativeLibrary.Free(module);
        module = 0;
    }

    protected static void Release<T>(ref T* pointer) where T : unmanaged
    {
        if (pointer is null)
            return;
        ((Windows.Win32.System.Com.IUnknown*)pointer)->Release();
        pointer = null;
    }

    // ---- messages ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        // An exception must never unwind into user32: log it, hide, carry on (fail-soft).
        try
        {
            if (s_windows.TryGetValue((nint)hwnd.Value, out var self) && self.Handle(message, wParam, lParam, out var result))
                return result;
        }
        catch (Exception e)
        {
            Log.Error($"popup: message 0x{message:X}", e);
            if (s_windows.TryGetValue((nint)hwnd.Value, out var failed) && message != PInvoke.WM_NCACTIVATE)
                failed.Hidden();
            return (LRESULT)0;
        }
        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private bool Handle(uint message, WPARAM wParam, LPARAM lParam, out LRESULT result)
    {
        result = (LRESULT)0;
        switch (message)
        {
            case PInvoke.WM_PAINT:
                PAINTSTRUCT paint;
                PInvoke.BeginPaint(_hwnd, &paint);
                try
                {
                    Paint();
                }
                finally
                {
                    PInvoke.EndPaint(_hwnd, &paint);
                }
                return true;
            case PInvoke.WM_ERASEBKGND:
                result = (LRESULT)1;
                return true;
            case PInvoke.WM_NCACTIVATE:
                // Always draw as active, so DWM keeps the acrylic instead of the inactive flat fill.
                result = PInvoke.DefWindowProc(_hwnd, message, 1, -1);
                return true;
            case PInvoke.WM_MOUSEACTIVATE:
                result = (LRESULT)(nint)PInvoke.MA_NOACTIVATE;
                return true;
            case PInvoke.WM_TIMER when wParam == FrameTimer:
                Frame();
                return true;
            case PInvoke.WM_SETTINGCHANGE:
                // Light/dark, accent or text size changed while showing: pick it up now.
                if (Visible)
                {
                    Theme = PopupTheme.Read();
                    ApplyTheme();
                    OnSettingsChanged();
                    Invalidate();
                }
                return false;
            default:
                return OnMessage(message, wParam, lParam, out result);
        }
    }

    public virtual void Dispose()
    {
        ReleaseGraphics();
        if (_hwnd != HWND.Null)
        {
            s_windows.Remove((nint)_hwnd.Value);
            if (PInvoke.IsWindow(_hwnd))
                PInvoke.DestroyWindow(_hwnd);
        }
        _hwnd = HWND.Null;
    }
}
