using System.Collections.Generic;
using Sable.Canvas.Platform;
using Sable.Core.Ai;
using Silk.NET.WebGPU;

namespace Sable.Canvas;

/// <summary>
/// AI hover-to-select (PHASE8_AI §8.3b, Affinity-style): SAM2 automatic mask generation precomputes
/// the active layer's objects (pushed in via <see cref="SetSmartObjects"/>); hovering highlights the
/// object under the cursor as diagonal stripes (blue=replace / green=add / red=subtract), clicking
/// commits it to the selection. Objects are at a bounded working resolution; the cursor maps doc→work.
/// </summary>
public sealed unsafe partial class GpuSurfaceControl
{
    private IReadOnlyList<ObjectMask>? _smartObjects;
    // doc-space rect of the analysed layer: SAM2 ran on the layer's own (content-sized, offset) buffer, so
    // the objects live in layer space and must be placed at this rect — NOT stretched across the doc.
    private int _smOffX, _smOffY, _smLayerW, _smLayerH;
    private byte[]? _previewCov;          // doc-sized R8 coverage of the hovered object
    private float _previewMode;           // 0 off, 1 blue (replace), 2 green (add), 3 red (subtract)
    private int _previewVer, _previewUploadedVer = -1;

    private Texture* _previewTex;
    private TextureView* _previewView;
    private int _previewTexW, _previewTexH;

    /// <summary>Receive the precomputed object masks plus the analysed layer's doc-space rect
    /// (offset + dims). The objects are in that layer's coordinate space; the rect places + scales them
    /// into the document so an offset / content-sized layer's masks line up. Resets hover.</summary>
    public void SetSmartObjects(IReadOnlyList<ObjectMask>? objects, int offX = 0, int offY = 0, int layerW = 0, int layerH = 0)
    {
        _smartObjects = objects is { Count: > 0 } ? objects : null;
        _smOffX = offX; _smOffY = offY; _smLayerW = layerW; _smLayerH = layerH;
        ClearSmartHover();
    }

    public bool HasSmartObjects => _smartObjects is { Count: > 0 };

    private void ClearSmartHover()
    {
        _previewCov = null;
        _previewMode = 0f;
        _previewVer++;
    }

    /// <summary>Object under the cursor (doc px), mapped through the analysed layer's rect into the
    /// objects' working resolution. Returns null when the cursor is outside the analysed layer.</summary>
    private ObjectMask? ObjectAt(double dx, double dy)
    {
        if (_smartObjects is not { Count: > 0 } objs || _doc is null) return null;
        int lw = _smLayerW > 0 ? _smLayerW : _doc.Width;
        int lh = _smLayerH > 0 ? _smLayerH : _doc.Height;
        double lx = dx - _smOffX, ly = dy - _smOffY;                 // doc → layer-local
        if (lx < 0 || ly < 0 || lx >= lw || ly >= lh) return null;   // outside the analysed layer
        int ow = objs[0].Width, oh = objs[0].Height;
        int ox = (int)(lx / lw * ow);
        int oy = (int)(ly / lh * oh);
        return AmgOps.BestAt(objs, ox, oy);
    }

    /// <summary>Bilinear-expand an object's working-res soft mask to doc-sized coverage (smooth edges,
    /// not a nearest-neighbour staircase).</summary>
    private byte[]? ObjectToDocCoverage(ObjectMask obj)
    {
        if (_doc is null) return null;
        int w = _doc.Width, h = _doc.Height, ow = obj.Width, oh = obj.Height;
        int lw = _smLayerW > 0 ? _smLayerW : w;
        int lh = _smLayerH > 0 ? _smLayerH : h;
        int ox0 = _smOffX, oy0 = _smOffY;
        var cov = new byte[w * h];   // zero outside the layer rect
        var src = obj.Coverage;
        // object res (ow×oh) maps to the layer rect (lw×lh) placed at (ox0,oy0) in the doc — bilinear sample
        double fx = (double)ow / lw, fy = (double)oh / lh;
        int xStart = System.Math.Max(0, ox0), xEnd = System.Math.Min(w, ox0 + lw);
        int yStart = System.Math.Max(0, oy0), yEnd = System.Math.Min(h, oy0 + lh);
        for (int y = yStart; y < yEnd; y++)
        {
            double syf = ((y - oy0) + 0.5) * fy - 0.5;
            int y0 = (int)System.Math.Floor(syf); double wy = syf - y0;
            int y0c = System.Math.Clamp(y0, 0, oh - 1), y1c = System.Math.Clamp(y0 + 1, 0, oh - 1);
            for (int x = xStart; x < xEnd; x++)
            {
                double sxf = ((x - ox0) + 0.5) * fx - 0.5;
                int x0 = (int)System.Math.Floor(sxf); double wx = sxf - x0;
                int x0c = System.Math.Clamp(x0, 0, ow - 1), x1c = System.Math.Clamp(x0 + 1, 0, ow - 1);
                double v00 = src[y0c * ow + x0c], v10 = src[y0c * ow + x1c];
                double v01 = src[y1c * ow + x0c], v11 = src[y1c * ow + x1c];
                double top = v00 + (v10 - v00) * wx, bot = v01 + (v11 - v01) * wx;
                cov[y * w + x] = (byte)System.Math.Clamp(top + (bot - top) * wy + 0.5, 0, 255);
            }
        }
        return cov;
    }

