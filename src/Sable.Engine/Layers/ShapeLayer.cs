namespace Sable.Engine.Layers;

public enum ShapeKind { Rectangle, Ellipse, Line }

/// <summary>
/// A parametric (vector-ish) shape as a first-class layer (PLAN §4): stores kind +
/// rectangle + fill colour + stroke, NO baked pixels. The compositor rasterizes it on
/// demand, so the fill/size stay editable after drawing and the layer's content bounds
/// are the shape itself (tight Move handles), Affinity-style.
/// </summary>
public sealed class ShapeLayer : Layer
{
    public ShapeKind Kind { get; }
    public float X, Y, W, H;                 // shape rect in document px
    public byte R, G, B, A = 255;            // fill colour
    public float StrokeWidth = 4f;           // line stroke width (Line kind)

    public ShapeLayer(ShapeKind kind, float x, float y, float w, float h, byte r, byte g, byte b)
    {
        Kind = kind;
        X = x; Y = y; W = w; H = h; R = r; G = g; B = b;
        Name = kind switch { ShapeKind.Ellipse => "Ellipse", ShapeKind.Line => "Line", _ => "Rectangle" };
    }

    /// <summary>Tight content bounds (doc px) — used for Move/Transform handles.</summary>
    public override (int x, int y, int w, int h) ContentBounds(int docW, int docH)
    {
        int pad = Kind == ShapeKind.Line ? (int)System.Math.Ceiling(StrokeWidth / 2) + 1 : 0;
        int x = (int)System.Math.Floor(System.Math.Min(X, X + W)) - pad;
        int y = (int)System.Math.Floor(System.Math.Min(Y, Y + H)) - pad;
        int w = (int)System.Math.Ceiling(System.Math.Abs(W)) + pad * 2;
        int h = (int)System.Math.Ceiling(System.Math.Abs(H)) + pad * 2;
        return (x, y, System.Math.Max(1, w), System.Math.Max(1, h));
    }

    /// <summary>Rasterize the shape into a doc-sized RGBA8 buffer (straight alpha). Clears first.</summary>
    public void Rasterize(byte[] dst, int dw, int dh)
    {
        System.Array.Clear(dst);
        double x0 = X, y0 = Y, x1 = X + W, y1 = Y + H;
        double minx = System.Math.Min(x0, x1), maxx = System.Math.Max(x0, x1);
        double miny = System.Math.Min(y0, y1), maxy = System.Math.Max(y0, y1);
        double halfW = System.Math.Max(0.5, StrokeWidth / 2);
        int pad = (int)System.Math.Ceiling(halfW) + 1;
        int lx = System.Math.Max(0, (int)System.Math.Floor(minx) - pad);
        int rx = System.Math.Min(dw - 1, (int)System.Math.Ceiling(maxx) + pad);
        int ty = System.Math.Max(0, (int)System.Math.Floor(miny) - pad);
        int by = System.Math.Min(dh - 1, (int)System.Math.Ceiling(maxy) + pad);
        double cx = (minx + maxx) / 2, cy = (miny + maxy) / 2;
        double erx = System.Math.Max(0.5, (maxx - minx) / 2), ery = System.Math.Max(0.5, (maxy - miny) / 2);

        for (int y = ty; y <= by; y++)
        for (int x = lx; x <= rx; x++)
        {
            double fx = x + 0.5, fy = y + 0.5;
            float cov = Kind switch
            {
                ShapeKind.Rectangle => (fx >= minx && fx <= maxx && fy >= miny && fy <= maxy) ? 1f : 0f,
                ShapeKind.Ellipse => EllipseCov(fx, fy, cx, cy, erx, ery),
                _ => LineCov(fx, fy, x0, y0, x1, y1, halfW)
            };
            if (cov <= 0f) continue;
            int i = (y * dw + x) * 4;
            dst[i] = R; dst[i + 1] = G; dst[i + 2] = B;
            dst[i + 3] = (byte)System.Math.Clamp(cov * (A / 255f) * 255f + 0.5f, 0, 255);
        }
    }

    private static float EllipseCov(double x, double y, double cx, double cy, double rx, double ry)
    {
        double nx = (x - cx) / rx, ny = (y - cy) / ry;
        double d = System.Math.Sqrt(nx * nx + ny * ny);
        double aa = 1.0 / System.Math.Min(rx, ry);
        return (float)System.Math.Clamp((1.0 - d) / System.Math.Max(1e-4, aa) + 0.5, 0, 1);
    }

    private static float LineCov(double x, double y, double x0, double y0, double x1, double y1, double halfW)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double len2 = dx * dx + dy * dy;
        double t = len2 > 1e-6 ? System.Math.Clamp(((x - x0) * dx + (y - y0) * dy) / len2, 0, 1) : 0;
        double px = x0 + t * dx, py = y0 + t * dy;
        double dist = System.Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
        return (float)System.Math.Clamp(halfW - dist + 0.5, 0, 1);
    }

    protected override Layer CreateClone() => new ShapeLayer(Kind, X, Y, W, H, R, G, B) { A = A, StrokeWidth = StrokeWidth };
}
