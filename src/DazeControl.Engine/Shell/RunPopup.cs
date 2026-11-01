using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DazeControl.Core.Model;
using DazeControl.Engine.Diagnostics;
using DazeControl.Runtime.Popup;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DazeControl.Engine.Shell;

/// <summary>
/// The run pop-up (UX §5.6, D-012): a Win32 pop-up with the DWM acrylic backdrop, drawn with Direct2D/DirectWrite.
/// Topmost, never activated, not in Alt+Tab; click-through unless the run failed. Message thread only. The window
/// is made on the first run; render target, text layouts and factories exist only while it is visible.
/// </summary>
internal sealed unsafe class RunPopup : IDisposable
{
    public const string ClassName = "DazeControl.Popup";

    private const float WidthDip = 340, PaddingDip = 16, SpacingDip = 12, GlyphDip = 20, TrackDip = 4, RowMinDip = 28, HeadlineGapDip = 2;
    private const nuint HideTimer = 1, FrameTimer = 2;
    private const int RecreateTarget = unchecked((int)0x8899000C); // D2DERR_RECREATE_TARGET
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(200), FadeOut = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SpinPeriod = TimeSpan.FromSeconds(1);

    private static RunPopup? s_current;
    private static bool s_registered;

    private HWND _hwnd;
    private ID2D1Factory* _d2d;
    private IDWriteFactory* _dwrite;
    private ID2D1HwndRenderTarget* _target;
    private ID2D1SolidColorBrush* _brush;
    private Layouts _layouts;

    private PopupView? _view;
    private PopupTheme _theme = PopupTheme.Read();
    private bool _visible;
    private PopupPosition _position;
    private bool _clickable;
    private uint _dpi = 96;
    private (int Width, int Height) _size;

    private double _alpha;
    private double _fadeFrom, _fadeTo;
    private long _fadeStart;
    private TimeSpan _fadeLength;

    public RunPopup()
    {
        if (s_current is not null)
            throw new InvalidOperationException("Only one RunPopup per process.");
        s_current = this;
    }

    /// <summary>A failure pop-up was selected: the user wants to see what happened.</summary>
    public event Action<PopupView>? Selected;

    public bool IsVisible => _visible;

    /// <summary>Shows or updates the pop-up at <paramref name="position"/> on the primary screen; never activates.</summary>
    public void Show(PopupView view, PopupPosition position)
    {
        if (!_visible)
            _theme = PopupTheme.Read();
        EnsureWindow();
        if (!_visible)
            ApplyTheme(); // the window outlives a light/dark switch between runs
        _view = view;
        _position = position;
        SetClickable(view.Clickable);
        Layout();
        Place(position);
        if (!_visible)
        {
            _visible = true;
            _alpha = 0;
            ApplyAlpha();
            PInvoke.SetWindowPos(_hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
                | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        }
        StartFade(1, FadeIn);
        PInvoke.KillTimer(_hwnd, HideTimer);
        if (view.HideAfter is { } after)
            PInvoke.SetTimer(_hwnd, HideTimer, (uint)Math.Clamp(after.TotalMilliseconds, 1, int.MaxValue), null);
        UpdateFrameTimer();
        PInvoke.InvalidateRect(_hwnd, (RECT?)null, false);
    }

    /// <summary>Fades out (or vanishes at once with Animation effects off), then frees the drawing resources.</summary>
    public void Hide()
    {
        if (!_visible)
            return;
        PInvoke.KillTimer(_hwnd, HideTimer);
        StartFade(0, FadeOut);
        UpdateFrameTimer();
    }

    private void EnsureWindow()
    {
        if (_hwnd != HWND.Null)
            return;
        var instance = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value;
        fixed (char* className = ClassName)
        fixed (char* title = "Daze Control")
        {
            if (!s_registered)
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
                    throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
                s_registered = true;
            }
            // Click-through needs WS_EX_TRANSPARENT on a layered window; the layered alpha is also the fade.
            _hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST
                | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT,
                className, title, WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, HWND.Null, HMENU.Null, instance, null);
        }
        if (_hwnd == HWND.Null)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        _clickable = false;
        ApplyAlpha();

