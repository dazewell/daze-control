using System.Text.RegularExpressions;

namespace CtrlAltDaze.Core.Backup;

/// <summary>
/// Looks for secret-looking literals in config text (D-014 forbids them, DESIGN §2): an Authorization / Bearer value or a
/// password-like key with a literal value. Findings are warnings only; the backup is still written. Never echoes the value.
/// </summary>
public static partial class SecretScan
{
    public static IReadOnlyList<string> Find(string configText)
    {
        var found = new List<string>();
        var lines = configText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (Suspicious().IsMatch(lines[i]))
                found.Add($"Line {i + 1} looks like it holds a secret. Put it in Credentials instead.");
        return found;
    }

    [GeneratedRegex(@"(?i)(authorization\s*[:=]\s*[""']?\S{3,})|(bearer\s+[A-Za-z0-9._~+/=-]{8,})|((?<![A-Za-z0-9_])[""']?(password|passwd|secret|api[_-]?key|token)[""']?\s*:\s*[""']?[^\s""'#{][^\s""']*)")]
    private static partial Regex Suspicious();
}
