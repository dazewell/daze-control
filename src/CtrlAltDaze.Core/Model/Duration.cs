using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CtrlAltDaze.Core.Model;

/// <summary>Durations as written in config.yaml: "500ms", "20s", "2m", "1h", or combined largest first: "1m30s".</summary>
public static class Duration
{
    private static readonly (string Unit, TimeSpan Size)[] Units =
    [
        ("h", TimeSpan.FromHours(1)),
        ("m", TimeSpan.FromMinutes(1)),
        ("s", TimeSpan.FromSeconds(1)),
        ("ms", TimeSpan.FromMilliseconds(1)),
    ];

    public static bool TryParse(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var span = (text ?? "").AsSpan().Trim();
        if (span.IsEmpty)
            return false;
        var lastUnit = -1;
        while (!span.IsEmpty)
        {
            var digits = 0;
            while (digits < span.Length && char.IsAsciiDigit(span[digits]))
                digits++;
            var letters = digits;
            while (letters < span.Length && char.IsAsciiLetter(span[letters]))
                letters++;
            if (digits == 0 || letters == digits
                || !long.TryParse(span[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
                return false;
            var unit = IndexOf(span[digits..letters]);
            if (unit <= lastUnit) // unknown unit, or units out of order / repeated
                return false;
            lastUnit = unit;
            try
            {
                duration += Units[unit].Size * amount;
            }
            catch (OverflowException)
            {
                return false;
            }
            span = span[letters..];
        }
        return true;
    }

    [return: NotNullIfNotNull(nameof(duration))]
    public static string? Format(TimeSpan? duration)
    {
        if (duration is not { } value)
            return null;
        var text = "";
        foreach (var (unit, size) in Units)
        {
            var amount = value.Ticks / size.Ticks;
            if (amount > 0)
                text += amount.ToString(CultureInfo.InvariantCulture) + unit;
            value -= size * amount;
        }
        return text.Length > 0 ? text : "0s"; // sub-millisecond remainders are dropped
    }

    private static int IndexOf(ReadOnlySpan<char> unit)
    {
        for (var i = 0; i < Units.Length; i++)
        {
            if (unit.Equals(Units[i].Unit, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
