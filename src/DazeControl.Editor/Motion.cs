using System.Numerics;
using DazeControl.Editor.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace DazeControl.Editor;

/// <summary>Shared motion for the configuration pages (D-097): put <c>local:Motion.Animate="True"</c> on a panel
/// and its children slide when neighbours appear/disappear and fade in when shown; <c>Motion.AnimateItems</c> does the
/// same for an ItemsControl's rows. Nothing runs while idle: the transitions are one-shot and the Windows
/// "Animation effects" setting is read when a panel loads (a new <see cref="UISettings"/> each time, no listener).</summary>
public static class Motion
{
    public static readonly DependencyProperty AnimateProperty =
        DependencyProperty.RegisterAttached("Animate", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnAnimateChanged));

    public static readonly DependencyProperty AnimateItemsProperty =
        DependencyProperty.RegisterAttached("AnimateItems", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnAnimateItemsChanged));

    public static bool GetAnimate(DependencyObject d) => (bool)d.GetValue(AnimateProperty);

    public static void SetAnimate(DependencyObject d, bool value) => d.SetValue(AnimateProperty, value);

    public static bool GetAnimateItems(DependencyObject d) => (bool)d.GetValue(AnimateItemsProperty);

    public static void SetAnimateItems(DependencyObject d, bool value) => d.SetValue(AnimateItemsProperty, value);

    /// <summary>The Windows "Animation effects" switch; animations are off when it is, or when it cannot be read.</summary>
    public static bool Enabled
    {
        get
        {
            try { return new UISettings().AnimationsEnabled; }
            catch { return false; }
        }
    }

    private static void OnAnimateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel panel || e.NewValue is not true)
            return;
        panel.Loaded -= OnPanelLoaded;
        panel.Loaded += OnPanelLoaded;
    }

    private static void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        var panel = (Panel)sender;
        panel.Loaded -= OnPanelLoaded;
        if (!Enabled)
            return;
        panel.ChildrenTransitions = [new RepositionThemeTransition { IsStaggeringEnabled = false }];
        foreach (var child in panel.Children)
            ApplyShowFade(child);
    }

    private static void OnAnimateItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl items || e.NewValue is not true)
            return;
        items.Loaded -= OnItemsLoaded;
        items.Loaded += OnItemsLoaded;
    }

    private static void OnItemsLoaded(object sender, RoutedEventArgs e)
    {
        var items = (ItemsControl)sender;
        items.Loaded -= OnItemsLoaded;
        if (Enabled)
            items.ItemContainerTransitions = [new AddDeleteThemeTransition(), new RepositionThemeTransition { IsStaggeringEnabled = false }];
    }

    /// <summary>A fade-in whenever <paramref name="element"/> turns visible; it ends on the element's own opacity.</summary>
    private static void ApplyShowFade(UIElement element)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.Duration = MotionTokens.Duration(true, MotionTokens.FadeMs);
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertExpressionKeyFrame(1f, "this.FinalValue");
        ElementCompositionPreview.SetImplicitShowAnimation(element, fade);
    }

    /// <summary>Fades a pane that now shows other content (another step selected); opacity only, so nothing moves.</summary>
    public static void Swap(UIElement element)
    {
        if (!Enabled)
            return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = MotionTokens.Duration(true, MotionTokens.FadeMs);
        fade.InsertKeyFrame(0f, MotionTokens.SwapStartOpacity);
        fade.InsertKeyFrame(1f, 1f);
        visual.StartAnimation("Opacity", fade);
    }
}
