namespace Sable.Tools;

/// <summary>
/// Active canvas tool (PLAN §14). Mirrors the PS/Affinity left toolbar; grows as
/// tools land. Flyout grouping + options bar are layered on as the strip matures.
/// </summary>
public enum ToolKind
{
    Move,
    Transform,    // scale/rotate gizmo
    Marquee,      // rectangular selection
    EllipseMarquee, // elliptical selection
    Lasso,        // freehand selection
    PolyLasso,    // polygonal selection (click vertices, double-click/Enter to close)
    MagicWand,    // contiguous-color selection
    ColorRange,   // global colour-similarity selection (non-contiguous)
    SmartSelect,  // AI hover-to-select (SAM2 automatic mask generation) — PHASE8_AI §8.3b
    Brush,
    Pencil,       // hard-edged aliased brush
    Eraser,
    Fill,         // bucket / flood
    Gradient,     // linear gradient fill (foreground → transparent)
    Crop,         // resize the document to a drawn rectangle
    ShapeRect,    // filled rectangle
    ShapeRoundedRect, // rounded rectangle
    ShapeEllipse, // filled ellipse
    ShapeLine,    // stroked line
    ShapePolygon, // regular n-gon
    ShapeStar,    // n-point star
    ShapeArrow,   // arrow (line + head)
    CloneStamp,   // copy pixels from a sampled source point
    Heal,         // clone source texture + match destination tone (healing brush)
    SpotHeal,     // heal using an auto-picked nearby source (no Alt-click)
    Patch,        // drag a selection to a clean source region, tone-matched
    Liquify,      // push/bloat/pucker/twirl displacement brush
    MeshWarp,     // deform through a draggable control-point grid
    Dodge,        // lighten
    Burn,         // darken
    Sponge,       // desaturate
    BlurBrush,    // soften
    SharpenBrush, // sharpen
    Smudge,       // push colour along the stroke
    Type,         // text layer
    Pen,          // vector bézier path (click = corner node, drag = smooth handles)
    Node,         // edit an existing path's nodes/handles (drag/add/delete)
    Eyedropper,
    Hand,         // pan
    Zoom,
}
