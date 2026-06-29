using System;
using System.Collections.Generic;

namespace Sable.Engine.Layers;

/// <summary>
/// A single bézier path node: an anchor plus its two control handles (absolute doc px).
/// A corner node has both handles coincident with the anchor. <see cref="Smooth"/> marks
/// the node as having mirrored handles (drag one → the other reflects) for the Node tool.
/// </summary>
public struct PathNode
{
    public float Ax, Ay;        // anchor
    public float InX, InY;      // incoming handle (controls the segment ENDING at this node)
    public float OutX, OutY;    // outgoing handle (controls the segment STARTING at this node)
    public bool Smooth;

    public PathNode(float ax, float ay)
    {
        Ax = ax; Ay = ay; InX = ax; InY = ay; OutX = ax; OutY = ay; Smooth = false;
    }

    /// <summary>Full node with explicit handles (e.g. from a PSD vector-mask knot). Smooth when
    /// the in/out handles are not both coincident with the anchor.</summary>
    public PathNode(float ax, float ay, float inX, float inY, float outX, float outY)
    {
        Ax = ax; Ay = ay; InX = inX; InY = inY; OutX = outX; OutY = outY;
        Smooth = Math.Abs(InX - Ax) > 0.01f || Math.Abs(InY - Ay) > 0.01f
              || Math.Abs(OutX - Ax) > 0.01f || Math.Abs(OutY - Ay) > 0.01f;
    }

    /// <summary>Move the whole node (anchor + both handles) by a delta.</summary>
    public void Translate(float dx, float dy)
    {
        Ax += dx; Ay += dy; InX += dx; InY += dy; OutX += dx; OutY += dy;
    }
}

/// <summary>
/// A parametric vector path (PLAN §16.10, Phase 4): a list of cubic-bézier nodes + a closed
/// flag, with an optional fill and stroke. NO baked pixels — the compositor rasterizes it on
/// demand (flatten → scanline even-odd fill + distance-field stroke), so nodes/fill/stroke stay
/// editable and the layer's content bounds hug the geometry (tight Move/Node handles), Affinity-style.
/// </summary>
public sealed class PathLayer : Layer
{
    public List<PathNode> Nodes { get; set; } = new();
    public bool Closed { get; set; }

    /// <summary>Extra sub-paths beyond the primary <see cref="Nodes"/> (e.g. letter counters from
    /// text→curves). Filled together with the primary via even-odd so holes work; stroked each.
    /// The Pen/Node tools edit only the primary sub-path.</summary>
    public List<(List<PathNode> Nodes, bool Closed)> ExtraContours { get; set; } = new();

    // fill
    public bool Filled { get; set; } = true;
    public byte FillR { get; set; }
    public byte FillG { get; set; }
    public byte FillB { get; set; }
    public byte FillA { get; set; } = 255;

    // stroke
    public bool Stroked { get; set; }
    public byte StrokeR { get; set; }
    public byte StrokeG { get; set; }
    public byte StrokeB { get; set; }
    public byte StrokeA { get; set; } = 255;
    public float StrokeWidth { get; set; } = 2f;

    public PathLayer() { Name = "Path"; }

    public PathLayer(IEnumerable<PathNode> nodes, bool closed, byte r, byte g, byte b)
    {
        Nodes = new List<PathNode>(nodes);
        Closed = closed;
        FillR = r; FillG = g; FillB = b;
        Name = "Path";
    }

    protected override Layer CreateClone()
    {
        var c = new PathLayer(Nodes, Closed, FillR, FillG, FillB)
        {
            Filled = Filled, FillA = FillA,
            Stroked = Stroked, StrokeR = StrokeR, StrokeG = StrokeG, StrokeB = StrokeB,
            StrokeA = StrokeA, StrokeWidth = StrokeWidth, DashOn = DashOn, DashLen = DashLen, GapLen = GapLen,
            Cap = Cap, Join = Join,
        };
        foreach (var (nodes, closed) in ExtraContours)
            c.ExtraContours.Add((new List<PathNode>(nodes), closed));
        return c;
    }

