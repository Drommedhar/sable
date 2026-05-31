using System;
using System.Collections.Generic;
using Sable.Canvas.Platform;
using Sable.Engine.Commands;
using Sable.Engine.Layers;
using Sable.Gpu;
using Sable.Tools;

namespace Sable.Canvas;

/// <summary>
/// Node tool (PLAN §16.10, Phase 4): edit an existing <see cref="PathLayer"/>'s nodes/handles.
/// Drag an anchor to move the node (handles follow); drag a handle endpoint to reshape (smooth
/// nodes mirror the opposite handle, Alt breaks the mirror); Alt-click an anchor to delete it;
/// click on the path between nodes to insert a node (de Casteljau split, curve-preserving). Each
/// completed gesture is one undoable <see cref="EditPathCommand"/>. Operates on the selected layer.
/// </summary>
public sealed unsafe partial class GpuSurfaceControl
{
    private bool _nodeDragging;
    private int _nodeIdx = -1;
    private int _nodePart;          // 0 = anchor, 1 = in-handle, 2 = out-handle
    private PathLayer? _nodePath;
    private List<PathNode>? _nodeBefore;
    private bool _nodeBeforeClosed;

    private PathLayer? ActivePath => SelLayer as PathLayer;

    private void NodeDown(double dx, double dy, CanvasMods mods)
    {
        if (_doc is null || ActivePath is not { } p || p.Nodes.Count == 0) return;
        bool alt = mods.HasFlag(CanvasMods.Alt);
        double ox = p.OffsetX, oy = p.OffsetY;
        double hit = 7.0 / Math.Max(0.0001, EffectiveScale);

        // hit-test: handle endpoints first (they sit on top of anchors when pulled out), then anchors
        for (int i = 0; i < p.Nodes.Count; i++)
        {
            var n = p.Nodes[i];
            if (Math.Abs(n.InX - n.Ax) > 1.5 || Math.Abs(n.InY - n.Ay) > 1.5)
                if (Math.Abs(dx - (n.InX + ox)) <= hit && Math.Abs(dy - (n.InY + oy)) <= hit)
                { BeginNodeDrag(p, i, 1); return; }
            if (Math.Abs(n.OutX - n.Ax) > 1.5 || Math.Abs(n.OutY - n.Ay) > 1.5)
                if (Math.Abs(dx - (n.OutX + ox)) <= hit && Math.Abs(dy - (n.OutY + oy)) <= hit)
                { BeginNodeDrag(p, i, 2); return; }
        }
        for (int i = 0; i < p.Nodes.Count; i++)
        {
            var n = p.Nodes[i];
            if (Math.Abs(dx - (n.Ax + ox)) <= hit && Math.Abs(dy - (n.Ay + oy)) <= hit)
            {
                if (alt) { DeleteNode(p, i); return; }
                BeginNodeDrag(p, i, 0);
                return;
            }
        }

        // not on a node → try inserting one on the nearest segment (curve-preserving split)
        if (TryInsertOnPath(p, dx - ox, dy - oy, hit * 1.4)) return;
    }

    private void BeginNodeDrag(PathLayer p, int idx, int part)
    {
        _nodePath = p; _nodeIdx = idx; _nodePart = part;
        _nodeBefore = new List<PathNode>(p.Nodes);
        _nodeBeforeClosed = p.Closed;
        _nodeDragging = true;
        _input?.Capture();
    }

    private void NodeMove(double sx, double sy)
    {
        if (!_nodeDragging || _nodePath is not { } p || _nodeIdx < 0 || _nodeIdx >= p.Nodes.Count) return;
        var (dx, dy) = MapToDoc(sx, sy);
        float nx = (float)(dx - p.OffsetX), ny = (float)(dy - p.OffsetY);
        var n = p.Nodes[_nodeIdx];
        if (_nodePart == 0)
        {
            n.Translate(nx - n.Ax, ny - n.Ay);   // move anchor + both handles together
        }
        else if (_nodePart == 1)
        {
            n.InX = nx; n.InY = ny;
            if (n.Smooth) { n.OutX = n.Ax * 2 - nx; n.OutY = n.Ay * 2 - ny; }   // mirror
        }
        else
        {
            n.OutX = nx; n.OutY = ny;
            if (n.Smooth) { n.InX = n.Ax * 2 - nx; n.InY = n.Ay * 2 - ny; }
        }
        p.Nodes[_nodeIdx] = n;
        p.Dirty = true;
        _doc?.MarkStructureChanged();
    }

    private void NodeUp()
    {
        if (!_nodeDragging) return;
        _nodeDragging = false;
        _input?.ReleaseCapture();
        if (_nodePath is { } p && _nodeBefore is { } before && _doc is { } d)
            CommandProduced?.Invoke(new EditPathCommand(d, p, before, _nodeBeforeClosed, new List<PathNode>(p.Nodes), p.Closed));
        _nodeBefore = null; _nodeIdx = -1; _nodePath = null;
    }

