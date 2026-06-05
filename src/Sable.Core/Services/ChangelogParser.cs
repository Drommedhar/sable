using System.Text;
using System.Text.RegularExpressions;

namespace Sable.Core.Services;

/// <summary>One <c>### Section</c> (Added/Changed/Fixed/…) of a release, with its markdown body.</summary>
public sealed record ChangelogSection(string Name, string Markdown);

/// <summary>One release (<c>## heading</c>) and its parsed sections, in document order.</summary>
public sealed record ChangelogVersion(string Heading, IReadOnlyList<ChangelogSection> Sections);

/// <summary>
/// Parses the aggregated changelog markdown produced by <see cref="UpdateService.BuildChangelog"/>
/// (each version under a <c>## heading</c>, separated by <c>---</c>, with <c>### Added/Changed/Fixed</c>
/// subsections) into a structured tree so the UI can show one tab per section and one collapsible
/// expander per version. Pure + unit-testable.
/// </summary>
public static class ChangelogParser
{
    /// <summary>Bucket name for content that sits under a version heading but outside any <c>### Section</c>.</summary>
    public const string GeneralSection = "Notes";

    public static IReadOnlyList<ChangelogVersion> Parse(string? markdown)
    {
        var versions = new List<ChangelogVersion>();
        if (string.IsNullOrWhiteSpace(markdown)) return versions;

        string? heading = null;
        var sections = new List<(string Name, StringBuilder Body)>();
        (string Name, StringBuilder Body)? current = null;

        void Flush()
        {
            if (heading is null) return;
            var secs = sections
                .Select(s => new ChangelogSection(s.Name, s.Body.ToString().Trim()))
                .Where(s => s.Markdown.Length > 0)
                .ToList();
            versions.Add(new ChangelogVersion(heading, secs));
            sections = new List<(string, StringBuilder)>();
            current = null;
        }

        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.TrimStart();

            // version heading: "## x" (but NOT "### x")
            if (t.StartsWith("## ", StringComparison.Ordinal) && !t.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                heading = t[3..].Trim();
                continue;
            }
            if (heading is null) continue;   // preamble before the first version — skip

            // section heading: "### Added" etc.
            if (t.StartsWith("### ", StringComparison.Ordinal))
            {
                var sec = (t[4..].Trim(), new StringBuilder());
                sections.Add(sec);
                current = sec;
                continue;
            }

            if (t == "---") continue;                                  // version separator
            if (t.StartsWith("**Full Changelog**", StringComparison.Ordinal)) continue;  // GitHub auto-trailer

            if (current is null)   // content under the version but before any "### Section"
            {
                var sec = (GeneralSection, new StringBuilder());
                sections.Add(sec);
                current = sec;
            }
            current.Value.Body.AppendLine(line);
        }
        Flush();
        return versions;
    }

    /// <summary>
    /// Distinct section names across all versions, ordered Added → Changed → Fixed → (others, then Notes).
    /// Drives the tab order in the update window.
    /// </summary>
    public static IReadOnlyList<string> SectionOrder(IReadOnlyList<ChangelogVersion> versions)
    {
        string[] preferred = { "Added", "Changed", "Fixed" };
        int Rank(string name)
        {
            var i = Array.IndexOf(preferred, name);
            if (i >= 0) return i;
            return name == GeneralSection ? int.MaxValue : preferred.Length;   // Notes last
        }
        return versions
            .SelectMany(v => v.Sections.Select(s => s.Name))
            .Distinct()
            .OrderBy(Rank)
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    // --- section-body rendering helpers (pure, so the UI just maps the result to Avalonia controls) ---

    private static readonly Regex LinkRx = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);

    /// <summary>
    /// Splits a section's markdown into bullets. Wrapped continuation lines are joined back into their
    /// bullet; nested bullets (leading spaces) carry an <see cref="ChangelogBullet.Indent"/> level; a
    /// leading non-bullet paragraph comes back with <see cref="ChangelogBullet.IsBullet"/> = false.
    /// </summary>
    public static IReadOnlyList<ChangelogBullet> Bullets(string? sectionMarkdown)
    {
        var result = new List<ChangelogBullet>();
        if (string.IsNullOrWhiteSpace(sectionMarkdown)) return result;

        int indent = 0; bool isBullet = false; StringBuilder? text = null;

        void Flush()
        {
            if (text is null) return;
            var s = text.ToString().Trim();
            if (s.Length > 0) result.Add(new ChangelogBullet(indent, isBullet, s));
            text = null;
        }

        foreach (var line in sectionMarkdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }   // blank line ends a bullet

            var lead = line.Length - line.TrimStart(' ').Length;
            var trimmed = line.Trim();
            var bullet = trimmed.StartsWith("- ", StringComparison.Ordinal)
                      || trimmed.StartsWith("* ", StringComparison.Ordinal);
            if (bullet)
            {
                Flush();
                indent = Math.Min(lead / 2, 3);
                isBullet = true;
                text = new StringBuilder(trimmed[2..]);
            }
            else if (text is not null)
            {
                text.Append(' ').Append(trimmed);   // wrapped continuation of the current bullet
            }
            else
            {
                indent = 0; isBullet = false;
                text = new StringBuilder(trimmed);   // leading paragraph, no bullet glyph
            }
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Splits inline text into bold / non-bold runs (markdown <c>**bold**</c>), after flattening links
    /// (<c>[label](url)</c> → <c>label</c>) and dropping inline-code backticks. The UI renders each run
    /// as a <c>Run</c> with the matching weight.
    /// </summary>
    public static IReadOnlyList<ChangelogSpan> Spans(string? text)
    {
        var spans = new List<ChangelogSpan>();
        if (string.IsNullOrEmpty(text)) return spans;

        var clean = LinkRx.Replace(text, "$1").Replace("`", string.Empty);

        var sb = new StringBuilder();
        var bold = false;
        for (var i = 0; i < clean.Length; i++)
        {
            if (clean[i] == '*' && i + 1 < clean.Length && clean[i + 1] == '*')
            {
                if (sb.Length > 0) { spans.Add(new ChangelogSpan(sb.ToString(), bold)); sb.Clear(); }
                bold = !bold;
                i++;   // consume the second '*'
                continue;
            }
            sb.Append(clean[i]);
        }
        if (sb.Length > 0) spans.Add(new ChangelogSpan(sb.ToString(), bold));
        return spans;
    }
}

/// <summary>One changelog bullet (or leading paragraph) with its nesting level.</summary>
public sealed record ChangelogBullet(int Indent, bool IsBullet, string Text);

/// <summary>A run of inline text and whether it is bold (markdown <c>**…**</c>).</summary>
public sealed record ChangelogSpan(string Text, bool Bold);
