using System.Globalization;
using System.Text;

namespace DazeControl.Core.Model;

/// <summary>Flow ids made from flow names (D-075): what a new or renamed flow is called in YAML, logs and history.</summary>
public static class FlowIds
{
    public const int MaxLength = 40;

    /// <summary>When nothing of the name is left ("🎧", "!!!").</summary>
    public const string Fallback = "flow";

    /// <summary>
    /// "Connect Technics" → "connect-technics": diacritics dropped, letters and digits of any script lowercased, every
    /// other run one "-", at most <see cref="MaxLength"/> characters (cut at a "-"), <see cref="Fallback"/> when empty.
    /// </summary>
    public static string Slug(string? name)
    {
        var text = (name ?? "").Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder(text.Length);
        var dash = false;
        foreach (var c in text)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;
            if (char.IsLetterOrDigit(c))
            {
                if (dash && slug.Length > 0)
                    slug.Append('-');
                dash = false;
                slug.Append(char.ToLowerInvariant(c));
            }
            else
            {
                dash = true;
            }
        }
        var result = slug.ToString().Normalize(NormalizationForm.FormC);
        if (result.Length > MaxLength)
        {
            var cut = result.LastIndexOf('-', MaxLength);
            result = cut > 0 ? result[..cut] : result[..MaxLength];
        }
        return result.Length == 0 ? Fallback : result;
    }

    /// <summary><paramref name="slug"/>, or it with "-2", "-3"… when <paramref name="taken"/> has it.</summary>
    public static string Unique(string slug, IReadOnlySet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var id = slug;
        for (var n = 2; taken.Contains(id); n++)
            id = string.Create(CultureInfo.InvariantCulture, $"{slug}-{n}");
        return id;
    }
}