    private void DeleteNode(PathLayer p, int idx)
    {
        if (_doc is null || p.Nodes.Count <= 2) return;   // keep at least a segment
        var before = new List<PathNode>(p.Nodes);
        var after = new List<PathNode>(p.Nodes);
        after.RemoveAt(idx);
        CommandProduced?.Invoke(new EditPathCommand(_doc, p, before, p.Closed, after, p.Closed));
    }

    /// <summary>Insert a node on the nearest cubic segment near (lx,ly) in layer-local px (offset removed).
    /// Splits the cubic with de Casteljau so the curve shape is preserved.</summary>
    private bool TryInsertOnPath(PathLayer p, double lx, double ly, double maxDist)
    {
        int n = p.Nodes.Count;
        if (n < 2) return false;
        int segCount = p.Closed ? n : n - 1;
        int bestSeg = -1; double bestT = 0, bestD = double.MaxValue;
        for (int s = 0; s < segCount; s++)
        {
            var a = p.Nodes[s]; var b = p.Nodes[(s + 1) % n];
            for (int k = 1; k < 32; k++)
            {
                double t = k / 32.0;
                var (px, py) = CubicPoint(a, b, t);
                double d = (px - lx) * (px - lx) + (py - ly) * (py - ly);
                if (d < bestD) { bestD = d; bestSeg = s; bestT = t; }
            }
        }
        if (bestSeg < 0 || Math.Sqrt(bestD) > maxDist) return false;

        var before = new List<PathNode>(p.Nodes);
        var after = new List<PathNode>(p.Nodes);
        int s0 = bestSeg, s1 = (bestSeg + 1) % n;
        var na = after[s0]; var nb = after[s1];
        double tt = bestT;
        // control points of the segment
        double p0x = na.Ax, p0y = na.Ay, p1x = na.OutX, p1y = na.OutY;
        double p2x = nb.InX, p2y = nb.InY, p3x = nb.Ax, p3y = nb.Ay;
        double ax = Lerp(p0x, p1x, tt), ay = Lerp(p0y, p1y, tt);
        double bx = Lerp(p1x, p2x, tt), by = Lerp(p1y, p2y, tt);
        double cx = Lerp(p2x, p3x, tt), cy = Lerp(p2y, p3y, tt);
        double dx2 = Lerp(ax, bx, tt), dy2 = Lerp(ay, by, tt);
        double ex = Lerp(bx, cx, tt), ey = Lerp(by, cy, tt);
        double fx = Lerp(dx2, ex, tt), fy = Lerp(dy2, ey, tt);   // point on curve

        na.OutX = (float)ax; na.OutY = (float)ay; na.Smooth = true;
        nb.InX = (float)cx; nb.InY = (float)cy; nb.Smooth = true;
        var mid = new PathNode((float)fx, (float)fy)
        {
            InX = (float)dx2, InY = (float)dy2, OutX = (float)ex, OutY = (float)ey, Smooth = true,
        };
        after[s0] = na; after[s1] = nb;
        after.Insert(s0 + 1, mid);
        CommandProduced?.Invoke(new EditPathCommand(_doc!, p, before, p.Closed, after, p.Closed));
        return true;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static (double x, double y) CubicPoint(PathNode a, PathNode b, double t)
    {
        double u = 1 - t;
        double w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
        return (w0 * a.Ax + w1 * a.OutX + w2 * b.InX + w3 * b.Ax,
                w0 * a.Ay + w1 * a.OutY + w2 * b.InY + w3 * b.Ay);
    }

    /// <summary>Populate the node-edit overlay (markers + spine) from a committed path. Surface px.</summary>
    private void BuildNodeOverlay(ref BlitOverlay ov, PathLayer p)
    {
        if (p.Nodes.Count == 0) return;
        var vp = ComputeViewport();
        (float sx, float sy) ToSurf(double ldx, double ldy)
            => ((float)(vp.Ox + (ldx + p.OffsetX) * vp.Scale), (float)(vp.Oy + (ldy + p.OffsetY) * vp.Scale));

        var nodes = new float[p.Nodes.Count * 6];
        for (int i = 0; i < p.Nodes.Count; i++)
        {
            var nd = p.Nodes[i];
            var (ax, ay) = ToSurf(nd.Ax, nd.Ay);
            var (ix, iy) = ToSurf(nd.InX, nd.InY);
            var (oxs, oys) = ToSurf(nd.OutX, nd.OutY);
            int b = i * 6;
            nodes[b] = ax; nodes[b + 1] = ay; nodes[b + 2] = ix; nodes[b + 3] = iy; nodes[b + 4] = oxs; nodes[b + 5] = oys;
        }
        var poly = p.Flatten(16);
        var flat = new float[poly.Count * 2];
        for (int i = 0; i < poly.Count; i++)
        {
            var (fsx, fsy) = ToSurf(poly[i].X, poly[i].Y);
            flat[i * 2] = fsx; flat[i * 2 + 1] = fsy;
        }
        ov.PenOn = true;
        ov.PenNodes = nodes;
        ov.PenFlat = flat;
        ov.PenActive = _nodeDragging ? _nodeIdx : -1;
    }
}
