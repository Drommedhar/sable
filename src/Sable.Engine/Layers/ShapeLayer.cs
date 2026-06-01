using System;
using System.Collections.Generic;

namespace Sable.Engine.Layers;

public enum ShapeKind { Rectangle, Ellipse, Line, RoundedRect, Polygon, Star, Arrow }

/// <summary>
/// A parametric (vector-ish) shape as a first-class layer (PLAN §4/§16.10): kind + bounds rect
/// + fill + stroke (width/colour/dash) + per-kind params (corner radius, polygon/star sides,
/// star inner ratio). NO baked pixels — the compositor rasterizes it on demand via the shared
/// <see cref="VectorRaster"/> (outline → fill + dash-aware stroke), so everything stays editable
/// and the layer's content bounds hug the shape (tight Move handles), Affinity-style.
/// </summary>
public sealed class ShapeLayer : Layer
{
    public ShapeKind Kind { get; set; }
    public float X, Y, W, H;                 // shape bounds rect in document px

    // fill (R/G/B/A kept as the historical names so old call-sites + serializer still match)
    public byte R, G, B, A = 255;
    public bool Filled { get; set; } = true;

    // stroke
    public bool Stroked { get; set; }
    public byte StrokeR, StrokeG, StrokeB, StrokeA = 255;
    public float StrokeWidth = 4f;
    public bool DashOn { get; set; }
    public float DashLen { get; set; } = 12f;
    public float GapLen { get; set; } = 8f;
    public LineCap Cap { get; set; } = LineCap.Round;
    public LineJoin Join { get; set; } = LineJoin.Round;

    // per-kind params
    public float CornerRadius { get; set; } = 12f;   // RoundedRect
    public int Sides { get; set; } = 5;              // Polygon / Star
    public float InnerRatio { get; set; } = 0.5f;    // Star

    public ShapeLayer(ShapeKind kind, float x, float y, float w, float h, byte r, byte g, byte b)
    {
        Kind = kind;
        X = x; Y = y; W = w; H = h; R = r; G = g; B = b;
        // line/arrow default to stroke-only with the picked colour; closed kinds default to fill
        if (kind is ShapeKind.Line or ShapeKind.Arrow)
        {
            Filled = false; Stroked = true;
            StrokeR = r; StrokeG = g; StrokeB = b;
        }
        Name = kind switch
        {
            ShapeKind.Ellipse => "Ellipse", ShapeKind.Line => "Line", ShapeKind.RoundedRect => "Rounded Rectangle",
            ShapeKind.Polygon => "Polygon", ShapeKind.Star => "Star", ShapeKind.Arrow => "Arrow", _ => "Rectangle"
        };
    }

    private bool IsLineKind => Kind is ShapeKind.Line or ShapeKind.Arrow;

