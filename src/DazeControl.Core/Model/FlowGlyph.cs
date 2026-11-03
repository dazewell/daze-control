using System.Globalization;

namespace DazeControl.Core.Model;

/// <summary>A flow's glyph as text in Segoe Fluent Icons: the editor's cards and the desktop shortcut's icon (D-049).</summary>
public static class FlowGlyph
{
    /// <summary>Segoe Fluent "Lightning bolt" when the flow names no glyph.</summary>
    public const string Default = "";

    /// <summary>The character for a code point written as hex text ("E702"); <see cref="Default"/> when it isn't one.</summary>
    public static string Decode(string? code) =>
        int.TryParse(code, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
        && value is >= 0x20 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
            ? char.ConvertFromUtf32(value)
            : Default;
}
