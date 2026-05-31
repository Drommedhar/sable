using SkiaSharp;

namespace Sable.Imaging;

/// <summary>
/// Rasterize a text string to an RGBA8 bitmap via SkiaSharp (PLAN §5/§16.10 Text). Supports
/// point text (auto-size), area/frame text (word-wrap to a fixed width), letter-spacing
/// (tracking), and text-on-a-path. Straight-alpha RGBA8 (byte order R,G,B,A); the caller blits
/// the returned bitmap into the document.
/// </summary>
public static class TextRaster
{
    /// <summary>Installed font family names (sorted) for the Type tool's font picker.</summary>
    public static string[] Families()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        using var mgr = SKFontManager.CreateDefault();
        foreach (var f in mgr.FontFamilies) if (!string.IsNullOrWhiteSpace(f)) set.Add(f);
        return set.Count > 0 ? set.ToArray() : new[] { "Default" };
    }

    internal static SKFont MakeFont(string family, float fontSize, bool bold, bool italic)
    {
        var weight = bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var slant = italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        var tf = (string.IsNullOrEmpty(family)
            ? SKTypeface.FromFamilyName(null, weight, SKFontStyleWidth.Normal, slant)
            : SKTypeface.FromFamilyName(family, weight, SKFontStyleWidth.Normal, slant)) ?? SKTypeface.Default;
        return new SKFont(tf, fontSize);
    }

    private static float LineStartX(int align, float maxW, float lineW)
        => align == 1 ? (maxW - lineW) * 0.5f + 2 : align == 2 ? (maxW - lineW) + 2 : 2;

    /// <summary>Width of a rendered line including tracking between glyphs.</summary>
    private static float LineWidth(SKFont font, string line, float tracking)
    {
        if (line.Length == 0) return 0;
        return font.MeasureText(line) + tracking * (line.Length - 1);
    }

    /// <summary>
    /// Build the display lines: hard '\n' breaks plus (when <paramref name="maxWidth"/> &gt; 0)
    /// greedy word-wrap to that width. Returns the wrapped lines.
    /// </summary>
    internal static List<string> LayoutLines(SKFont font, string text, float maxWidth, float tracking)
    {
        var outLines = new List<string>();
        foreach (var para in (text ?? "").Split('\n'))
        {
            if (maxWidth <= 0) { outLines.Add(para); continue; }
            if (para.Length == 0) { outLines.Add(""); continue; }
            var words = para.Split(' ');
            string cur = "";
            foreach (var w in words)
            {
                string cand = cur.Length == 0 ? w : cur + " " + w;
                if (LineWidth(font, cand, tracking) <= maxWidth || cur.Length == 0)
                    cur = cand;
                else { outLines.Add(cur); cur = w; }
            }
            outLines.Add(cur);
        }
        if (outLines.Count == 0) outLines.Add("");
        return outLines;
    }

    private static void DrawLine(SKCanvas canvas, SKFont font, SKPaint paint, string line, float x, float by, float tracking)
    {
        if (line.Length == 0) return;
        if (tracking == 0) { canvas.DrawText(line, x, by, SKTextAlign.Left, font, paint); return; }
        float cx = x;
        foreach (var ch in line)
        {
            string s = ch.ToString();
            canvas.DrawText(s, cx, by, SKTextAlign.Left, font, paint);
            cx += font.MeasureText(s) + tracking;
        }
    }

    /// <summary>Caret position relative to the text top-left: end of the last (wrapped) line.</summary>
    public static (float x, float y, float lineH) CaretOffset(string text, float fontSize,
        string family = "", bool bold = false, bool italic = false, int align = 0, float lineSpacing = 1f,
        float maxWidth = 0, float tracking = 0)
    {
        using var font = MakeFont(family, fontSize, bold, italic);
        float lineH = (font.Metrics.Descent - font.Metrics.Ascent) * Math.Max(0.1f, lineSpacing);
        var lines = LayoutLines(font, text, maxWidth, tracking);
        float maxW = maxWidth > 0 ? maxWidth : 0;
        if (maxW <= 0) foreach (var ln in lines) maxW = Math.Max(maxW, LineWidth(font, ln, tracking));
        float lastW = lines.Count > 0 ? LineWidth(font, lines[^1], tracking) : 0;
        return (LineStartX(align, maxW, lastW) + lastW, (lines.Count - 1) * lineH, lineH);
    }

    public static (int w, int h, byte[] rgba) Render(string text, float fontSize, byte r, byte g, byte b,
        string family = "", bool bold = false, bool italic = false,
        bool underline = false, bool strikethrough = false, int align = 0, float lineSpacing = 1f,
        float maxWidth = 0, float tracking = 0)
    {
        if (string.IsNullOrEmpty(text) || fontSize < 1) return (1, 1, new byte[4]);

        using var font = MakeFont(family, fontSize, bold, italic);
        using var paint = new SKPaint { Color = new SKColor(r, g, b, 255), IsAntialias = true };

        var m = font.Metrics;
        var lines = LayoutLines(font, text, maxWidth, tracking);
        float lineH = (m.Descent - m.Ascent) * Math.Max(0.1f, lineSpacing);
        var ws = new float[lines.Count];
        float contentW = 0;
        for (int i = 0; i < lines.Count; i++) { ws[i] = LineWidth(font, lines[i], tracking); contentW = Math.Max(contentW, ws[i]); }
        float maxW = maxWidth > 0 ? maxWidth : contentW;   // area text uses the fixed box width for alignment

        int w = Math.Max(1, (int)Math.Ceiling(maxW) + 4);
        int h = Math.Max(1, (int)Math.Ceiling(lineH * lines.Count) + 4);
        float baseline = -m.Ascent + 2;
        float ulPos = m.UnderlinePosition ?? (m.Descent * 0.5f);
        float ulThick = Math.Max(1f, m.UnderlineThickness ?? fontSize * 0.06f);
        float soPos = m.StrikeoutPosition ?? (m.Ascent * 0.35f);   // negative = above baseline

        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        for (int i = 0; i < lines.Count; i++)
        {
            float x = LineStartX(align, maxW, ws[i]);
            float by = baseline + i * lineH;
            DrawLine(canvas, font, paint, lines[i], x, by, tracking);
            if (ws[i] > 0 && underline) canvas.DrawRect(SKRect.Create(x, by + ulPos, ws[i], ulThick), paint);
            if (ws[i] > 0 && strikethrough) canvas.DrawRect(SKRect.Create(x, by + soPos, ws[i], ulThick), paint);
        }
        return (w, h, bmp.Bytes);
    }

    /// <summary>
    /// Render text flowed along a path (doc-px points). Returns the bitmap covering the affected
    /// area + the top-left doc offset to blit it at. Single line (newlines become spaces).
    /// </summary>
    public static (int w, int h, byte[] rgba, int ox, int oy) RenderOnPath(string text, float fontSize,
        byte r, byte g, byte b, string family, bool bold, bool italic, float tracking,
        IReadOnlyList<(float X, float Y)> path)
    {
        if (string.IsNullOrEmpty(text) || fontSize < 1 || path.Count < 2) return (1, 1, new byte[4], 0, 0);
        string line = text.Replace('\n', ' ');

        using var font = MakeFont(family, fontSize, bold, italic);
        using var paint = new SKPaint { Color = new SKColor(r, g, b, 255), IsAntialias = true };
        var m = font.Metrics;

        // cumulative arc length of the polyline
        var cum = new float[path.Count];
        for (int i = 1; i < path.Count; i++)
        {
            float dx = path[i].X - path[i - 1].X, dy = path[i].Y - path[i - 1].Y;
            cum[i] = cum[i - 1] + MathF.Sqrt(dx * dx + dy * dy);
        }
        float total = cum[^1];

        // path bbox + glyph extents → bitmap covering everything (padded by the font height)
        float minx = float.MaxValue, miny = float.MaxValue, maxx = float.MinValue, maxy = float.MinValue;
        foreach (var (px, py) in path) { minx = Math.Min(minx, px); miny = Math.Min(miny, py); maxx = Math.Max(maxx, px); maxy = Math.Max(maxy, py); }
        float pad = (m.Descent - m.Ascent) + 4;
        int ox = (int)Math.Floor(minx - pad), oy = (int)Math.Floor(miny - pad);
        int w = Math.Max(1, (int)Math.Ceiling(maxx - minx + pad * 2));
        int h = Math.Max(1, (int)Math.Ceiling(maxy - miny + pad * 2));

        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        float s = 0;
        foreach (var ch in line)
        {
            string cs = ch.ToString();
            float gw = font.MeasureText(cs);
            float mid = s + gw / 2;
            if (mid > total) break;                    // ran past the end of the path
            var (px, py, ang) = SampleAt(path, cum, mid);
            canvas.Save();
            canvas.Translate(px - ox, py - oy);
            canvas.RotateRadians(ang);
            if (cs != " ") canvas.DrawText(cs, -gw / 2, 0, SKTextAlign.Left, font, paint);
            canvas.Restore();
            s += gw + tracking;
        }
        return (w, h, bmp.Bytes, ox, oy);
    }

    /// <summary>Point + tangent angle (radians) at arc-length <paramref name="s"/> along the polyline.</summary>
    private static (float x, float y, float ang) SampleAt(IReadOnlyList<(float X, float Y)> path, float[] cum, float s)
    {
        int i = 1;
        while (i < cum.Length - 1 && cum[i] < s) i++;
        float segLen = cum[i] - cum[i - 1];
        float t = segLen > 1e-4f ? (s - cum[i - 1]) / segLen : 0;
        float ax = path[i - 1].X, ay = path[i - 1].Y, bx = path[i].X, by = path[i].Y;
        float x = ax + (bx - ax) * t, y = ay + (by - ay) * t;
        float ang = MathF.Atan2(by - ay, bx - ax);
        return (x, y, ang);
    }
}
