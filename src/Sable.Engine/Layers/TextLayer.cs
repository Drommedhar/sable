using Sable.Imaging;

namespace Sable.Engine.Layers;

public enum TextAlign { Left, Center, Right }

/// <summary>
/// A parametric text layer (PLAN §5, v1 = rasterized but re-editable). Stores the
/// string + font size + colour + position; the compositor rasterizes it on demand
/// (SkiaSharp), so the text stays editable and the layer's bounds are the text itself.
/// </summary>
public sealed class TextLayer : Layer
{
    public string Text { get; set; }
    public float FontSize { get; set; }
    public byte R, G, B;
    public float X, Y;        // top-left of the text block in doc px
    public string FontFamily { get; set; } = "";   // "" = system default
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strikethrough { get; set; }
    public TextAlign Align { get; set; } = TextAlign.Left;
    public float LineSpacing { get; set; } = 1f;    // multiplier on line height
    public float BoxWidth { get; set; }             // 0 = point text (auto-size); >0 = area text wrapped to this width
    public float Tracking { get; set; }             // extra letter-spacing in px

    /// <summary>When non-empty, glyphs flow along this doc-px polyline (text-on-path). Baked from a path.</summary>
    public List<(float X, float Y)> PathPoints { get; set; } = new();
    public bool OnPath => PathPoints.Count >= 2;

    private int _rw, _rh;     // last rendered bitmap size (for ContentBounds)
    public int RenderedW => _rw;
    public int RenderedH => _rh;

    // caret position relative to (X,Y): end of the last line
    public float CaretX { get; private set; }
    public float CaretY { get; private set; }
    public float CaretH { get; private set; }

    public TextLayer(string text, float x, float y, float fontSize, byte r, byte g, byte b)
    {
        Text = text; X = x; Y = y; FontSize = fontSize; R = r; G = g; B = b;
        Name = text;
    }

    protected override Layer CreateClone() => new TextLayer(Text, X, Y, FontSize, R, G, B)
    {
        FontFamily = FontFamily, Bold = Bold, Italic = Italic, Underline = Underline,
        Strikethrough = Strikethrough, Align = Align, LineSpacing = LineSpacing,
        BoxWidth = BoxWidth, Tracking = Tracking,
        PathPoints = new List<(float, float)>(PathPoints),
    };

    /// <summary>Convert this text to an editable vector path (text→curves, PLAN §16.10). Glyph
    /// outlines become bézier contours (first = primary sub-path, rest = counters/holes), filled
    /// in the text colour. Returns a path with no nodes if there is nothing to convert.</summary>
    public PathLayer ToPath()
    {
        var contours = TextRaster_Glyphs();
        var path = new PathLayer
        {
            Name = string.IsNullOrWhiteSpace(Text) ? "Path" : Text,
            Closed = true, Filled = true, FillR = R, FillG = G, FillB = B, FillA = 255,
        };
        for (int ci = 0; ci < contours.Count; ci++)
        {
            var nodes = new List<PathNode>(contours[ci].Nodes.Count);
            foreach (var on in contours[ci].Nodes)
                nodes.Add(new PathNode
                {
                    Ax = on.Ax + X, Ay = on.Ay + Y, InX = on.Ix + X, InY = on.Iy + Y,
                    OutX = on.Ox + X, OutY = on.Oy + Y, Smooth = false,
                });
            if (ci == 0) { path.Nodes = nodes; path.Closed = contours[0].Closed; }
            else path.ExtraContours.Add((nodes, contours[ci].Closed));
        }
        return path;
    }

    private List<Sable.Imaging.OutlineContour> TextRaster_Glyphs()
        => Sable.Imaging.TextOutline.Glyphs(Text, FontSize, FontFamily, Bold, Italic, (int)Align, LineSpacing, BoxWidth, Tracking);

    public override (int x, int y, int w, int h) ContentBounds(int docW, int docH)
    {
        if (OnPath)
        {
            float minx = float.MaxValue, miny = float.MaxValue, maxx = float.MinValue, maxy = float.MinValue;
            foreach (var (px, py) in PathPoints) { minx = System.Math.Min(minx, px); miny = System.Math.Min(miny, py); maxx = System.Math.Max(maxx, px); maxy = System.Math.Max(maxy, py); }
            int pad = (int)FontSize + 2;
            return ((int)minx - pad, (int)miny - pad, System.Math.Max(1, (int)(maxx - minx) + pad * 2), System.Math.Max(1, (int)(maxy - miny) + pad * 2));
        }
        return ((int)X, (int)Y, System.Math.Max(1, _rw), System.Math.Max(1, _rh));
    }

    /// <summary>Render the text and blit it into a doc-sized RGBA8 buffer. Clears first.</summary>
    public void Rasterize(byte[] dst, int dw, int dh)
    {
        System.Array.Clear(dst);
        int align = (int)Align;
        int tw, th, ox, oy;
        byte[] rgba;
        if (OnPath)
        {
            var (pw, ph, prgba, pox, poy) = TextRaster.RenderOnPath(Text, FontSize, R, G, B, FontFamily, Bold, Italic, Tracking, PathPoints);
            tw = pw; th = ph; rgba = prgba; ox = pox; oy = poy;
            _rw = tw; _rh = th;
            CaretX = 0; CaretY = 0; CaretH = FontSize;
        }
        else
        {
            var (rw, rh, rrgba) = TextRaster.Render(Text, FontSize, R, G, B, FontFamily, Bold, Italic,
                Underline, Strikethrough, align, LineSpacing, BoxWidth, Tracking);
            tw = rw; th = rh; rgba = rrgba;
            _rw = tw; _rh = th;
            var (cx, cy, ch) = TextRaster.CaretOffset(Text, FontSize, FontFamily, Bold, Italic, align, LineSpacing, BoxWidth, Tracking);
            CaretX = cx; CaretY = cy; CaretH = ch;
            ox = (int)System.Math.Round(X); oy = (int)System.Math.Round(Y);
        }
        for (int y = 0; y < th; y++)
        {
            int dyp = oy + y;
            if (dyp < 0 || dyp >= dh) continue;
            for (int x = 0; x < tw; x++)
            {
                int dxp = ox + x;
                if (dxp < 0 || dxp >= dw) continue;
                int si = (y * tw + x) * 4, di = (dyp * dw + dxp) * 4;
                if (rgba[si + 3] == 0) continue;
                dst[di] = rgba[si]; dst[di + 1] = rgba[si + 1]; dst[di + 2] = rgba[si + 2]; dst[di + 3] = rgba[si + 3];
            }
        }
    }
}