    /// <summary>Tight content bounds (doc px) — used for Move/Transform handles.</summary>
    public override (int x, int y, int w, int h) ContentBounds(int docW, int docH)
    {
        double minx, miny, maxx, maxy;
        if (IsLineKind)
        {
            minx = Math.Min(X, X + W); maxx = Math.Max(X, X + W);
            miny = Math.Min(Y, Y + H); maxy = Math.Max(Y, Y + H);
        }
        else { minx = Math.Min(X, X + W); maxx = Math.Max(X, X + W); miny = Math.Min(Y, Y + H); maxy = Math.Max(Y, Y + H); }
        double sw = (Stroked || IsLineKind) ? StrokeWidth / 2 : 0;
        double head = Kind == ShapeKind.Arrow ? Math.Max(StrokeWidth * 4, 8) : 0;
        double pad0 = Math.Max(sw, head);
        int pad = pad0 > 0 ? (int)Math.Ceiling(pad0) + 1 : 0;   // tight bounds for fill-only shapes
        int x = (int)Math.Floor(minx) - pad, y = (int)Math.Floor(miny) - pad;
        int w = (int)Math.Ceiling(maxx - minx) + pad * 2, h = (int)Math.Ceiling(maxy - miny) + pad * 2;
        return (x, y, Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>Build the shape outline polyline (doc px) + whether it is closed.</summary>
    public (List<(double X, double Y)> pts, bool closed) BuildOutline()
    {
        var pts = new List<(double, double)>();
        double minx = Math.Min(X, X + W), maxx = Math.Max(X, X + W);
        double miny = Math.Min(Y, Y + H), maxy = Math.Max(Y, Y + H);
        double cx = (minx + maxx) / 2, cy = (miny + maxy) / 2;
        double rx = Math.Max(0.5, (maxx - minx) / 2), ry = Math.Max(0.5, (maxy - miny) / 2);

        switch (Kind)
        {
            case ShapeKind.Rectangle:
                pts.Add((minx, miny)); pts.Add((maxx, miny)); pts.Add((maxx, maxy)); pts.Add((minx, maxy));
                return (pts, true);

            case ShapeKind.RoundedRect:
            {
                double r = Math.Clamp(CornerRadius, 0, Math.Min(maxx - minx, maxy - miny) / 2);
                if (r <= 0.5)
                {
                    pts.Add((minx, miny)); pts.Add((maxx, miny)); pts.Add((maxx, maxy)); pts.Add((minx, maxy));
                    return (pts, true);
                }
                const int seg = 6;
                // corners CW from top-left: centres + arc sweep
                AddArc(pts, minx + r, miny + r, r, 180, 270, seg);
                AddArc(pts, maxx - r, miny + r, r, 270, 360, seg);
                AddArc(pts, maxx - r, maxy - r, r, 0, 90, seg);
                AddArc(pts, minx + r, maxy - r, r, 90, 180, seg);
                return (pts, true);
            }

            case ShapeKind.Ellipse:
            {
                const int seg = 64;
                for (int i = 0; i < seg; i++)
                {
                    double a = i * 2 * Math.PI / seg;
                    pts.Add((cx + Math.Cos(a) * rx, cy + Math.Sin(a) * ry));
                }
                return (pts, true);
            }

            case ShapeKind.Polygon:
            {
                int n = Math.Max(3, Sides);
                for (int i = 0; i < n; i++)
                {
                    double a = -Math.PI / 2 + i * 2 * Math.PI / n;
                    pts.Add((cx + Math.Cos(a) * rx, cy + Math.Sin(a) * ry));
                }
                return (pts, true);
            }

            case ShapeKind.Star:
            {
                int n = Math.Max(3, Sides);
                double ir = Math.Clamp(InnerRatio, 0.05f, 0.95f);
                for (int i = 0; i < n * 2; i++)
                {
                    double a = -Math.PI / 2 + i * Math.PI / n;
                    double rr = (i % 2 == 0) ? 1.0 : ir;
                    pts.Add((cx + Math.Cos(a) * rx * rr, cy + Math.Sin(a) * ry * rr));
                }
                return (pts, true);
            }

            default: // Line / Arrow — open 2-point polyline (arrowhead drawn separately)
                pts.Add((X, Y)); pts.Add((X + W, Y + H));
                return (pts, false);
        }
    }

    private static void AddArc(List<(double, double)> pts, double cx, double cy, double r, double a0Deg, double a1Deg, int seg)
    {
        for (int i = 0; i <= seg; i++)
        {
            double a = (a0Deg + (a1Deg - a0Deg) * i / seg) * Math.PI / 180;
            pts.Add((cx + Math.Cos(a) * r, cy + Math.Sin(a) * r));
        }
    }

    /// <summary>Rasterize the shape into a doc-sized RGBA8 buffer (straight alpha). Clears first.</summary>
    public void Rasterize(byte[] dst, int dw, int dh)
    {
        Array.Clear(dst);
        var (pts, closed) = BuildOutline();
        if (pts.Count < 2) return;

        if (Filled && closed)
            VectorRaster.Fill(dst, dw, dh, pts, R, G, B, A);

        if (Kind == ShapeKind.Arrow)
        {
            RasterizeArrow(dst, dw, dh);
            return;
        }

        bool stroke = Stroked || IsLineKind;   // a line with no stroke would be invisible
        if (stroke && StrokeWidth > 0)
            VectorRaster.Stroke(dst, dw, dh, pts, closed, StrokeWidth, StrokeR, StrokeG, StrokeB, StrokeA, DashOn, DashLen, GapLen, Cap, Join);
    }

    private void RasterizeArrow(byte[] dst, int dw, int dh)
    {
        double p0x = X, p0y = Y, p1x = X + W, p1y = Y + H;
        double dx = p1x - p0x, dy = p1y - p0y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3) return;
        double ux = dx / len, uy = dy / len, px = -uy, py = ux;       // unit dir + perpendicular
        double headLen = Math.Min(len, Math.Max(StrokeWidth * 4, 8));
        double headWid = headLen * 0.85;
        double basex = p1x - ux * headLen, basey = p1y - uy * headLen;
        // shaft: from start to the head base
        var shaft = new List<(double, double)> { (p0x, p0y), (basex, basey) };
        if (StrokeWidth > 0)
            VectorRaster.Stroke(dst, dw, dh, shaft, false, StrokeWidth, StrokeR, StrokeG, StrokeB, StrokeA, DashOn, DashLen, GapLen, Cap, Join);
        // head: filled triangle (tip + two base corners), in the stroke colour
        var head = new List<(double, double)>
        {
            (p1x, p1y),
            (basex + px * headWid / 2, basey + py * headWid / 2),
            (basex - px * headWid / 2, basey - py * headWid / 2),
        };
        VectorRaster.Fill(dst, dw, dh, head, StrokeR, StrokeG, StrokeB, StrokeA);
    }

    protected override Layer CreateClone() => new ShapeLayer(Kind, X, Y, W, H, R, G, B)
    {
        A = A, Filled = Filled,
        Stroked = Stroked, StrokeR = StrokeR, StrokeG = StrokeG, StrokeB = StrokeB, StrokeA = StrokeA,
        StrokeWidth = StrokeWidth, DashOn = DashOn, DashLen = DashLen, GapLen = GapLen, Cap = Cap, Join = Join,
        CornerRadius = CornerRadius, Sides = Sides, InnerRatio = InnerRatio,
    };
}
