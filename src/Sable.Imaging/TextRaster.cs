using SkiaSharp;

namespace Sable.Imaging;

/// <summary>
/// Rasterize a text string to an RGBA8 bitmap via SkiaSharp (PLAN §5 Text, v1 = rasterized).
/// Returns the tight-ish bitmap + its size; the caller blits it into the document at the
/// layer position. Straight-alpha RGBA8 (byte order R,G,B,A).
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

    private static SKFont MakeFont(string family, float fontSize, bool bold, bool italic)
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

    /// <summary>Caret position relative to the text top-left: end of the last line.</summary>
    public static (float x, float y, float lineH) CaretOffset(string text, float fontSize,
        string family = "", bool bold = false, bool italic = false, int align = 0, float lineSpacing = 1f)
    {
        using var font = MakeFont(family, fontSize, bold, italic);
        float lineH = (font.Metrics.Descent - font.Metrics.Ascent) * Math.Max(0.1f, lineSpacing);
        var lines = (text ?? "").Split('\n');
        float maxW = 0; foreach (var ln in lines) maxW = Math.Max(maxW, font.MeasureText(ln));
        float lastW = lines.Length > 0 ? font.MeasureText(lines[^1]) : 0;
        return (LineStartX(align, maxW, lastW) + lastW, (lines.Length - 1) * lineH, lineH);
    }

    public static (int w, int h, byte[] rgba) Render(string text, float fontSize, byte r, byte g, byte b,
        string family = "", bool bold = false, bool italic = false,
        bool underline = false, bool strikethrough = false, int align = 0, float lineSpacing = 1f)
    {
        if (string.IsNullOrEmpty(text) || fontSize < 1) return (1, 1, new byte[4]);

        using var font = MakeFont(family, fontSize, bold, italic);
        using var paint = new SKPaint { Color = new SKColor(r, g, b, 255), IsAntialias = true };

        var m = font.Metrics;
        var lines = text.Split('\n');
        float lineH = (m.Descent - m.Ascent) * Math.Max(0.1f, lineSpacing);
        var ws = new float[lines.Length];
        float maxW = 0;
        for (int i = 0; i < lines.Length; i++) { ws[i] = font.MeasureText(lines[i]); maxW = Math.Max(maxW, ws[i]); }

        int w = Math.Max(1, (int)Math.Ceiling(maxW) + 4);
        int h = Math.Max(1, (int)Math.Ceiling(lineH * lines.Length) + 4);
        float baseline = -m.Ascent + 2;
        float ulPos = m.UnderlinePosition ?? (m.Descent * 0.5f);
        float ulThick = Math.Max(1f, m.UnderlineThickness ?? fontSize * 0.06f);
        float soPos = m.StrikeoutPosition ?? (m.Ascent * 0.35f);   // negative = above baseline

        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        for (int i = 0; i < lines.Length; i++)
        {
            float x = LineStartX(align, maxW, ws[i]);
            float by = baseline + i * lineH;
            if (lines[i].Length > 0) canvas.DrawText(lines[i], x, by, SKTextAlign.Left, font, paint);
            if (ws[i] > 0 && underline) canvas.DrawRect(SKRect.Create(x, by + ulPos, ws[i], ulThick), paint);
            if (ws[i] > 0 && strikethrough) canvas.DrawRect(SKRect.Create(x, by + soPos, ws[i], ulThick), paint);
        }
        return (w, h, bmp.Bytes);
    }
}
