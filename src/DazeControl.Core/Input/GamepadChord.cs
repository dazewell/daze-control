using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace DazeControl.Core.Input;

/// <summary>Xbox buttons, one bit each in the order a chord is written (D-057).</summary>
[Flags]
public enum GamepadButtons : ushort
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    X = 1 << 2,
    Y = 1 << 3,
    LB = 1 << 4,
    RB = 1 << 5,
    LT = 1 << 6,
    RT = 1 << 7,
    View = 1 << 8,
    Menu = 1 << 9,
    LS = 1 << 10,
    RS = 1 << 11,
    Up = 1 << 12,
    Down = 1 << 13,
    Left = 1 << 14,
    Right = 1 << 15,
}

/// <summary>
/// A controller chord as written in <c>gamepad:</c> ("View+Menu", D-057): 2–4 buttons held together. The written order
/// doesn't matter; <see cref="ToString"/> uses the canonical one, so equality and clashes ignore it. No Guide (Game
/// Bar owns it) and no single button (a game would start the flow by accident).
/// </summary>
public sealed record GamepadChord(GamepadButtons Buttons)
{
    public const int MinButtons = 2, MaxButtons = 4;

    private static readonly GamepadButtons[] Order = [.. Enum.GetValues<GamepadButtons>().Where(b => b != GamepadButtons.None)];

    /// <summary>Case and spaces don't matter. False for unknown names (Guide too), repeats and fewer than 2 or more than 4 buttons.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out GamepadChord? chord) =>
        Parse(text, out chord) is null;

    /// <summary>Why <paramref name="text"/> isn't a chord, or null when it is.</summary>
    public static string? Explain(string? text) => Parse(text, out _);

    private static string? Parse(string? text, out GamepadChord? chord)
    {
        chord = null;
        if (string.IsNullOrWhiteSpace(text))
            return "Write the buttons joined by +, like View+Menu.";
        var buttons = GamepadButtons.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Guide", StringComparison.OrdinalIgnoreCase))
                return "Guide can't be used: Game Bar owns it and the controller doesn't report it.";
            if (!Order.Any(b => b.ToString().Equals(part, StringComparison.OrdinalIgnoreCase)))
                return $"\"{part}\" isn't a button (A B X Y LB RB LT RT View Menu LS RS Up Down Left Right).";
            var button = Order.First(b => b.ToString().Equals(part, StringComparison.OrdinalIgnoreCase));
            if ((buttons & button) != 0)
                return $"{button} is listed twice.";
            buttons |= button;
        }
        var count = BitOperations.PopCount((uint)buttons);
        if (count < MinButtons)
            return "Use at least two buttons together, so a game can't start the flow by accident.";
        if (count > MaxButtons)
            return $"Use at most {MaxButtons} buttons together.";
        chord = new(buttons);
        return null;
    }

    /// <summary>Canonical form: "LB+RB+Y" is written "Y+LB+RB".</summary>
    public override string ToString() => string.Join('+', Order.Where(b => (Buttons & b) != 0));

    /// <summary>As shown to people: "View + Menu" (trigger text "Controller · View + Menu").</summary>
    public string Display => string.Join(" + ", Order.Where(b => (Buttons & b) != 0));
}
