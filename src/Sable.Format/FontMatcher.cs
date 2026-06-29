using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Sable.Format;

/// <summary>
/// Pure PostScript-font-name → installed-family matching (roadmap §8 / §3.4). A PSD stores fonts as
/// PostScript names ("OpenSans-Bold", "HelveticaNeue-Italic"); we match them loosely against the
/// alphanumeric-normalised installed family names. Extracted from <see cref="PsdReader.MapPsFont"/>
/// and the app's font-installed check so the ONE matching rule is shared and unit-tested — the
/// installed-family list is injected, so there is no SkiaSharp / font-system dependency here.
/// </summary>
public static class FontMatcher
{
    /// <summary>Keep letters+digits, lower-cased ("Open Sans" → "opensans").</summary>
    public static string Norm(string s)
        => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Bold/italic flags inferred from the PostScript name.</summary>
    public static (bool bold, bool italic) StyleFlags(string psName)
    {
        bool bold = psName.Contains("Bold", StringComparison.OrdinalIgnoreCase)
                 || psName.Contains("Black", StringComparison.OrdinalIgnoreCase)
                 || psName.Contains("Heavy", StringComparison.OrdinalIgnoreCase);
        bool italic = psName.Contains("Italic", StringComparison.OrdinalIgnoreCase)
                   || psName.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        return (bold, italic);
    }

    /// <summary>The longest installed family whose normalised name prefixes the (normalised) PS
    /// name, or null when none matches (the font is missing).</summary>
    public static string? MatchInstalled(string psName, IEnumerable<string> installedFamilies)
    {
        var n = Norm(psName);
        if (n.Length == 0) return null;
        string? best = null; int bestLen = 0;
        foreach (var fam in installedFamilies)
        {
            var nf = Norm(fam);
            if (nf.Length >= 3 && n.StartsWith(nf, StringComparison.Ordinal) && nf.Length > bestLen)
            { best = fam; bestLen = nf.Length; }
        }
        return best;
    }

    /// <summary>True when an installed family matches the PS name — or the name is unparseable
    /// (blank / punctuation only), in which case we don't cry wolf about a missing font.</summary>
    public static bool IsInstalled(string psName, IEnumerable<string> installedFamilies)
        => Norm(psName).Length == 0 || MatchInstalled(psName, installedFamilies) is not null;

    /// <summary>The family to render with: the matched installed family, else a human-readable
    /// camel-case split of the base name ("OpenSans-Bold" → "Open Sans"). <paramref name="installed"/>
    /// reports whether a real installed family was found (false = the renderer substitutes a default).</summary>
    public static string Resolve(string psName, IEnumerable<string> installedFamilies, out bool installed)
    {
        var match = MatchInstalled(psName, installedFamilies);
        if (match is not null) { installed = true; return match; }
        installed = false;
        return Humanize(psName);
    }

    /// <summary>"OpenSans-Bold" → "Open Sans": the base name before '-', camel-case split.</summary>
    public static string Humanize(string psName)
    {
        var baseName = psName.Split('-')[0];
        var sb = new StringBuilder(baseName.Length + 4);
        for (int i = 0; i < baseName.Length; i++)
        {
            if (i > 0 && char.IsUpper(baseName[i]) && char.IsLower(baseName[i - 1])) sb.Append(' ');
            sb.Append(baseName[i]);
        }
        return sb.ToString();
    }
}
