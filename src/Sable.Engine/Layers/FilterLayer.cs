namespace Sable.Engine.Layers;

/// <summary>
/// A non-destructive live filter as a first-class tree node (PLAN §5A.2): no pixels
/// of its own — the compositor applies it to the accumulated backdrop below it.
/// Unlike an adjustment, a filter samples neighboring pixels (e.g. blur). M1:
/// Gaussian blur. More filters (sharpen, etc.) follow the same pattern.
/// </summary>
public sealed class FilterLayer : Layer
{
    public FilterKind Kind { get; }

    /// <summary>Blur radius in pixels (Gaussian).</summary>
    public float Radius { get; set; } = 8f;

    public FilterLayer(FilterKind kind = FilterKind.GaussianBlur)
    {
        Kind = kind;
        Name = kind switch
        {
            FilterKind.GaussianBlur => "Gaussian Blur",
            _ => "Filter"
        };
    }
}

public enum FilterKind
{
    GaussianBlur = 0,
}
