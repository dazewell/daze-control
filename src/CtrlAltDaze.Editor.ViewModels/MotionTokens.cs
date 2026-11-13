namespace CtrlAltDaze.Editor.ViewModels;

/// <summary>Shared motion tokens of the editor (D-097). Pure, so it is unit-testable; the view layer feeds in the
/// Windows "Animation effects" setting. Zero means "do not animate".</summary>
public static class MotionTokens
{
    /// <summary>Content appearing or a selection swap: a quick fade.</summary>
    public const int FadeMs = 120;

    /// <summary>Opacity a swapped-in pane starts from.</summary>
    public const float SwapStartOpacity = 0.4f;

    /// <summary>Canvas steps moved by layout (Arrange, insert, delete, undo) glide this long, decelerating (UX §5.3).</summary>
    public const int ReflowMs = 200;

    /// <summary>A step added to the canvas fades in and grows from <see cref="AppearScale"/> this long (UX §5.3).</summary>
    public const int AppearMs = 150;

    public const double AppearScale = 0.96;

    public static TimeSpan Duration(bool animationsEnabled, int ms) =>
        animationsEnabled && ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
}