        // The whole client area is "frame", so the backdrop shows through wherever we draw nothing.
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        Cosmetic("frame", PInvoke.DwmExtendFrameIntoClientArea(_hwnd, &margins));
        var backdrop = DWM_SYSTEMBACKDROP_TYPE.DWMSBT_TRANSIENTWINDOW;
        Cosmetic("backdrop", PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE, &backdrop, sizeof(DWM_SYSTEMBACKDROP_TYPE)));
        var corners = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        Cosmetic("corners", PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corners, sizeof(DWM_WINDOW_CORNER_PREFERENCE)));
        ApplyTheme();
        // DWM draws an inactive window's backdrop as a flat fill; this window is never active, so tell its frame it is.
        PInvoke.SendMessage(_hwnd, PInvoke.WM_NCACTIVATE, 1, 0);
    }

    private void ApplyTheme()
    {
        BOOL dark = !_theme.Light;
        Cosmetic("dark mode", PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, (uint)sizeof(BOOL)));
    }

    private static void Cosmetic(string what, HRESULT result)
    {
        // Square corners or a flat fill are no reason to show nothing.
        if (result.Failed)
            Log.Warn($"popup: {what} 0x{result.Value:X8}");
    }

    private void SetClickable(bool clickable)
    {
        if (clickable == _clickable)
            return;
        _clickable = clickable;
        var ex = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        ex = clickable ? ex & ~WINDOW_EX_STYLE.WS_EX_TRANSPARENT : ex | WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        _ = PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)ex);
    }

    /// <summary>Primary work area like the OSD, sized in DIPs × the primary monitor's DPI.</summary>
    private void Place(PopupPosition position)
    {
        var monitor = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, &info))
            return;
        if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded && dpi != _dpi)
        {
            _dpi = dpi;
            Layout();
        }
        var scale = _dpi / 96f;
        var size = ((int)Math.Ceiling(WidthDip * scale), (int)Math.Ceiling(_layouts.Height * scale));
        var area = info.rcWork;
        var (x, y) = PopupPlacement.Place(area.left, area.top, area.right - area.left, area.bottom - area.top,
            size.Item1, size.Item2, position, (int)Math.Round(PopupPlacement.MarginDip * scale));
        PInvoke.SetWindowPos(_hwnd, HWND.HWND_TOPMOST, x, y, size.Item1, size.Item2, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        if (size != _size)
        {
            _size = size;
            if (_target is not null)
            {
                var pixels = new D2D_SIZE_U { width = (uint)size.Item1, height = (uint)size.Item2 };
                _target->Resize(&pixels);
            }
        }
    }

    private void StartFade(double to, TimeSpan length)
    {
        if (!_theme.Animations)
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

    /// <summary>16 ms frames only while fading or spinning; no timer at all once the pop-up is still or gone.</summary>
    private void UpdateFrameTimer()
    {
        if (_hwnd == HWND.Null)
            return;
        var spinning = _visible && _theme.Animations && _view?.Tone == PopupTone.Running;
        if (Fading || spinning)
            PInvoke.SetTimer(_hwnd, FrameTimer, 16, null);
        else
            PInvoke.KillTimer(_hwnd, FrameTimer);
    }

    private void OnFrame()
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
        if (_view?.Tone == PopupTone.Running)
            PInvoke.InvalidateRect(_hwnd, (RECT?)null, false);
        UpdateFrameTimer();
    }

    private void ApplyAlpha() =>
        PInvoke.SetLayeredWindowAttributes(_hwnd, new COLORREF(0), (byte)Math.Round(_alpha * 255), LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);

    private void Hidden()
    {
        PInvoke.KillTimer(_hwnd, FrameTimer);
        PInvoke.KillTimer(_hwnd, HideTimer);
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
        _visible = false;
        _view = null;
        // Back to the idle footprint: the D3D device behind the render target is most of the pop-up's memory.
        ReleaseGraphics();
    }

    // ---- drawing ----

    /// <summary>Text layouts and row heights for the current view, in DIPs.</summary>
    private struct Layouts
    {
        public IDWriteTextLayout* Glyph, Name, Status, Headline, Detail, Hint;
        public float Row1, StatusWidth, HeadlineHeight, DetailHeight, HintHeight, Height;
    }

    private void EnsureFactories()
    {
        if (_d2d is null)
        {
            void* factory;
            var iid = ID2D1Factory.IID_Guid;
            PInvoke.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, &iid, null, &factory).ThrowOnFailure();
            _d2d = (ID2D1Factory*)factory;
        }
        if (_dwrite is null)
        {
            PInvoke.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, out IDWriteFactory* factory).ThrowOnFailure();
            _dwrite = factory;
        }
    }

    private void Layout()
    {
        ReleaseLayouts();
        if (_view is not { } view)
            return;
        EnsureFactories();
        var scale = _theme.TextScale;
        var content = WidthDip - 2 * PaddingDip;
        var status = Text(view.Status, 12 * scale, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, content, wrap: false);
        DWRITE_TEXT_METRICS metrics;
        status->GetMetrics(&metrics);
        // The 44-DIP status column (UX §5.6) holds "2 of 3" and "Done"; longer outcomes go to row 3 (Headline).
        var statusWidth = view.Status.Length == 0
            ? 0
            : Math.Min(Math.Max(44, (float)Math.Ceiling(metrics.widthIncludingTrailingWhitespace)), content / 2);
        status->SetMaxWidth(Math.Max(statusWidth, 1));
        status->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING);

        var nameWidth = content - GlyphDip - SpacingDip - (statusWidth > 0 ? statusWidth + 8 : 0);
        var name = Text(view.Title, 14 * scale, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD, nameWidth, wrap: false);
        name->GetMetrics(&metrics);
        var row1 = Math.Max(RowMinDip, (float)Math.Ceiling(metrics.height));

        IDWriteTextLayout* headline = null;
        float headlineHeight = 0;
        if (view.Headline is { } headlineText)
        {
            headline = Text(headlineText, 12 * scale, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD, content, wrap: true);
            headline->GetMetrics(&metrics);
            headlineHeight = (float)Math.Ceiling(metrics.height) + HeadlineGapDip;
        }

        var detail = Text(view.Detail, 12 * scale, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, content, wrap: true);
        detail->GetMetrics(&metrics);
        // At most two lines; the second one ends in "…" when the reason is longer (UX §5.6).
        var line = metrics.lineCount > 0 ? metrics.height / metrics.lineCount : 16 * scale;
        var detailHeight = (float)Math.Ceiling(Math.Min(metrics.height, 2 * line));
        detail->SetMaxHeight(detailHeight + 0.5f);

        IDWriteTextLayout* hint = null;
        float hintHeight = 0;
        if (view.Hint is { } hintText)
        {
            hint = Text(hintText, 12 * scale, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, content, wrap: false);
            hint->GetMetrics(&metrics);
            hintHeight = (float)Math.Ceiling(metrics.height);
        }

        var glyph = GlyphText(view.Tone);
        _layouts = new Layouts
        {
            Glyph = glyph, Name = name, Status = status, Headline = headline, Detail = detail, Hint = hint,
            Row1 = row1, StatusWidth = statusWidth, HeadlineHeight = headlineHeight, DetailHeight = detailHeight,
            HintHeight = hintHeight,
            Height = PaddingDip + row1 + SpacingDip + TrackDip + SpacingDip + headlineHeight + detailHeight
                + (hint is null ? 0 : SpacingDip + hintHeight) + PaddingDip,
        };
    }

    private IDWriteTextLayout* GlyphText(PopupTone tone)
    {
        var glyph = tone switch
        {
            PopupTone.Succeeded => "",
            PopupTone.Partial => "",
            PopupTone.Failed => "",
            PopupTone.Stopped => "",
            _ => null, // running: a drawn ring
        };
        if (glyph is null)
            return null;
        IDWriteTextFormat* format;
        fixed (char* family = "Segoe Fluent Icons")
        fixed (char* locale = "en-us")
            _dwrite->CreateTextFormat(family, null, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL,
                DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, 16, locale, &format);
        try
        {
            format->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
            format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            IDWriteTextLayout* layout;
            fixed (char* text = glyph)
                _dwrite->CreateTextLayout(text, (uint)glyph.Length, format, GlyphDip, GlyphDip, &layout);
            return layout;
        }
        finally
        {
            format->Release();
        }
    }

    private IDWriteTextLayout* Text(string text, float size, DWRITE_FONT_WEIGHT weight, float width, bool wrap)
    {
        IDWriteTextFormat* format;
        fixed (char* family = "Segoe UI Variable Text")
        fixed (char* locale = "en-us")
            _dwrite->CreateTextFormat(family, null, weight, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, size, locale, &format);
        try
        {
            format->SetWordWrapping(wrap ? DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_WRAP : DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);
            // "…" where it doesn't fit: the name on one line, the reason on two.
            IDWriteInlineObject* ellipsis;
            _dwrite->CreateEllipsisTrimmingSign(format, &ellipsis);
            var trimming = new DWRITE_TRIMMING { granularity = DWRITE_TRIMMING_GRANULARITY.DWRITE_TRIMMING_GRANULARITY_CHARACTER };
            format->SetTrimming(&trimming, ellipsis);
            ellipsis->Release();
            IDWriteTextLayout* layout;
            fixed (char* chars = text)
                _dwrite->CreateTextLayout(chars, (uint)text.Length, format, width, 1000, &layout);
            return layout;
        }
        finally
        {
            format->Release();
        }
    }

    private void Render()
    {
        if (_view is not { } view || _layouts.Name is null)
            return;
        if (_target is null)
            CreateTarget();
        var target = (ID2D1RenderTarget*)_target;
        var brush = (ID2D1Brush*)_brush;
        target->BeginDraw();
        var clear = default(D2D1_COLOR_F); // transparent: the acrylic shows through
        target->Clear(&clear);

        var x = PaddingDip;
        var y = PaddingDip;
        var right = WidthDip - PaddingDip;
        var toneColour = view.Tone switch
        {
            PopupTone.Succeeded => _theme.Success,
            PopupTone.Partial => _theme.Caution,
            PopupTone.Failed => _theme.Critical,
            PopupTone.Stopped => _theme.Secondary,
            _ => _theme.Accent,
        };

        // Row 1: glyph | name | status.
        var glyphTop = y + (_layouts.Row1 - GlyphDip) / 2;
        SetColour(toneColour);
        if (_layouts.Glyph is not null)
            target->DrawTextLayout(new D2D_POINT_2F { x = x, y = glyphTop }, _layouts.Glyph, brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
        else
            DrawRing(target, brush, x + GlyphDip / 2, glyphTop + GlyphDip / 2);
        SetColour(_theme.Text);
        DrawCentred(target, brush, _layouts.Name, x + GlyphDip + SpacingDip, y, _layouts.Row1);
        if (_layouts.StatusWidth > 0)
        {
            SetColour(view.Tone is PopupTone.Running ? _theme.Secondary : toneColour);
            DrawCentred(target, brush, _layouts.Status, right - _layouts.StatusWidth, y, _layouts.Row1);
        }
        y += _layouts.Row1 + SpacingDip;

        // Row 2: determinate track of finished nodes.
        SetColour(_theme.Track);
        var track = new D2D1_ROUNDED_RECT { rect = new D2D_RECT_F { left = x, top = y, right = right, bottom = y + TrackDip }, radiusX = 2, radiusY = 2 };
        target->FillRoundedRectangle(&track, brush);
        if (view.Progress > 0)
        {
            SetColour(toneColour);
            track.rect.right = x + (float)((right - x) * view.Progress);
            target->FillRoundedRectangle(&track, brush);
        }
        y += TrackDip + SpacingDip;

        // Row 3: current step or the outcome in words; row 4: the failure's way to the run.
        if (_layouts.Headline is not null)
        {
            SetColour(toneColour);
            target->DrawTextLayout(new D2D_POINT_2F { x = x, y = y }, _layouts.Headline, brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
            y += _layouts.HeadlineHeight;
        }
        SetColour(_theme.Secondary);
        target->DrawTextLayout(new D2D_POINT_2F { x = x, y = y }, _layouts.Detail, brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_CLIP);
        if (_layouts.Hint is not null)
        {
            y += _layouts.DetailHeight + SpacingDip;
            SetColour(_theme.Accent);
            target->DrawTextLayout(new D2D_POINT_2F { x = x, y = y }, _layouts.Hint, brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
        }

        var result = target->EndDraw(null, null);
        if (result.Value == RecreateTarget)
            ReleaseTarget(); // D2DERR_RECREATE_TARGET, device lost (driver update, GPU reset): the next paint makes a new one
        else
            result.ThrowOnFailure();
    }

    private static void DrawCentred(ID2D1RenderTarget* target, ID2D1Brush* brush, IDWriteTextLayout* layout, float x, float y, float row)
    {
        DWRITE_TEXT_METRICS metrics;
        layout->GetMetrics(&metrics);
        target->DrawTextLayout(new D2D_POINT_2F { x = x, y = y + (row - metrics.height) / 2 }, layout, brush,
            D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_CLIP);
    }

    /// <summary>The running ring: a ¾ arc that turns once a second, still with Animation effects off (UX §5.3).</summary>
    private void DrawRing(ID2D1RenderTarget* target, ID2D1Brush* brush, float cx, float cy)
    {
        const float radius = 8;
        var turn = _theme.Animations
            ? Stopwatch.GetTimestamp() % (long)(SpinPeriod.TotalSeconds * Stopwatch.Frequency) / (SpinPeriod.TotalSeconds * Stopwatch.Frequency)
            : 0;
        var start = turn * 2 * Math.PI - Math.PI / 2;
        var end = start + 1.5 * Math.PI;
        ID2D1PathGeometry* path;
        _d2d->CreatePathGeometry(&path);
        try
        {
            ID2D1GeometrySink* sink;
            path->Open(&sink);
            sink->BeginFigure(new D2D_POINT_2F { x = cx + radius * (float)Math.Cos(start), y = cy + radius * (float)Math.Sin(start) },
                D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_HOLLOW);
            var arc = new D2D1_ARC_SEGMENT
            {
                point = new D2D_POINT_2F { x = cx + radius * (float)Math.Cos(end), y = cy + radius * (float)Math.Sin(end) },
                size = new D2D_SIZE_F { width = radius, height = radius },
                sweepDirection = D2D1_SWEEP_DIRECTION.D2D1_SWEEP_DIRECTION_CLOCKWISE,
                arcSize = D2D1_ARC_SIZE.D2D1_ARC_SIZE_LARGE,
            };
            sink->AddArc(&arc);
            sink->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_OPEN);
            sink->Close();
            sink->Release();
            target->DrawGeometry((ID2D1Geometry*)path, brush, 2, null);
        }
        finally
        {
            path->Release();
        }
    }

    private void SetColour(D2D1_COLOR_F colour) => _brush->SetColor(&colour);

    private void CreateTarget()
    {
        EnsureFactories();
        var properties = new D2D1_RENDER_TARGET_PROPERTIES
        {
            type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_DEFAULT,
            pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
        };
        var hwndProperties = new D2D1_HWND_RENDER_TARGET_PROPERTIES
        {
            hwnd = _hwnd,
            pixelSize = new D2D_SIZE_U { width = (uint)Math.Max(_size.Width, 1), height = (uint)Math.Max(_size.Height, 1) },
        };
        ID2D1HwndRenderTarget* target;
        _d2d->CreateHwndRenderTarget(&properties, &hwndProperties, &target);
        _target = target;
        var renderTarget = (ID2D1RenderTarget*)target;
        renderTarget->SetDpi(_dpi, _dpi);
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

    private void ReleaseLayouts()
    {
        Release(ref _layouts.Glyph);
        Release(ref _layouts.Name);
        Release(ref _layouts.Status);
        Release(ref _layouts.Headline);
        Release(ref _layouts.Detail);
        Release(ref _layouts.Hint);
    }

    private void ReleaseGraphics()
    {
        ReleaseLayouts();
        ReleaseTarget();
        Release(ref _d2d);
        Release(ref _dwrite);
    }

    private static void Release<T>(ref T* pointer) where T : unmanaged
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
            if (s_current is { } self && hwnd == self._hwnd && self.Handle(message, wParam, out var result))
                return result;
        }
        catch (Exception e)
        {
            Log.Error($"popup: message 0x{message:X}", e);
            if (s_current is { } failed && hwnd == failed._hwnd && message != PInvoke.WM_NCACTIVATE)
                failed.Hidden();
            return (LRESULT)0;
        }
        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private bool Handle(uint message, WPARAM wParam, out LRESULT result)
    {
        result = (LRESULT)0;
        switch (message)
        {
            case PInvoke.WM_PAINT:
                PAINTSTRUCT paint;
                PInvoke.BeginPaint(_hwnd, &paint);
                try
                {
                    Render();
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
            case PInvoke.WM_LBUTTONUP:
                if (_clickable && _view is { } view)
                {
                    Hide();
                    Selected?.Invoke(view);
                }
                return true;
            case PInvoke.WM_TIMER:
                if (wParam == HideTimer)
                    Hide();
                else if (wParam == FrameTimer)
                    OnFrame();
                return true;
            case PInvoke.WM_SETTINGCHANGE:
                // Light/dark, accent or text size changed while showing: pick it up now.
                if (_visible)
                {
                    _theme = PopupTheme.Read();
                    ApplyTheme();
                    Layout();
                    Place(_position);
                    PInvoke.InvalidateRect(_hwnd, (RECT?)null, false);
                }
                return false;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        ReleaseGraphics();
        if (_hwnd != HWND.Null && PInvoke.IsWindow(_hwnd))
            PInvoke.DestroyWindow(_hwnd);
        _hwnd = HWND.Null;
        s_current = null;
    }
}
