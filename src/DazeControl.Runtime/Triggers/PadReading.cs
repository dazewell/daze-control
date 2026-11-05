using DazeControl.Core.Input;

namespace DazeControl.Runtime.Triggers;

/// <summary>One reading of one controller: buttons plus the left stick (−1…1, up and right positive).</summary>
public readonly record struct PadReading(GamepadButtons Buttons, float StickX = 0, float StickY = 0);
