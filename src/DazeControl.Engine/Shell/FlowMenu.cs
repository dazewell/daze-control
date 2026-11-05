using System.Diagnostics;
using DazeControl.Engine.Diagnostics;
using DazeControl.Runtime.Menus;
using DazeControl.Runtime.Providers;
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
/// The flow menu window (M12, DESIGN §3.3, §4): a borderless, never-activated pop-up on the shared
/// <see cref="AcrylicSurface"/> that shows a <see cref="MenuModel"/> as a list or a grid. Keys come from
/// <see cref="MenuKeys"/> (hotkeys, only while open), the mouse from the window and a pass-through hook, the pad from
/// an <see cref="IMenuPadFeed"/>. It is the engine's <see cref="IMenuPresenter"/>: <see cref="ShowAsync"/> may be called
/// from any thread; everything else runs on the message thread. The caller holds the gate (one menu at a time) and has
/// asked <see cref="FullScreen"/> already. Every close path (pick, cancel, timeout, step cancel, engine exit) frees the
/// keys, the hook and the pad.
/// </summary>
internal sealed unsafe class FlowMenu : AcrylicSurface, IMenuPresenter
{
    public const string ClassName = "DazeControl.Menu";

    private const float PaddingDip = 16, SpacingDip = 8, RowGapDip = 4, TileGapDip = 8, HintDip = 24, TitleRowMin = 32, ScreenMarginDip = 48;
    private const nuint CountdownTimer = 3;
    private const int WheelStepDip = 56;
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(150), FadeOut = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SlideLength = TimeSpan.FromMilliseconds(120), ShakeLength = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ClosingShows = TimeSpan.FromSeconds(5);

    private static bool s_exists;

    private readonly MessageWindow _messages;
    private readonly MenuKeys _keys;
    private readonly MenuMouseHook _mouse = new();
    private readonly IMenuPadFeed? _pad;
    private readonly TimeProvider _time;

    private MenuRequest? _request;
    private MenuModel? _model;
    private TaskCompletionSource<MenuOutcome>? _result;
    private IDisposable? _padSubscription;

    // Layout, in DIPs.
    private Layouts _layouts = new();
    private float _width, _height, _bodyTop, _viewport, _content, _scroll, _rowHeight, _tileWidth, _tileHeight, _gridLeft;
    private float _maxHeight;
    private bool _large;
    private (int X, int Y, int Width, int Height) _bounds; // physical pixels, read by the mouse hook

    // Focus animation: the drawn focus rectangle in content coordinates.
    private D2D_RECT_F _from, _drawn;
    private bool _haveDrawn;
    private long _slideStart, _shakeStart;
    private bool _sliding, _shaking;

    public FlowMenu(MessageWindow messages, IMenuPadFeed? pad, TimeProvider time)
        : base(ClassName, clickThrough: false)
    {
        if (s_exists)
            throw new InvalidOperationException("Only one FlowMenu per process.");
        s_exists = true;
        _messages = messages;
        _keys = new MenuKeys(messages);
        _pad = pad;
        _time = time;
    }

    protected override bool Animating => _sliding || _shaking;

    // ---- the presenter ----

    public Task<MenuOutcome> ShowAsync(MenuRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new TaskCompletionSource<MenuOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A stopped flow or a step timeout closes the menu and ends the step with the cancellation.
        var registration = cancellationToken.Register(() =>
        {
            result.TrySetCanceled(cancellationToken);
            _messages.Post(() => CloseFor(result));
        });
        _ = result.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        _messages.Post(() => Open(request, result));
        return result.Task;
    }

