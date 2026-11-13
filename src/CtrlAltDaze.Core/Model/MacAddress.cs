namespace CtrlAltDaze.Core.Model;

/// <summary>
/// A hardware (MAC) address as people write it (T24.7, D-139). One place for the validator and the provider, so a MAC the
/// editor accepts is one the step sends to: normalise first (spaces at the ends), then classify.
/// </summary>
public static class MacAddress
{
    /// <summary>
    /// Reads <c>aa:bb:cc:dd:ee:ff</c>, <c>aa-bb-cc-dd-ee-ff</c>, <c>aabb.ccdd.eeff</c> or twelve bare hex digits (any case) into
    /// its six bytes. A mix of separators, a wrong group size or any other character is not a MAC.
    /// </summary>
    public static bool TryParse(string? text, out byte[] bytes)
    {
        bytes = [];
        var value = (text ?? "").Trim();
        char separator = value.Contains(':', StringComparison.Ordinal) ? ':' : value.Contains('-', StringComparison.Ordinal) ? '-' : value.Contains('.', StringComparison.Ordinal) ? '.' : '\0';
        var groups = separator == '\0' ? [value] : value.Split(separator);
        var size = separator == '.' ? 4 : separator == '\0' ? 12 : 2;
        if (groups.Length != 12 / size || groups.Any(g => g.Length != size || !g.All(Uri.IsHexDigit)))
            return false;
        bytes = Convert.FromHexString(string.Concat(groups));
        return true;
    }

    /// <summary>The usual way to write it: lower case, colons.</summary>
    public static string Format(byte[] bytes) => string.Join(':', bytes.Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
}
