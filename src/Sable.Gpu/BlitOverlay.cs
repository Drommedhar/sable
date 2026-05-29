using Silk.NET.WebGPU;

namespace Sable.Gpu;

/// <summary>
/// On-canvas overlays drawn by the blit pass (in surface/screen space where noted):
/// a marching-ants rectangle (layer bounds / marquee, in doc px) and/or a transform
/// gizmo (rotated box + corner + rotate handles, corners in surface px).
/// </summary>
public struct BlitOverlay
{
    // marching-ants rect, document pixels
    public bool RectOn;
    public float RectX, RectY, RectW, RectH;
    public bool SelHandles;   // draw GIMP-style resize grips on the rect

    // transform gizmo — 4 corners (TL,TR,BR,BL) in SURFACE pixels, 8 floats
    public bool GizmoOn;
    public float[]? Corners;
    public float RotateHandleDist;   // surface px from top edge to the rotate handle

    // brush cursor preview — centre + radius in SURFACE pixels, ghost of the dab
    public bool BrushOn;
    public float BrushX, BrushY, BrushR;
    public float BrushColR, BrushColG, BrushColB;   // 0..1
    public bool BrushErase;
    public float BrushHardness;

    // non-rectangular selection edge: marching ants traced along an R8 coverage mask
    // (ellipse/lasso/wand). MaskView is a doc-sized R8 texture sampled in doc UV.
    public bool MaskOn;
    public unsafe TextureView* MaskView;
}
