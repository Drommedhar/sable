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
    public bool QuickMask;   // when true, render the mask as translucent red (rubylith) instead of ants

    // gradient tool drag line — start/end in SURFACE pixels
    public bool GradientOn;
    public float GradX0, GradY0, GradX1, GradY1;

    // crop preview — dims the document outside the crop rect (reuses RectX/Y/W/H, doc px)
    public bool CropOn;

    // shape tool drag preview — outline in SURFACE pixels. ShapeKind: 0=rect,1=ellipse,2=line
    public bool ShapeOn;
    public int ShapeKind;
    public float ShX0, ShY0, ShX1, ShY1;

    // clone-stamp source marker — crosshair at the live source point (SURFACE px)
    public bool CloneSrcOn;
    public float CloneSrcSx, CloneSrcSy;

    // text caret — vertical bar (SURFACE px)
    public bool CaretOn;
    public float CaretX, CaretY0, CaretY1;

    // pasteboard (the surround outside the document) colour, 0..1. Themed by the chrome.
    // Left at 0 → blitter falls back to the default dark grey.
    public float PasteR, PasteG, PasteB;

    // document grid (doc px spacing) + 1px pixel grid (shown only when zoomed in)
    public bool GridOn;
    public float GridSpacing;
    public bool PixelGrid;

    // guide lines (document px): vertical (constant X) + horizontal (constant Y)
    public float[]? GuidesX;
    public float[]? GuidesY;

    // transient smart-guide alignment lines (magenta) shown while moving a layer
    public float[]? SmartX;
    public float[]? SmartY;

    // pen-tool node markers — geometry in SURFACE px: per node [ax,ay,inx,iny,outx,outy].
    // PenActive = index of the highlighted (active/first) anchor, -1 for none.
    public bool PenOn;
    public float[]? PenNodes;   // length = 6 × nodeCount
    public float[]? PenFlat;    // flattened spine polyline, length = 2 × pointCount (surface px)
    public int PenActive;

    // AI hover-select object preview (PHASE8_AI §8.3b): a doc-sized R8 coverage of the hovered object,
    // drawn as diagonal stripes. PreviewMode: 0 off, 1 blue (replace), 2 green (add), 3 red (subtract).
    public unsafe TextureView* PreviewMaskView;
    public float PreviewMode;

    // customisable overlay colours (0..1). When HasOverlayColors is false the blitter
    // uses built-in defaults (cyan guides / magenta smart-guides / grey grid / red quick-mask).
    public bool HasOverlayColors;
    public float GuideColR, GuideColG, GuideColB;
    public float SmartColR, SmartColG, SmartColB;
    public float GridColR, GridColG, GridColB;
    public float QuickMaskColR, QuickMaskColG, QuickMaskColB;
}