    /// <summary>Message thread.</summary>
    private void Open(MenuRequest request, TaskCompletionSource<MenuOutcome> result)
    {
        if (result.Task.IsCompleted)
            return; // cancelled while queued
        try
        {
            Teardown(); // a previous menu may still be fading out
            _request = request;
            _result = result;
            BeginShow();
            _large = request.Large;
            _model = new MenuModel(request.Items, request.Layout, request.CloseAfter, _time, request.LastPickedFlowId);
            var model = _model;
            model.Changed += () => _messages.Post(() => OnChanged(model));
            model.Rejected += () => _messages.Post(() => OnRejected(model));
            model.Completed += outcome => _messages.Post(() => Complete(model, outcome));
            var area = Monitor();
            Layout(area.MaxHeightDip);
            Place(area);
            _scroll = 0;
            EnsureFocusVisible();
            _haveDrawn = false;
            _sliding = _shaking = false;
            SetClickable(true);
            _keys.Enable(key => MenuKeyMap.Apply(model, key));
            _mouse.Start(OnMouse);
            _padSubscription = _pad?.Start(model.Pad);
            ShowWindow(FadeIn);
            ArmCountdown();
            Invalidate();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error("menu: couldn't open", e);
            Teardown();
            result.TrySetException(new StepFailedException("The menu couldn't open. Try again."));
            if (Visible)
                HideWindow(FadeOut);
        }
    }

    /// <summary>The menu ended on its own (pick, Esc, B, click outside, close timer): free everything, answer, fade out.</summary>
    private void Complete(MenuModel model, MenuOutcome outcome)
    {
        if (!ReferenceEquals(model, _model))
            return;
        var result = _result;
        Teardown();
        result?.TrySetResult(outcome);
        HideWindow(FadeOut);
    }

    private void CloseFor(TaskCompletionSource<MenuOutcome> result)
    {
        if (!ReferenceEquals(result, _result))
            return;
        Teardown();
        HideWindow(FadeOut);
    }

    /// <summary>Keys, hook, pad and model go on every close path; the window itself fades on its own.</summary>
    private void Teardown()
    {
        _keys.Disable();
        _mouse.Stop();
        _padSubscription?.Dispose();
        _padSubscription = null;
        PInvoke.KillTimer(Hwnd, CountdownTimer);
        _model?.Dispose();
        _model = null;
        _result = null;
    }

    private void OnChanged(MenuModel model)
    {
        if (!ReferenceEquals(model, _model))
            return;
        StartSlide();
        EnsureFocusVisible();
        ArmCountdown();
        UpdateFrameTimer();
        Invalidate();
    }

    private void OnRejected(MenuModel model)
    {
        if (!ReferenceEquals(model, _model) || !Theme.Animations)
            return;
        _shaking = true;
        _shakeStart = Stopwatch.GetTimestamp();
        UpdateFrameTimer();
    }

    // ---- placement ----

    private readonly record struct Area(RECT Rect, uint Dpi, float MaxHeightDip);

