using System;
using System.Collections.Generic;
using Sable.Canvas.Platform;
using Sable.Engine.Layers;
using Sable.Gpu;
using Sable.Tools;

namespace Sable.Canvas;

/// <summary>
/// Pen tool (PLAN §16.10, Phase 4): click places corner nodes, click-drag pulls mirrored
/// bézier handles (smooth node). Click the first node (or Enter) to finish — closed if it
/// returned to the start, open otherwise. Esc cancels. The in-progress path is previewed via
/// the blit overlay (spine + node/handle markers); on commit a <see cref="PathLayer"/> is
/// produced (one undoable layer-add).
/// </summary>
public sealed unsafe partial class GpuSurfaceControl
{
    private readonly List<PathNode> _penNodes = new();
    private bool _penDragging;       // pulling the handle on the just-placed node
    private bool _penClosed;         // path returned to the first node

    /// <summary>True while a pen path is being drawn (so the host can route Enter/Esc + show the tool state).</summary>
    public bool PenActive => _penNodes.Count > 0;

    private void PenDown(double dx, double dy, CanvasMods mods)
    {
        if (_doc is null) return;

        // click back on the first anchor → close + commit
        if (_penNodes.Count >= 2)
        {
            double cth = 9.0 / Math.Max(0.0001, EffectiveScale);
            var f = _penNodes[0];
            if (Math.Abs(dx - f.Ax) <= cth && Math.Abs(dy - f.Ay) <= cth)
            {
                _penClosed = true;
                CommitPen();
                return;
            }
        }

        _penNodes.Add(new PathNode((float)dx, (float)dy));
        _penDragging = true;
        _input?.Capture();
    }

    private void PenMove(double sx, double sy)
    {
        if (!_penDragging || _penNodes.Count == 0) return;
        var (dx, dy) = MapToDoc(sx, sy);
        int i = _penNodes.Count - 1;
        var nd = _penNodes[i];
        // pull mirrored handles: out toward the cursor, in reflected across the anchor (smooth node)
        nd.OutX = (float)dx; nd.OutY = (float)dy;
        nd.InX = nd.Ax * 2 - (float)dx; nd.InY = nd.Ay * 2 - (float)dy;
        nd.Smooth = true;
        _penNodes[i] = nd;
    }

    private void PenUp()
    {
        if (!_penDragging) return;
        _penDragging = false;
        _input?.ReleaseCapture();
        // tiny drag → keep it a corner (handles coincident with the anchor)
        int i = _penNodes.Count - 1;
        var nd = _penNodes[i];
        if (Math.Abs(nd.OutX - nd.Ax) < 1.5 && Math.Abs(nd.OutY - nd.Ay) < 1.5)
        {
            nd.InX = nd.Ax; nd.InY = nd.Ay; nd.OutX = nd.Ax; nd.OutY = nd.Ay; nd.Smooth = false;
            _penNodes[i] = nd;
        }
    }

    /// <summary>Finish the path (Enter / closed). Open if it was not closed. Needs ≥2 nodes.</summary>
    public void CommitPen()
    {
        if (_doc is null || _penNodes.Count < 2) { CancelPen(); return; }
        bool closed = _penClosed;
        var path = new PathLayer(_penNodes, closed, Brush.R, Brush.G, Brush.B)
        {
            Filled = closed,
            FillA = 255,
            Stroked = !closed,
            StrokeR = Brush.R, StrokeG = Brush.G, StrokeB = Brush.B, StrokeA = 255,
            StrokeWidth = 2f,
        };
        _penNodes.Clear();
        _penClosed = false;
        _penDragging = false;
        LayerProduced?.Invoke(path);
    }

    /// <summary>Discard the in-progress path (Esc / tool switch).</summary>
    public void CancelPen()
    {
        bool had = _penNodes.Count > 0;
        _penNodes.Clear();
        _penClosed = false;
        _penDragging = false;
        if (had) _input?.ReleaseCapture();
    }

    /// <summary>Populate the pen overlay (markers + live spine) for the blit pass. Surface px.</summary>
    private void BuildPenOverlay(ref BlitOverlay ov)
    {
        if (_penNodes.Count == 0) return;
        var vp = ComputeViewport();
        float S(double d) => (float)d;
        (float sx, float sy) ToSurf(double dx, double dy) => (S(vp.Ox + dx * vp.Scale), S(vp.Oy + dy * vp.Scale));

        // node markers (anchor + handles), surface px
        var nodes = new float[_penNodes.Count * 6];
        for (int i = 0; i < _penNodes.Count; i++)
        {
            var n = _penNodes[i];
            var (ax, ay) = ToSurf(n.Ax, n.Ay);
            var (ix, iy) = ToSurf(n.InX, n.InY);
            var (ox, oy) = ToSurf(n.OutX, n.OutY);
            int b = i * 6;
            nodes[b] = ax; nodes[b + 1] = ay; nodes[b + 2] = ix; nodes[b + 3] = iy; nodes[b + 4] = ox; nodes[b + 5] = oy;
        }

        // live spine: flatten the placed nodes, plus a provisional rubber-band node at the cursor
        // (only while NOT actively pulling a handle and the path is still open)
        var preview = new PathLayer { Closed = _penClosed };
        preview.Nodes.AddRange(_penNodes);
        if (!_penClosed && !_penDragging && _penNodes.Count >= 1)
        {
            var (cdx, cdy) = MapToDoc(_lastMouseX, _lastMouseY);
            preview.Nodes.Add(new PathNode((float)cdx, (float)cdy));
        }
        var poly = preview.Flatten(16);
        var flat = new float[poly.Count * 2];
        for (int i = 0; i < poly.Count; i++)
        {
            var (fsx, fsy) = ToSurf(poly[i].X, poly[i].Y);
            flat[i * 2] = fsx; flat[i * 2 + 1] = fsy;
        }

        ov.PenOn = true;
        ov.PenNodes = nodes;
        ov.PenFlat = flat;
        ov.PenActive = 0;   // highlight the first node (the close target)
    }
}
