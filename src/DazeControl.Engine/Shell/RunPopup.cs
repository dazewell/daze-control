using System.Diagnostics;
using DazeControl.Core.Model;
using DazeControl.Runtime.Popup;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DazeControl.Engine.Shell;

/// <summary>
/// The run pop-up (UX §5.6, D-012) on the shared <see cref="AcrylicSurface"/>: click-through unless the run failed,
/// on the primary screen's work area. The window is made on the first run.
/// </summary>
internal sealed unsafe class RunPopup : AcrylicSurface
{
    public const string ClassName = "DazeControl.Popup";

    private const float WidthDip = 340, PaddingDip = 16, SpacingDip = 12, GlyphDip = 20, TrackDip = 4, RowMinDip = 28, HeadlineGapDip = 2;
    private const nuint HideTimer = 1;
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(200), FadeOut = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SpinPeriod = TimeSpan.FromSeconds(1);

    private static bool s_exists;

    private Layouts _layouts;
    private PopupView? _view;
    private PopupPosition _position;

    public RunPopup()
        : base(ClassName, clickThrough: true)
    {
        if (s_exists)
            throw new InvalidOperationException("Only one RunPopup per process.");
        s_exists = true;
    }

    /// <summary>A failure pop-up was selected: the user wants to see what happened.</summary>
    public event Action<PopupView>? Selected;

    public bool IsVisible => Visible;

    protected override bool Animating => Theme.Animations && _view?.Tone == PopupTone.Running;

    /// <summary>Shows or updates the pop-up at <paramref name="position"/> on the primary screen; never activates.</summary>
    public void Show(PopupView view, PopupPosition position)
    {
        BeginShow();
        _view = view;
        _position = position;
        SetClickable(view.Clickable);
        Layout();
        Place(position);
        ShowWindow(FadeIn);
        PInvoke.KillTimer(Hwnd, HideTimer);
        if (view.HideAfter is { } after)
            PInvoke.SetTimer(Hwnd, HideTimer, (uint)Math.Clamp(after.TotalMilliseconds, 1, int.MaxValue), null);
        Invalidate();
    }

    /// <summary>Fades out (or vanishes at once with Animation effects off), then frees the drawing resources.</summary>
    public void Hide()
    {
        if (!Visible)
            return;
        PInvoke.KillTimer(Hwnd, HideTimer);
        HideWindow(FadeOut);
    }