    /// <summary>The monitor of the foreground window (a game on the TV), else the cursor's; its work area, or all of it under a covering window.</summary>
    private Area Monitor()
    {
        var foreground = PInvoke.GetForegroundWindow();
        HMONITOR monitor;
        RECT window = default;
        var covering = false;
        if (foreground != HWND.Null && foreground != Hwnd)
        {
            monitor = PInvoke.MonitorFromWindow(foreground, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
            covering = PInvoke.GetWindowRect(foreground, out window);
        }
        else
        {
            PInvoke.GetCursorPos(out var point);
            monitor = PInvoke.MonitorFromPoint(point, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        }
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        PInvoke.GetMonitorInfo(monitor, &info);
        // A window that covers the whole monitor (borderless game) owns the work area too: centre on all of it.
        var rect = covering && window.left <= info.rcMonitor.left && window.top <= info.rcMonitor.top
            && window.right >= info.rcMonitor.right && window.bottom >= info.rcMonitor.bottom ? info.rcMonitor : info.rcWork;
        var dpi = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var effective, out _).Succeeded ? effective : 96u;
        Dpi = dpi;
        return new Area(rect, dpi, (rect.bottom - rect.top) * 96f / dpi - ScreenMarginDip);
    }

    private void Place(Area area)
    {
        var scale = area.Dpi / 96f;
        var width = (int)Math.Ceiling(_width * scale);
        var height = (int)Math.Ceiling(_height * scale);
        var x = area.Rect.left + (area.Rect.right - area.Rect.left - width) / 2;
        var y = area.Rect.top + (area.Rect.bottom - area.Rect.top - height) / 2;
        _bounds = (x, y, width, height);
        SetBounds(x, y, width, height);
    }

    protected override void OnSettingsChanged()
    {
        if (_request is null)
            return;
        var area = Monitor();
        Layout(area.MaxHeightDip);
        Place(area);
        EnsureFocusVisible();
    }

    // ---- layout ----

    private struct ItemLayout
    {
        public IDWriteTextLayout* Glyph, Name, Caption;
    }

    private sealed class Layouts
    {
        public ItemLayout[] Items = [];
    }

    private IDWriteTextLayout* _title, _count, _emptyHead, _emptyBody, _hintRun, _hintClose, _hintKeys, _badgeA, _badgeB;
    private float _titleRow, _titleWidth, _countWidth, _emptyHeight, _hintHeight;

    private static string Caption(MenuItemState state) => state switch
    {
        MenuItemState.Running => "Running",
        MenuItemState.NeedsFixing => "Needs fixing",
        MenuItemState.TurnedOff => "Turned off",
        _ => "",
    };

    private static string GlyphOf(MenuItemState state) => state switch
    {
        MenuItemState.Running => "",
        MenuItemState.NeedsFixing => "",
        MenuItemState.TurnedOff => "",
        _ => "",
    };

    private void Layout(float maxHeightDip)
    {
        ReleaseLayouts();
        if (_request is not { } request || _model is not { } model)
            return;
        _maxHeight = maxHeightDip;
        EnsureFactories();
        var scale = Theme.TextScale;
        var items = request.Items;
        var grid = request.Layout == MenuLayout.Grid;
        var large = _large;
        var columns = model.Columns;
        _tileWidth = large ? 184 : 136;
        var tileMin = large ? 152 : 112;
        var glyphBox = large ? 48f : 40f;
        var glyphSize = grid ? (large ? 48f : 32f) : (large ? 32f : 24f);
        var bodySize = (large ? 18f : 14f) * scale;
        var captionSize = (large ? 14f : 12f) * scale;
        var titleSize = (large ? 28f : 20f) * scale;
        var gridWidth = columns * _tileWidth + (columns - 1) * TileGapDip;
        _width = grid ? Math.Max(gridWidth + 2 * PaddingDip, large ? 440 : 320) : large ? 560 : 400;
        _gridLeft = PaddingDip + (_width - 2 * PaddingDip - gridWidth) / 2;
        var content = _width - 2 * PaddingDip;

        DWRITE_TEXT_METRICS metrics;
        var count = items.Count == 1 ? "1 choice" : $"{items.Count} choices";
        _count = Text(count, captionSize, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, content, wrap: false);
        _count->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING);
        _count->GetMetrics(&metrics);
        _countWidth = (float)Math.Ceiling(metrics.widthIncludingTrailingWhitespace);
        _count->SetMaxWidth(_countWidth);
        _titleWidth = content - _countWidth - SpacingDip;
        _title = Text(request.Title, titleSize, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD, _titleWidth, wrap: false);
        _title->GetMetrics(&metrics);
        _titleRow = Math.Max(TitleRowMin, (float)Math.Ceiling(metrics.height));

        var layouts = new Layouts { Items = new ItemLayout[items.Count] };
        _layouts = layouts; // from here ReleaseLayouts frees whatever exists if a later call throws
        var anyCaption = items.Any(i => Caption(i.State).Length > 0);
        float captionHeight = 0;
        var nameHeight = 0f;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            ref var layout = ref layouts.Items[i];
            layout.Glyph = IconText(GlyphOf(item.State), glyphSize, glyphBox);
            var caption = Caption(item.State);
            if (grid)
            {
                var width = _tileWidth - 16;
                layout.Name = Text(item.Name, bodySize, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, width, wrap: true);
                layout.Name->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
                layout.Name->GetMetrics(&metrics);
                var line = metrics.lineCount > 0 ? metrics.height / metrics.lineCount : bodySize * 1.4f;
                var height = (float)Math.Ceiling(Math.Min(metrics.height, 2 * line));
                layout.Name->SetMaxHeight(height + 0.5f);
                nameHeight = Math.Max(nameHeight, height);
                if (caption.Length > 0)
                {
                    layout.Caption = Text(caption, captionSize, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, width, wrap: false);
                    layout.Caption->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
                    layout.Caption->GetMetrics(&metrics);
                    captionHeight = Math.Max(captionHeight, (float)Math.Ceiling(metrics.height));
                }
            }
            else
            {
                float captionWidth = 0;
                if (caption.Length > 0)
                {
                    layout.Caption = Text(caption, captionSize, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, content, wrap: false);
                    layout.Caption->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING);
                    layout.Caption->GetMetrics(&metrics);
                    captionWidth = (float)Math.Ceiling(metrics.widthIncludingTrailingWhitespace);
                    layout.Caption->SetMaxWidth(captionWidth);
                }
                var nameWidth = content - 8 - glyphBox - 12 - 12 - (captionWidth > 0 ? captionWidth + 8 : 0);
                layout.Name = Text(item.Name, bodySize, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, Math.Max(nameWidth, 40), wrap: false);
                layout.Name->GetMetrics(&metrics);
                nameHeight = Math.Max(nameHeight, (float)Math.Ceiling(metrics.height));
            }
        }
        _rowHeight = Math.Max(large ? 72f : 56f, nameHeight + 16);
        _tileHeight = Math.Max(tileMin, 12 + glyphBox + 4 + nameHeight + (anyCaption ? captionHeight + 2 : 0) + 12);

        var itemsHeight = grid
            ? (items.Count == 0 ? 0 : ((items.Count + columns - 1) / columns) * _tileHeight + ((items.Count + columns - 1) / columns - 1) * TileGapDip)
            : items.Count * _rowHeight + Math.Max(items.Count - 1, 0) * RowGapDip;
        _emptyHeight = 0;
        if (model.NothingRunnable)
        {
            _emptyHead = Text("Nothing here can run right now", bodySize, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD, content, wrap: true);
            _emptyBody = Text("Turn on or fix these flows in Daze Control.", captionSize, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, content, wrap: true);
            _emptyHead->GetMetrics(&metrics);
            var head = (float)Math.Ceiling(metrics.height);
            _emptyBody->GetMetrics(&metrics);
            _emptyHeight = SpacingDip + head + 2 + (float)Math.Ceiling(metrics.height);
        }
        _content = itemsHeight + _emptyHeight;

        var hintSize = (large ? 14f : 12f) * scale;
        _hintRun = Text("Run", hintSize, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, content, wrap: false);
        _hintClose = Text("Close", hintSize, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, content, wrap: false);
        _hintKeys = Text("Enter Run · Esc Close", hintSize, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, content, wrap: false);
        _badgeA = Text("A", hintSize * 0.8f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD, 20, wrap: false);
        _badgeB = Text("B", hintSize * 0.8f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD, 20, wrap: false);
        _badgeA->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
        _badgeB->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
        _hintRun->GetMetrics(&metrics);
        _hintHeight = Math.Max(HintDip, (float)Math.Ceiling(metrics.height));

        var chrome = PaddingDip + _titleRow + SpacingDip + SpacingDip + _hintHeight + PaddingDip;
        _viewport = Math.Max(Math.Min(_content, _maxHeight - chrome), 0);
        _height = chrome + _viewport;
        _bodyTop = PaddingDip + _titleRow + SpacingDip;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(_content - _viewport, 0));
    }

    private D2D_RECT_F ItemRect(int index)
    {
        if (_request is not { } request || _model is not { } model)
            return default;
        if (request.Layout == MenuLayout.List)
        {
            var top = index * (_rowHeight + RowGapDip);
            return new D2D_RECT_F { left = PaddingDip, top = top, right = _width - PaddingDip, bottom = top + _rowHeight };
        }
        var column = index % model.Columns;
        var row = index / model.Columns;
        var left = _gridLeft + column * (_tileWidth + TileGapDip);
        var y = row * (_tileHeight + TileGapDip);
        return new D2D_RECT_F { left = left, top = y, right = left + _tileWidth, bottom = y + _tileHeight };
    }

    /// <summary>Keeps the focused choice in the viewport with one row of context (DESIGN §4.1).</summary>
    private void EnsureFocusVisible()
    {
        if (_model is not { } model || _content <= _viewport)
        {
            _scroll = 0;
            return;
        }
        var rect = ItemRect(model.Focus);
        var context = _request?.Layout == MenuLayout.Grid ? _tileHeight : _rowHeight;
        if (rect.top - context < _scroll)
            _scroll = rect.top - context;
        else if (rect.bottom + context > _scroll + _viewport)
            _scroll = rect.bottom + context - _viewport;
        _scroll = Math.Clamp(_scroll, 0, _content - _viewport);
    }

    private int HitTest(int xPixels, int yPixels)
    {
        if (_model is not { } model)
            return -1;
        var x = xPixels * 96f / Dpi;
        var y = yPixels * 96f / Dpi;
        if (y < _bodyTop || y > _bodyTop + _viewport)
            return -1;
        for (var i = 0; i < model.Items.Count; i++)
        {
            var rect = ItemRect(i);
            var top = rect.top - _scroll + _bodyTop;
            if (x >= rect.left && x < rect.right && y >= top && y < top + (rect.bottom - rect.top))
                return i;
        }
        return -1;
    }

    // ---- input ----

    /// <summary>Mouse hook, on the message thread: a press outside closes, the wheel over the menu scrolls.</summary>
    private void OnMouse(uint message, int x, int y, int delta)
    {
        var inside = x >= _bounds.X && x < _bounds.X + _bounds.Width && y >= _bounds.Y && y < _bounds.Y + _bounds.Height;
        var model = _model;
        if (model is null)
            return;
        if (message == PInvoke.WM_MOUSEWHEEL)
        {
            if (inside)
                _messages.Post(() => Scroll(model, -delta / 120f * WheelStepDip));
        }
        else if (!inside)
        {
            _messages.Post(() => model.Cancel());
        }
    }

    private void Scroll(MenuModel model, float deltaDip)
    {
        if (!ReferenceEquals(model, _model) || _content <= _viewport)
            return;
        _scroll = Math.Clamp(_scroll + deltaDip, 0, _content - _viewport);
        Invalidate();
    }

    protected override bool OnMessage(uint message, WPARAM wParam, LPARAM lParam, out LRESULT result)
    {
        result = (LRESULT)0;
        switch (message)
        {
            case PInvoke.WM_MOUSEMOVE:
                if (_model is { } hover && HitTest((short)(lParam.Value & 0xFFFF), (short)((lParam.Value >> 16) & 0xFFFF)) is var over and >= 0
                    && over != hover.Focus)
                    hover.Hover(over);
                return true;
            case PInvoke.WM_LBUTTONUP:
                if (_model is { } click && HitTest((short)(lParam.Value & 0xFFFF), (short)((lParam.Value >> 16) & 0xFFFF)) is var hit and >= 0)
                {
                    click.Hover(hit);
                    click.Activate(MenuInput.Mouse);
                }
                return true;
            case PInvoke.WM_TIMER when wParam == CountdownTimer:
                ArmCountdown();
                Invalidate();
                return true;
            default:
                return false;
        }
    }

    /// <summary>"Closing in N s" shows for the last 5 s (#31): one timer for the moment it starts, then one tick a second.</summary>
    private void ArmCountdown()
    {
        PInvoke.KillTimer(Hwnd, CountdownTimer);
        if (_model is not { } model)
            return;
        var remaining = model.Remaining;
        var wait = remaining > ClosingShows ? remaining - ClosingShows : TimeSpan.FromSeconds(1);
        PInvoke.SetTimer(Hwnd, CountdownTimer, (uint)Math.Clamp(wait.TotalMilliseconds, 50, int.MaxValue), null);
    }

    private void StartSlide()
    {
        if (!Theme.Animations || !_haveDrawn)
            return;
        _from = _drawn;
        _slideStart = Stopwatch.GetTimestamp();
        _sliding = true;
    }

    protected override void OnFrame()
    {
        var now = Stopwatch.GetTimestamp();
        if (_sliding && Stopwatch.GetElapsedTime(_slideStart, now) >= SlideLength)
            _sliding = false;
        if (_shaking && Stopwatch.GetElapsedTime(_shakeStart, now) >= ShakeLength)
            _shaking = false;
        Invalidate();
    }

    protected override void OnHidden()
    {
        // A hide with a menu still open (a theme crash) must free the keys too.
        Teardown();
        _request = null;
    }

    // ---- drawing ----

    protected override void Render(ID2D1RenderTarget* target, ID2D1SolidColorBrush* solid)
    {
        if (_request is not { } request || _model is not { } model || _title is null)
            return;
        var brush = (ID2D1Brush*)solid;
        var theme = Theme;
        var grid = request.Layout == MenuLayout.Grid;

        // Title row.
        SetColour(theme.Text);
        DrawCentred(target, brush, _title, PaddingDip, PaddingDip, _titleRow);
        SetColour(theme.Secondary);
        DrawCentred(target, brush, _count, _width - PaddingDip - _countWidth, PaddingDip, _titleRow);

        // Body, clipped to the viewport.
        var clip = new D2D_RECT_F { left = 0, top = _bodyTop, right = _width, bottom = _bodyTop + _viewport };
        target->PushAxisAlignedClip(&clip, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
        var offset = _bodyTop - _scroll;

        var focus = ItemRect(model.Focus);
        var now = Stopwatch.GetTimestamp();
        var shake = 0f;
        if (_shaking)
        {
            var t = (float)(Stopwatch.GetElapsedTime(_shakeStart, now) / ShakeLength);
            shake = (float)(Math.Sin(t * Math.PI * 6) * 4 * (1 - t));
        }
        var rect = focus;
        if (_sliding && _haveDrawn)
        {
            var t = (float)Math.Clamp(Stopwatch.GetElapsedTime(_slideStart, now) / SlideLength, 0, 1);
            var eased = 1 - (1 - t) * (1 - t) * (1 - t);
            rect = new D2D_RECT_F
            {
                left = _from.left + (focus.left - _from.left) * eased, top = _from.top + (focus.top - _from.top) * eased,
                right = _from.right + (focus.right - _from.right) * eased, bottom = _from.bottom + (focus.bottom - _from.bottom) * eased,
            };
        }
        _drawn = rect;
        _haveDrawn = true;

        var fill = new D2D1_ROUNDED_RECT
        {
            rect = new D2D_RECT_F { left = rect.left + shake, top = rect.top + offset, right = rect.right + shake, bottom = rect.bottom + offset },
            radiusX = 6, radiusY = 6,
        };
        SetColour(theme.HighContrast ? theme.Highlight : theme.Accent);
        target->FillRoundedRectangle(&fill, brush);
        DrawRing(target, fill, theme);

        var glyphBox = _large ? 48f : 40f;
        for (var i = 0; i < model.Items.Count; i++)
        {
            var r = ItemRect(i);
            if (r.bottom - _scroll < 0 || r.top - _scroll > _viewport)
                continue;
            var focused = i == model.Focus;
            var item = model.Items[i];
            var dx = focused ? shake : 0;
            var colour = focused ? theme.OnAccent : item.CanRun ? theme.Text : theme.Secondary;
            var captionColour = focused ? theme.OnAccent : theme.Secondary;
            ref var layout = ref _layouts.Items[i];
            var top = r.top + offset;
            if (grid)
            {
                SetColour(colour);
                var glyphTop = top + 12;
                target->DrawTextLayout(new D2D_POINT_2F { x = r.left + (_tileWidth - glyphBox) / 2 + dx, y = glyphTop }, layout.Glyph, brush,
                    D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
                var nameTop = glyphTop + glyphBox + 4;
                target->DrawTextLayout(new D2D_POINT_2F { x = r.left + 8 + dx, y = nameTop }, layout.Name, brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_CLIP);
                if (layout.Caption is not null)
                {
                    SetColour(captionColour);
                    DWRITE_TEXT_METRICS metrics;
                    layout.Name->GetMetrics(&metrics);
                    target->DrawTextLayout(new D2D_POINT_2F { x = r.left + 8 + dx, y = nameTop + metrics.height + 2 }, layout.Caption, brush,
                        D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_CLIP);
                }
            }
            else
            {
                SetColour(colour);
                target->DrawTextLayout(new D2D_POINT_2F { x = r.left + 8 + dx, y = top + (_rowHeight - glyphBox) / 2 }, layout.Glyph, brush,
                    D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
                DrawCentred(target, brush, layout.Name, r.left + 8 + glyphBox + 12 + dx, top, _rowHeight);
                if (layout.Caption is not null)
                {
                    SetColour(captionColour);
                    DWRITE_TEXT_METRICS metrics;
                    layout.Caption->GetMetrics(&metrics);
                    DrawCentred(target, brush, layout.Caption, r.right - 12 - metrics.widthIncludingTrailingWhitespace + dx, top, _rowHeight);
                }
            }
        }

        if (_emptyHead is not null)
        {
            var itemsHeight = _content - _emptyHeight;
            var y = itemsHeight + SpacingDip + offset;
            SetColour(theme.Text);
            target->DrawTextLayout(new D2D_POINT_2F { x = PaddingDip, y = y }, _emptyHead, brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
            DWRITE_TEXT_METRICS metrics;
            _emptyHead->GetMetrics(&metrics);
            SetColour(theme.Secondary);
            target->DrawTextLayout(new D2D_POINT_2F { x = PaddingDip, y = y + metrics.height + 2 }, _emptyBody, brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
        }
        target->PopAxisAlignedClip();

        DrawHint(target, brush, model, theme);
    }

    private void DrawRing(ID2D1RenderTarget* target, D2D1_ROUNDED_RECT fill, PopupTheme theme)
    {
        // Two-tone system focus visual outside the fill; a contrast theme gets one 3-DIP ring (DESIGN §4.2).
        var outer = fill;
        var width = theme.HighContrast ? 3f : 2f;
        Inflate(ref outer.rect, width / 2 + 1);
        outer.radiusX = outer.radiusY = 7;
        SetColour(theme.HighContrast ? theme.Text : theme.Light ? Rgb(0) : Rgb(1));
        target->DrawRoundedRectangle(&outer, Brush, width, null);
        if (theme.HighContrast)
            return;
        var inner = fill;
        Inflate(ref inner.rect, 0.5f);
        SetColour(theme.Light ? Rgb(1) : Rgb(0));
        target->DrawRoundedRectangle(&inner, Brush, 1, null);
    }

    private static D2D1_COLOR_F Rgb(float v) => new() { r = v, g = v, b = v, a = 1 };

    private static void Inflate(ref D2D_RECT_F rect, float by)
    {
        rect.left -= by;
        rect.top -= by;
        rect.right += by;
        rect.bottom += by;
    }

    /// <summary>The hint row follows the input used last (#30); the last 5 s show "Closing in N s" instead (#31).</summary>
    private void DrawHint(ID2D1RenderTarget* target, ID2D1Brush* brush, MenuModel model, PopupTheme theme)
    {
        var y = _bodyTop + _viewport + SpacingDip;
        var remaining = model.Remaining;
        SetColour(theme.Secondary);
        if (remaining <= ClosingShows && remaining > TimeSpan.Zero)
        {
            var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
            var closing = Text($"Closing in {seconds} s", (_large ? 14f : 12f) * theme.TextScale, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, _width - 2 * PaddingDip, wrap: false);
            try
            {
                DrawCentred(target, brush, closing, PaddingDip, y, _hintHeight);
            }
            finally
            {
                closing->Release();
            }
            return;
        }
        switch (model.LastInput)
        {
            case MenuInput.Mouse:
                return;
            case MenuInput.Pad:
                var x = PaddingDip;
                x = DrawBadge(target, brush, _badgeA, x, y, theme) + 6;
                DrawCentred(target, brush, _hintRun, x, y, _hintHeight);
                DWRITE_TEXT_METRICS metrics;
                _hintRun->GetMetrics(&metrics);
                x += metrics.widthIncludingTrailingWhitespace + 16;
                x = DrawBadge(target, brush, _badgeB, x, y, theme) + 6;
                SetColour(theme.Secondary);
                DrawCentred(target, brush, _hintClose, x, y, _hintHeight);
                return;
            default:
                DrawCentred(target, brush, _hintKeys, PaddingDip, y, _hintHeight);
                return;
        }
    }

    /// <summary>A circled letter, drawn (not a font glyph); returns its right edge.</summary>
    private float DrawBadge(ID2D1RenderTarget* target, ID2D1Brush* brush, IDWriteTextLayout* letter, float x, float y, PopupTheme theme)
    {
        const float radius = 9;
        var centre = new D2D_POINT_2F { x = x + radius, y = y + _hintHeight / 2 };
        var ellipse = new D2D1_ELLIPSE { point = centre, radiusX = radius, radiusY = radius };
        SetColour(theme.Secondary);
        target->DrawEllipse(&ellipse, brush, 1, null);
        DWRITE_TEXT_METRICS metrics;
        letter->GetMetrics(&metrics);
        target->DrawTextLayout(new D2D_POINT_2F { x = x + radius - 10, y = centre.y - metrics.height / 2 }, letter, brush,
            D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
        return x + 2 * radius;
    }

    private static void DrawCentred(ID2D1RenderTarget* target, ID2D1Brush* brush, IDWriteTextLayout* layout, float x, float y, float row)
    {
        DWRITE_TEXT_METRICS metrics;
        layout->GetMetrics(&metrics);
        target->DrawTextLayout(new D2D_POINT_2F { x = x, y = y + (row - metrics.height) / 2 }, layout, brush,
            D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_CLIP);
    }

    protected override void ReleaseLayouts()
    {
        foreach (var item in _layouts.Items)
        {
            var glyph = item.Glyph;
            var name = item.Name;
            var caption = item.Caption;
            Release(ref glyph);
            Release(ref name);
            Release(ref caption);
        }
        _layouts = new Layouts();
        Release(ref _title);
        Release(ref _count);
        Release(ref _emptyHead);
        Release(ref _emptyBody);
        Release(ref _hintRun);
        Release(ref _hintClose);
        Release(ref _hintKeys);
        Release(ref _badgeA);
        Release(ref _badgeB);
    }

    public override void Dispose()
    {
        Teardown();
        _keys.Dispose();
        _mouse.Dispose();
        base.Dispose();
        s_exists = false;
    }
}
