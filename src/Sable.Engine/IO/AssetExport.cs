using System.Collections.Generic;
using System.Text;

namespace Sable.Engine.IO;

/// <summary>One output size for a batch asset export: a filename suffix + a scale percentage
/// (e.g. <c>("@2x", 200)</c>). 100 = original size, no suffix.</summary>
public readonly record struct ScaleVariant(string Suffix, int Percent);

/// <summary>
/// Pure helpers for batch asset export (ROADMAP P3): filename sanitising, duplicate-name
/// disambiguation, and tight alpha-bbox cropping. GPU rendering + file IO live in the app
/// (MainWindow); everything here is headless-testable.
/// </summary>
public static class AssetExport
{
    private const string Invalid = "<>:\"/\\|?*";

    /// <summary>Strip path-invalid / control chars from a layer name; never empty.</summary>
    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "asset";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
            sb.Append(char.IsControl(ch) || Invalid.IndexOf(ch) >= 0 ? '_' : ch);
        // Windows disallows a trailing dot or space on a file name component.
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        return s.Length == 0 ? "asset" : s;
    }

    /// <summary>Build a file name: sanitised base + scale suffix + extension.</summary>
    public static string BuildFileName(string? baseName, string suffix, string ext)
        => $"{SanitizeName(baseName)}{suffix}.{ext}";

    /// <summary>De-duplicate a list of file names (case-insensitive): the 2nd+ collision gets
    /// <c>-2</c>, <c>-3</c>… inserted before the extension. Order preserved.</summary>
    public static List<string> UniqueNames(IReadOnlyList<string> names)
    {
        var seen = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(names.Count);
        foreach (var n in names)
        {
            if (!seen.TryGetValue(n, out var count))
            {
                seen[n] = 1;
                result.Add(n);
            }
            else
            {
                int dot = n.LastIndexOf('.');
                string stem = dot < 0 ? n : n.Substring(0, dot);
                string ext = dot < 0 ? "" : n.Substring(dot);
                string candidate;
                do { count++; candidate = $"{stem}-{count}{ext}"; }
                while (seen.ContainsKey(candidate));
                seen[n] = count;
                seen[candidate] = 1;
                result.Add(candidate);
            }
        }
        return result;
    }

    /// <summary>Tight bounding box of non-transparent pixels in an RGBA8 buffer. Returns false
    /// (and zero rect) when the buffer is fully transparent.</summary>
    public static bool AlphaBounds(byte[] rgba, int w, int h, out int x, out int y, out int bw, out int bh)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
                if (rgba[(yy * w + xx) * 4 + 3] != 0)
                {
                    if (xx < minX) minX = xx;
                    if (xx > maxX) maxX = xx;
                    if (yy < minY) minY = yy;
                    if (yy > maxY) maxY = yy;
                }
        if (maxX < 0) { x = y = bw = bh = 0; return false; }
        x = minX; y = minY; bw = maxX - minX + 1; bh = maxY - minY + 1;
        return true;
    }

    /// <summary>Copy a sub-rectangle out of an RGBA8 buffer into a tightly-packed new buffer.</summary>
    public static byte[] Crop(byte[] rgba, int w, int h, int x, int y, int cw, int ch)
    {
        var outp = new byte[cw * ch * 4];
        for (int yy = 0; yy < ch; yy++)
            System.Array.Copy(rgba, ((y + yy) * w + x) * 4, outp, yy * cw * 4, cw * 4);
        return outp;
    }
}