    /// <summary>Hover: highlight the object under the cursor in the modifier's colour (blue/green/red).</summary>
    private void UpdateSmartHover(double dx, double dy, CanvasMods mods)
    {
        if (!HasSmartObjects) { ClearSmartHover(); return; }
        var obj = ObjectAt(dx, dy);
        if (obj is null) { if (_previewMode != 0f) ClearSmartHover(); return; }

        _previewCov = ObjectToDocCoverage(obj);
        _previewVer++;
        bool shift = mods.HasFlag(CanvasMods.Shift), alt = mods.HasFlag(CanvasMods.Alt);
        _previewMode = alt ? 3f : shift ? 2f : 1f;   // red subtract / green add / blue replace
    }

    /// <summary>Click: commit the hovered object to the selection (replace/add/subtract per modifiers).</summary>
    private void SmartSelectClick(double dx, double dy, CanvasMods mods)
    {
        if (_doc is null || !HasSmartObjects) return;
        var obj = ObjectAt(dx, dy);
        if (obj is null) return;
        var cov = ObjectToDocCoverage(obj);
        if (cov is null) return;
        CaptureSelMode(mods);
        ApplyMask(cov);
        ClearSmartHover();   // committed → marching ants take over
    }

    /// <summary>(Re)upload the hover-preview coverage into its R8 doc texture; gated by version.</summary>
    private void UpdatePreviewTexture()
    {
        if (_gpu is null || _doc is null || _previewCov is null) return;
        var api = _gpu.Api;
        int w = _doc.Width, h = _doc.Height;

        if (_previewTex is null || _previewTexW != w || _previewTexH != h)
        {
            if (_previewView is not null) { api.TextureViewRelease(_previewView); _previewView = null; }
            if (_previewTex is not null) { api.TextureRelease(_previewTex); _previewTex = null; }
            var td = new TextureDescriptor
            {
                Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
                Dimension = TextureDimension.Dimension2D,
                Size = new Extent3D { Width = (uint)w, Height = (uint)h, DepthOrArrayLayers = 1 },
                Format = TextureFormat.R8Unorm, MipLevelCount = 1, SampleCount = 1
            };
            _previewTex = api.DeviceCreateTexture(_gpu.Device, in td);
            _previewView = api.TextureCreateView(_previewTex, null);
            _previewTexW = w; _previewTexH = h;
            _previewUploadedVer = -1;
        }

        if (_previewUploadedVer == _previewVer) return;

        int aligned = (w + 255) & ~255;
        byte[] src = _previewCov;
        if (aligned != w)
        {
            var padded = new byte[aligned * h];
            for (int y = 0; y < h; y++) System.Array.Copy(_previewCov, y * w, padded, y * aligned, w);
            src = padded;
        }
        var dst = new ImageCopyTexture { Texture = _previewTex, MipLevel = 0, Aspect = TextureAspect.All };
        var layout = new TextureDataLayout { Offset = 0, BytesPerRow = (uint)aligned, RowsPerImage = (uint)h };
        var ext = new Extent3D { Width = (uint)w, Height = (uint)h, DepthOrArrayLayers = 1 };
        fixed (byte* p = src)
            api.QueueWriteTexture(_gpu.Queue, in dst, p, (nuint)src.Length, in layout, in ext);
        _previewUploadedVer = _previewVer;
    }

    private void ReleaseSmartSelect()
    {
        if (_previewView is not null) { _gpu?.Api.TextureViewRelease(_previewView); _previewView = null; }
        if (_previewTex is not null) { _gpu?.Api.TextureRelease(_previewTex); _previewTex = null; }
    }
}
