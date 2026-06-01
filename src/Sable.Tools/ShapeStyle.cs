namespace Sable.Tools;

/// <summary>
/// Draw-time defaults for the Shape tools (PLAN §16.10): fill on/off (fill colour comes from the
/// foreground/brush colour), stroke on/off + colour + width + dash, and per-kind params
/// (rounded-rect corner radius, polygon/star sides, star inner ratio). The options bar mutates
/// this; <c>GpuSurfaceControl</c> bakes it into each new <c>ShapeLayer</c>. Editing after the fact
/// is done on the layer itself via the Shape properties panel.
/// </summary>
public sealed class ShapeStyle
{
    public bool Filled { get; set; } = true;

    public bool StrokeOn { get; set; }
    public byte StrokeR { get; set; }
    public byte StrokeG { get; set; }
    public byte StrokeB { get; set; }
    public float StrokeWidth { get; set; } = 3f;
    public bool DashOn { get; set; }
    public float DashLen { get; set; } = 12f;
    public float GapLen { get; set; } = 8f;

    public float CornerRadius { get; set; } = 12f;
    public int Sides { get; set; } = 5;
    public float InnerRatio { get; set; } = 0.5f;
}
