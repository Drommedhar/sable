using System;
using Sable.Engine.Commands;
using Sable.Engine.Layers;
using Sable.Gpu;
using Sable.Tools;

namespace Sable.Canvas;

/// <summary>
/// Mesh-warp tool (PLAN §16.9): overlay a control-point grid over the active pixel layer, drag the
/// points, then Enter to apply (destructive triangle-mesh warp) or Esc to cancel. Grid points are
/// in document px; the warp runs in the layer buffer's own space. Point markers reuse the pen/node
/// blit overlay (binding 6).
/// </summary>
public sealed unsafe partial class GpuSurfaceControl
{
    private const int MeshGX = 5, MeshGY = 5;
    private (float X, float Y)[]? _meshSrc;   // undeformed grid (doc px)
    private (float X, float Y)[]? _meshDst;   // dragged grid (doc px)
    private PixelLayer? _meshLayer;
    private int _meshDragIdx = -1;

    /// <summary>True while a mesh-warp grid is active (Enter applies, Esc cancels).</summary>
    public bool MeshActive => _meshSrc is not null;

    /// <summary>Build a regular grid over the active layer's content bounds (called on tool select).</summary>
    public void BeginMeshWarp()
    {
        if (_doc is null || ActiveLayer is not { } l) { _meshSrc = null; return; }
        var (bx, by, bw, bh) = l.ContentBounds(_doc.Width, _doc.Height);
        bx += l.OffsetX; by += l.OffsetY;   // doc space
        _meshLayer = l;
        _meshSrc = new (float, float)[MeshGX * MeshGY];
        _meshDst = new (float, float)[MeshGX * MeshGY];
        for (int gy = 0; gy < MeshGY; gy++)
        for (int gx = 0; gx < MeshGX; gx++)
        {
            float px = bx + bw * gx / (float)(MeshGX - 1);
            float py = by + bh * gy / (float)(MeshGY - 1);
            int i = gy * MeshGX + gx;
            _meshSrc[i] = (px, py); _meshDst[i] = (px, py);
        }
        _meshDragIdx = -1;
    }

    private void MeshDown(double dx, double dy)
    {
        if (_meshDst is null) return;
        double tol = 10.0 / Math.Max(0.0001, EffectiveScale);
        int best = -1; double bd = tol;
        for (int i = 0; i < _meshDst.Length; i++)
        {
            double d = Math.Sqrt((dx - _meshDst[i].X) * (dx - _meshDst[i].X) + (dy - _meshDst[i].Y) * (dy - _meshDst[i].Y));
            if (d <= bd) { bd = d; best = i; }
        }
        if (best >= 0) { _meshDragIdx = best; _input?.Capture(); }
    }

    private void MeshDrag(double sx, double sy)
    {
        if (_meshDst is null || _meshDragIdx < 0) return;
        var (dx, dy) = MapToDoc(sx, sy);
        _meshDst[_meshDragIdx] = ((float)dx, (float)dy);
    }

    private void MeshUp()
    {
        if (_meshDragIdx >= 0) { _meshDragIdx = -1; _input?.ReleaseCapture(); }
    }

    /// <summary>Apply the mesh warp to the active layer (destructive, undoable). Resets the grid.</summary>
    public void CommitMeshWarp()
    {
        if (_doc is null || _meshLayer is not { } l || _meshSrc is null || _meshDst is null) { CancelMeshWarp(); return; }
        // any actual deformation?
        bool moved = false;
        for (int i = 0; i < _meshSrc.Length; i++)
            if (Math.Abs(_meshSrc[i].X - _meshDst[i].X) > 0.5f || Math.Abs(_meshSrc[i].Y - _meshDst[i].Y) > 0.5f) { moved = true; break; }
        if (!moved) { CancelMeshWarp(); return; }

        int lw = l.Width, lh = l.Height, ox = l.OffsetX, oy = l.OffsetY;
        var srcL = new (float, float)[_meshSrc.Length];
        var dstL = new (float, float)[_meshDst.Length];
        for (int i = 0; i < srcL.Length; i++)
        {
            srcL[i] = (_meshSrc[i].X - ox, _meshSrc[i].Y - oy);
            dstL[i] = (_meshDst[i].X - ox, _meshDst[i].Y - oy);
        }
        var before = RasterState.Capture(l);
        var warped = MeshWarpTool.Warp(l.Pixels, lw, lh, MeshGX, MeshGY, srcL, dstL);
        l.SetBuffer(lw, lh, warped);   // same dims + offset
        var after = RasterState.Capture(l);
        CommandProduced?.Invoke(new RasterStateCommand(l, before, after, () => l.Dirty = true));
        // re-base the grid on the new pixels
        BeginMeshWarp();
    }

    public void CancelMeshWarp()
    {
        if (_meshDragIdx >= 0) _input?.ReleaseCapture();   // mid point-drag → release the captured pointer
        _meshSrc = null; _meshDst = null; _meshLayer = null; _meshDragIdx = -1;
    }

    private void BuildMeshOverlay(ref BlitOverlay ov)
    {
        if (_meshDst is null) return;
        var vp = ComputeViewport();
        var nodes = new float[_meshDst.Length * 6];
        for (int i = 0; i < _meshDst.Length; i++)
        {
            float sx = (float)(vp.Ox + _meshDst[i].X * vp.Scale);
            float sy = (float)(vp.Oy + _meshDst[i].Y * vp.Scale);
            int b = i * 6;
            // anchor only (no handles) → drawn as a marker square
            nodes[b] = sx; nodes[b + 1] = sy; nodes[b + 2] = sx; nodes[b + 3] = sy; nodes[b + 4] = sx; nodes[b + 5] = sy;
        }
        ov.PenOn = true;
        ov.PenNodes = nodes;
        ov.PenActive = _meshDragIdx;
    }
}