    /// <summary>Flatten the path to a list of doc-px polylines (one per subpath; here always one).
    /// Each cubic segment is subdivided into <paramref name="steps"/> line segments.</summary>
    public List<(double X, double Y)> Flatten(int steps = 24)
    {
        var pts = new List<(double, double)>();
        int n = Nodes.Count;
        if (n == 0) return pts;
        if (n == 1) { pts.Add((Nodes[0].Ax, Nodes[0].Ay)); return pts; }

        int segCount = Closed ? n : n - 1;
        pts.Add((Nodes[0].Ax, Nodes[0].Ay));
        for (int s = 0; s < segCount; s++)
        {
            var a = Nodes[s];
            var b = Nodes[(s + 1) % n];
            double p0x = a.Ax, p0y = a.Ay;
            double p1x = a.OutX, p1y = a.OutY;
            double p2x = b.InX, p2y = b.InY;
            double p3x = b.Ax, p3y = b.Ay;
            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps, u = 1 - t;
                double w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
                pts.Add((w0 * p0x + w1 * p1x + w2 * p2x + w3 * p3x,
                         w0 * p0y + w1 * p1y + w2 * p2y + w3 * p3y));
            }
        }
        return pts;
    }

    /// <summary>Tight content bounds (doc px) over all anchors + handles, padded by the stroke half-width.</summary>
    public override (int x, int y, int w, int h) ContentBounds(int docW, int docH)
    {
        if (Nodes.Count == 0) return (0, 0, 1, 1);
        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        void Acc(double x, double y) { minx = Math.Min(minx, x); miny = Math.Min(miny, y); maxx = Math.Max(maxx, x); maxy = Math.Max(maxy, y); }
        void AccNode(PathNode nd) { Acc(nd.Ax, nd.Ay); Acc(nd.InX, nd.InY); Acc(nd.OutX, nd.OutY); }
        foreach (var nd in Nodes) AccNode(nd);
        foreach (var (nodes, _) in ExtraContours) foreach (var nd in nodes) AccNode(nd);
        int pad = Stroked ? (int)Math.Ceiling(StrokeWidth / 2) + 1 : 1;
        int x = (int)Math.Floor(minx) - pad;
        int y = (int)Math.Floor(miny) - pad;
        int w = (int)Math.Ceiling(maxx - minx) + pad * 2;
        int h = (int)Math.Ceiling(maxy - miny) + pad * 2;
        return (x, y, Math.Max(1, w), Math.Max(1, h));
    }

    // dashed stroke (shared with shapes)
    public bool DashOn { get; set; }
    public float DashLen { get; set; } = 12f;
    public float GapLen { get; set; } = 8f;
    public LineCap Cap { get; set; } = LineCap.Round;
    public LineJoin Join { get; set; } = LineJoin.Round;

    /// <summary>Flatten the primary sub-path + all extra contours (for multi-contour fill/stroke).</summary>
    public List<(List<(double X, double Y)> poly, bool closed)> FlattenAll(int steps = 24)
    {
        var all = new List<(List<(double, double)>, bool)>();
        var primary = Flatten(steps);
        if (primary.Count > 0) all.Add((primary, Closed));
        foreach (var (nodes, closed) in ExtraContours)
        {
            var p = new PathLayer { Closed = closed }; p.Nodes.AddRange(nodes);
            var f = p.Flatten(steps);
            if (f.Count > 0) all.Add((f, closed));
        }
        return all;
    }

    /// <summary>Rasterize fill + stroke into a doc-sized RGBA8 buffer (straight alpha). Clears first.</summary>
    public void Rasterize(byte[] dst, int dw, int dh)
    {
        Array.Clear(dst);
        var all = FlattenAll();
        if (all.Count == 0) return;

        if (Filled)
        {
            var closedPolys = new List<IReadOnlyList<(double X, double Y)>>();
            foreach (var (poly, closed) in all) if (closed && poly.Count >= 3) closedPolys.Add(poly);
            if (closedPolys.Count > 0)
                VectorRaster.FillMulti(dst, dw, dh, closedPolys, FillR, FillG, FillB, FillA);
        }
        if (Stroked && StrokeWidth > 0)
            foreach (var (poly, closed) in all)
                VectorRaster.Stroke(dst, dw, dh, poly, closed, StrokeWidth, StrokeR, StrokeG, StrokeB, StrokeA, DashOn, DashLen, GapLen, Cap, Join);
    }
}
