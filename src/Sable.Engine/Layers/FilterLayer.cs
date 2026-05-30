namespace Sable.Engine.Layers;

/// <summary>
/// A non-destructive live filter as a first-class tree node (PLAN §5A.2/§16.5): no
/// pixels of its own — the compositor applies it to the accumulated backdrop below it,
/// then blends the result back with the layer's opacity + mask. Unlike an adjustment,
/// a filter samples neighbouring pixels (blur/sharpen/noise/etc).
/// </summary>
public sealed class FilterLayer : Layer
{
    public FilterKind Kind { get; }

    /// <summary>Blur radius / spread in pixels (also unsharp/high-pass/clarity radius).</summary>
    public float Radius { get; set; } = 8f;

    /// <summary>Strength: sharpen/unsharp/clarity amount, motion length, zoom strength, noise amount, denoise sigma.</summary>
    public float Amount { get; set; } = 1f;

    /// <summary>Motion-blur direction in degrees.</summary>
    public float Angle { get; set; }

    public FilterLayer(FilterKind kind = FilterKind.GaussianBlur)
    {
        Kind = kind;
        Name = kind switch
        {
            FilterKind.GaussianBlur => "Gaussian Blur",
            FilterKind.BoxBlur => "Box Blur",
            FilterKind.MotionBlur => "Motion Blur",
            FilterKind.ZoomBlur => "Zoom Blur",
            FilterKind.Sharpen => "Sharpen",
            FilterKind.UnsharpMask => "Unsharp Mask",
            FilterKind.HighPass => "High Pass",
            FilterKind.Clarity => "Clarity",
            FilterKind.AddNoise => "Add Noise",
            FilterKind.Denoise => "Denoise",
            _ => "Filter"
        };
    }

    protected override Layer CreateClone() => new FilterLayer(Kind) { Radius = Radius, Amount = Amount, Angle = Angle };
}

public enum FilterKind
{
    GaussianBlur = 0,
    BoxBlur = 1,
    MotionBlur = 2,
    ZoomBlur = 3,
    Sharpen = 4,
    UnsharpMask = 5,
    HighPass = 6,
    Clarity = 7,
    AddNoise = 8,
    Denoise = 9,
}
