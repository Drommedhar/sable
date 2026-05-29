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
    MagicWand,    // contiguous-color selection
    Brush,
    Eraser,
    Fill,         // bucket / flood
    Gradient,     // linear gradient fill (foreground → transparent)
    Crop,         // resize the document to a drawn rectangle
    ShapeRect,    // filled rectangle
    ShapeEllipse, // filled ellipse
    ShapeLine,    // stroked line
    CloneStamp,   // copy pixels from a sampled source point
    Dodge,        // lighten
    Burn,         // darken
    Sponge,       // desaturate
    BlurBrush,    // soften
    SharpenBrush, // sharpen
    Smudge,       // push colour along the stroke
    Type,         // text layer
    Eyedropper,
    Hand,         // pan
    Zoom,
}