    /// <summary>Primary work area like the OSD, sized in DIPs × the primary monitor's DPI.</summary>
    private void Place(PopupPosition position)
    {
        var monitor = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, &info))
            return;
        if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded && dpi != Dpi)
        {
            Dpi = dpi;
            Layout();
        }
        var scale = Dpi / 96f;
        var size = ((int)Math.Ceiling(WidthDip * scale), (int)Math.Ceiling(_layouts.Height * scale));
        var area = info.rcWork;
        var (x, y) = PopupPlacement.Place(area.left, area.top, area.right - area.left, area.bottom - area.top,
            size.Item1, size.Item2, position, (int)Math.Round(PopupPlacement.MarginDip * scale));
        SetBounds(x, y, size.Item1, size.Item2);
    }

    protected override void OnFrame()
    {
        if (_view?.Tone == PopupTone.Running)
            Invalidate();
    }

    protected override void OnHidden()
    {
        PInvoke.KillTimer(Hwnd, HideTimer);
        _view = null;
    }

    protected override void OnSettingsChanged()
    {
        Layout();
        Place(_position);
    }

    // ---- drawing ----

    /// <summary>Text layouts and row heights for the current view, in DIPs.</summary>
    private struct Layouts
    {
        public IDWriteTextLayout* Glyph, Name, Status, Headline, Detail, Hint;
        public float Row1, StatusWidth, HeadlineHeight, DetailHeight, HintHeight, Height;
    }

    private void Layout()
    {
        ReleaseLayouts();
        if (_view is not { } view)
            return;
        EnsureFactories();
        var scale = Theme.TextScale;
        var content = WidthDip - 2 * PaddingDip;
        // Each layout goes into _layouts as soon as it exists: if a later one throws, ReleaseLayouts frees it.
        var status = _layouts.Status = Text(view.Status, 12 * scale, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, content, wrap: false);
        DWRITE_TEXT_METRICS metrics;
        status->GetMetrics(&metrics);
        // The 44-DIP status column (UX §5.6) holds "2 of 3" and "Done"; longer outcomes go to row 3 (Headline).
        var statusWidth = view.Status.Length == 0
            ? 0
            : Math.Min(Math.Max(44, (float)Math.Ceiling(metrics.widthIncludingTrailingWhitespace)), content / 2);
        status->SetMaxWidth(Math.Max(statusWidth, 1));
        status->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING);

        var nameWidth = content - GlyphDip - SpacingDip - (statusWidth > 0 ? statusWidth + 8 : 0);
        var name = _layouts.Name = Text(view.Title, 14 * scale, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD, nameWidth, wrap: false);
        name->GetMetrics(&metrics);
        var row1 = Math.Max(RowMinDip, (float)Math.Ceiling(metrics.height));

        IDWriteTextLayout* headline = null;
        float headlineHeight = 0;
        if (view.Headline is { } headlineText)
        {
            headline = _layouts.Headline = Text(headlineText, 12 * scale, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD, content, wrap: true);
            headline->GetMetrics(&metrics);
            headlineHeight = (float)Math.Ceiling(metrics.height) + HeadlineGapDip;
        }

        var detail = _layouts.Detail = Text(view.Detail, 12 * scale, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, content, wrap: true);
        detail->GetMetrics(&metrics);
        // At most two lines; the second one ends in "…" when the reason is longer (UX §5.6).
        var line = metrics.lineCount > 0 ? metrics.height / metrics.lineCount : 16 * scale;
        var detailHeight = (float)Math.Ceiling(Math.Min(metrics.height, 2 * line));
        detail->SetMaxHeight(detailHeight + 0.5f);

        IDWriteTextLayout* hint = null;
        float hintHeight = 0;
        if (view.Hint is { } hintText)
        {
            hint = _layouts.Hint = Text(hintText, 12 * scale, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, content, wrap: false);
            hint->GetMetrics(&metrics);
            hintHeight = (float)Math.Ceiling(metrics.height);
        }

        var glyph = _layouts.Glyph = GlyphText(view.Tone);
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
        return glyph is null ? null : IconText(glyph, 16, GlyphDip);
    }

    protected override void Render(ID2D1RenderTarget* target, ID2D1SolidColorBrush* solid)
    {
        if (_view is not { } view || _layouts.Name is null)
            return;
        var brush = (ID2D1Brush*)solid;

        var x = PaddingDip;
        var y = PaddingDip;
        var right = WidthDip - PaddingDip;
        var toneColour = view.Tone switch
        {
            PopupTone.Succeeded => Theme.Success,
            PopupTone.Partial => Theme.Caution,
            PopupTone.Failed => Theme.Critical,
            PopupTone.Stopped => Theme.Secondary,
            _ => Theme.Accent,
        };

        // Row 1: glyph | name | status.
        var glyphTop = y + (_layouts.Row1 - GlyphDip) / 2;
        SetColour(toneColour);
        if (_layouts.Glyph is not null)
            target->DrawTextLayout(new D2D_POINT_2F { x = x, y = glyphTop }, _layouts.Glyph, brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
        else
            DrawRing(target, brush, x + GlyphDip / 2, glyphTop + GlyphDip / 2);
        SetColour(Theme.Text);
        DrawCentred(target, brush, _layouts.Name, x + GlyphDip + SpacingDip, y, _layouts.Row1);
        if (_layouts.StatusWidth > 0)
        {
            SetColour(view.Tone is PopupTone.Running ? Theme.Secondary : toneColour);
            DrawCentred(target, brush, _layouts.Status, right - _layouts.StatusWidth, y, _layouts.Row1);
        }
        y += _layouts.Row1 + SpacingDip;

        // Row 2: determinate track of finished nodes.
        SetColour(Theme.Track);
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
        SetColour(Theme.Secondary);
        target->DrawTextLayout(new D2D_POINT_2F { x = x, y = y }, _layouts.Detail, brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_CLIP);
        if (_layouts.Hint is not null)
        {
            y += _layouts.DetailHeight + SpacingDip;
            SetColour(Theme.Accent);
            target->DrawTextLayout(new D2D_POINT_2F { x = x, y = y }, _layouts.Hint, brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
        }
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
        var turn = Theme.Animations
            ? Stopwatch.GetTimestamp() % (long)(SpinPeriod.TotalSeconds * Stopwatch.Frequency) / (SpinPeriod.TotalSeconds * Stopwatch.Frequency)
            : 0;
        var start = turn * 2 * Math.PI - Math.PI / 2;
        var end = start + 1.5 * Math.PI;
        ID2D1PathGeometry* path;
        D2d->CreatePathGeometry(&path);
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
            target->DrawGeometry((ID2D1Geometry*)path, brush, Theme.HighContrast ? 3 : 2, null);
        }
        finally
        {
            path->Release();
        }
    }

    protected override void ReleaseLayouts()
    {
        Release(ref _layouts.Glyph);
        Release(ref _layouts.Name);
        Release(ref _layouts.Status);
        Release(ref _layouts.Headline);
        Release(ref _layouts.Detail);
        Release(ref _layouts.Hint);
    }

    // ---- messages ----

    protected override bool OnMessage(uint message, WPARAM wParam, LPARAM lParam, out LRESULT result)
    {
        result = (LRESULT)0;
        switch (message)
        {
            case PInvoke.WM_LBUTTONUP:
                if (Clickable && _view is { } view)
                {
                    Hide();
                    Selected?.Invoke(view);
                }
                return true;
            case PInvoke.WM_TIMER when wParam == HideTimer:
                Hide();
                return true;
            default:
                return false;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        s_exists = false;
    }
}
