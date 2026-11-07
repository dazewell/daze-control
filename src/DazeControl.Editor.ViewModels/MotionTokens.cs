namespace DazeControl.Editor.ViewModels;

/// <summary>Shared motion tokens of the editor (D-097). Pure, so it is unit-testable; the view layer feeds in the
/// Windows "Animation effects" setting. Zero means "do not animate".</summary>
public static class MotionTokens
{
    /// <summary>Content appearing or a selection swap: a quick fade.</summary>
    public const int FadeMs = 120;

    /// <summary>Neighbours sliding to make room (reposition).</summary>
    public const int RepositionMs = 167;

    /// <summary>Opacity a swapped-in pane starts from.</summary>
    public const float SwapStartOpacity = 0.4f;

    public static TimeSpan Duration(bool animationsEnabled, int ms) =>
        animationsEnabled && ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
}
