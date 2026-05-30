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
    };

    public override (int x, int y, int w, int h) ContentBounds(int docW, int docH)
        => ((int)X, (int)Y, System.Math.Max(1, _rw), System.Math.Max(1, _rh));

    /// <summary>Render the text and blit it into a doc-sized RGBA8 buffer at (X,Y). Clears first.</summary>
    public void Rasterize(byte[] dst, int dw, int dh)
    {
        System.Array.Clear(dst);
        int align = (int)Align;
        var (tw, th, rgba) = TextRaster.Render(Text, FontSize, R, G, B, FontFamily, Bold, Italic,
            Underline, Strikethrough, align, LineSpacing);
        _rw = tw; _rh = th;
        var (cx, cy, ch) = TextRaster.CaretOffset(Text, FontSize, FontFamily, Bold, Italic, align, LineSpacing);
        CaretX = cx; CaretY = cy; CaretH = ch;
        int ox = (int)System.Math.Round(X), oy = (int)System.Math.Round(Y);
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
