using SkiaSharp;

namespace Sable.Imaging;

/// <summary>One bézier node of a glyph outline: anchor + in/out handles (relative to the text top-left).</summary>
public readonly record struct OutlineNode(float Ax, float Ay, float Ix, float Iy, float Ox, float Oy);

/// <summary>One closed contour of a glyph outline (e.g. a letter's exterior, or a counter/hole).</summary>
public sealed class OutlineContour
{
    public List<OutlineNode> Nodes { get; } = new();
    public bool Closed { get; set; } = true;
}

/// <summary>
/// Convert laid-out text to bézier contours (PLAN §16.10 text→curves). Mirrors
/// <see cref="TextRaster"/>'s line layout (wrap/tracking/align) so the outlines line up with the
/// rasterised text, then walks each glyph's SKPath into cubic nodes. Coords are relative to the
/// text top-left (the engine offsets by the layer's X/Y). Neutral types so Imaging stays
/// independent of the engine.
/// </summary>
public static class TextOutline
{
    public static List<OutlineContour> Glyphs(string text, float fontSize, string family, bool bold, bool italic,
        int align, float lineSpacing, float maxWidth, float tracking)
    {
        var result = new List<OutlineContour>();
        if (string.IsNullOrEmpty(text) || fontSize < 1) return result;

        using var font = TextRaster.MakeFont(family, fontSize, bold, italic);
        var m = font.Metrics;
        var lines = TextRaster.LayoutLines(font, text, maxWidth, tracking);
        float lineH = (m.Descent - m.Ascent) * Math.Max(0.1f, lineSpacing);
        float baseline = -m.Ascent + 2;

        // content width for alignment
        float contentW = 0;
        var ws = new float[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            ws[i] = lines[i].Length == 0 ? 0 : font.MeasureText(lines[i]) + tracking * (lines[i].Length - 1);
            contentW = Math.Max(contentW, ws[i]);
        }
        float maxW = maxWidth > 0 ? maxWidth : contentW;

        using var full = new SKPath();
        for (int li = 0; li < lines.Count; li++)
        {
            string line = lines[li];
            float x = align == 1 ? (maxW - ws[li]) * 0.5f + 2 : align == 2 ? (maxW - ws[li]) + 2 : 2;
            float by = baseline + li * lineH;
            foreach (var ch in line)
            {
                string cs = ch.ToString();
                if (cs != " ")
                {
                    var glyphs = font.GetGlyphs(cs);
                    if (glyphs.Length > 0)
                    {
                        using var gp = font.GetGlyphPath(glyphs[0]);
                        if (gp is not null && !gp.IsEmpty)
                        {
                            gp.Transform(SKMatrix.CreateTranslation(x, by));
                            full.AddPath(gp);
                        }
                    }
                }
                x += font.MeasureText(cs) + tracking;
            }
        }

        ExtractContours(full, result);
        return result;
    }

    private static void ExtractContours(SKPath path, List<OutlineContour> result)
    {
        using var it = path.CreateIterator(false);
        var pts = new SKPoint[4];
        OutlineContour? cur = null;

        OutlineNode N(SKPoint a) => new(a.X, a.Y, a.X, a.Y, a.X, a.Y);
        void SetOut(OutlineContour c, float ox, float oy)
        {
            var n = c.Nodes[^1];
            c.Nodes[^1] = n with { Ox = ox, Oy = oy };
        }

        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    if (cur is { Nodes.Count: > 1 }) result.Add(cur);
                    cur = new OutlineContour();
                    cur.Nodes.Add(N(pts[0]));
                    break;
                case SKPathVerb.Line:
                    cur?.Nodes.Add(N(pts[1]));
                    break;
                case SKPathVerb.Quad:
                case SKPathVerb.Conic:   // approximate a conic by its quad control point (ignore weight)
                    if (cur is not null)
                    {
                        // elevate quad (p0,c,p1) to cubic handles
                        float c1x = pts[0].X + 2f / 3f * (pts[1].X - pts[0].X);
                        float c1y = pts[0].Y + 2f / 3f * (pts[1].Y - pts[0].Y);
                        float c2x = pts[2].X + 2f / 3f * (pts[1].X - pts[2].X);
                        float c2y = pts[2].Y + 2f / 3f * (pts[1].Y - pts[2].Y);
                        SetOut(cur, c1x, c1y);
                        cur.Nodes.Add(new OutlineNode(pts[2].X, pts[2].Y, c2x, c2y, pts[2].X, pts[2].Y));
                    }
                    break;
                case SKPathVerb.Cubic:
                    if (cur is not null)
                    {
                        SetOut(cur, pts[1].X, pts[1].Y);
                        cur.Nodes.Add(new OutlineNode(pts[3].X, pts[3].Y, pts[2].X, pts[2].Y, pts[3].X, pts[3].Y));
                    }
                    break;
                case SKPathVerb.Close:
                    if (cur is not null)
                    {
                        // drop a duplicate closing anchor (== first) but keep its incoming handle on the first node
                        if (cur.Nodes.Count > 1)
                        {
                            var first = cur.Nodes[0];
                            var last = cur.Nodes[^1];
                            if (Math.Abs(first.Ax - last.Ax) < 0.01f && Math.Abs(first.Ay - last.Ay) < 0.01f)
                            {
                                cur.Nodes[0] = first with { Ix = last.Ix, Iy = last.Iy };
                                cur.Nodes.RemoveAt(cur.Nodes.Count - 1);
                            }
                        }
                        cur.Closed = true;
                        result.Add(cur);
                        cur = null;
                    }
                    break;
            }
        }
        if (cur is { Nodes.Count: > 1 }) result.Add(cur);
    }
}
