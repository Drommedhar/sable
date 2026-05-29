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
    Eyedropper,
    Hand,         // pan
    Zoom,
}
