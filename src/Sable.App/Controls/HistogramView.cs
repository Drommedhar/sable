using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sable.App;

/// <summary>
/// Standalone RGB histogram display (Levels panel header, Affinity-style). Set
/// <see cref="SetBins"/> with an int[768] R/G/B histogram.
/// </summary>
public sealed class HistogramView : Control
{
    private int[]? _bins;

    public HistogramView() { MinHeight = 60; }

    public void SetBins(int[]? bins) { _bins = bins; InvalidateVisual(); }

    public override void Render(DrawingContext ctx)
    {
        var area = new Rect(0, 0, Bounds.Width, Bounds.Height);
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18)), area);
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), 1), area);
        if (_bins is { } b) Histogram.Draw(ctx, area.Deflate(1), b, 7);
    }
}
