namespace Sable.Engine;

/// <summary>A normalized rectangular selection in document pixels.</summary>
public readonly record struct SelRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool Contains(int px, int py) => px >= X && py >= Y && px < X + W && py < Y + H;

    /// <summary>Build a normalized rect from two corner points (clamped to the document).</summary>
    public static SelRect FromCorners(double x0, double y0, double x1, double y1, int docW, int docH)
    {
        int ax = (int)Math.Round(Math.Min(x0, x1));
        int ay = (int)Math.Round(Math.Min(y0, y1));
        int bx = (int)Math.Round(Math.Max(x0, x1));
        int by = (int)Math.Round(Math.Max(y0, y1));
        ax = Math.Clamp(ax, 0, docW); ay = Math.Clamp(ay, 0, docH);
        bx = Math.Clamp(bx, 0, docW); by = Math.Clamp(by, 0, docH);
        return new SelRect(ax, ay, Math.Max(0, bx - ax), Math.Max(0, by - ay));
    }
}
