namespace DazeControl.Core.Input;

/// <summary>Xbox buttons, one bit each (D-057, D-086).</summary>
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
