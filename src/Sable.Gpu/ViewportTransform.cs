namespace Sable.Gpu;

/// <summary>
/// Maps document space into the surface for presentation: where the document's
/// top-left sits (in surface pixels), the pixels-per-doc-pixel scale, and the doc
/// size. Consumed by fullscreen_blit.wgsl (matches its Viewport uniform layout).
/// </summary>
public struct ViewportTransform
{
    public float Ox;
    public float Oy;
    public float Scale;     // surface pixels per document pixel
    public float DocW;
    public float DocH;

    /// <summary>
    /// Aspect-fit the document into the surface (zoom=1 = fit), then apply zoom
    /// about the surface center and a pixel pan. Pure — unit-testable.
    /// </summary>
    public static ViewportTransform Fit(float surfaceW, float surfaceH,
        float docW, float docH, double zoom, double panX, double panY)
    {
        if (docW <= 0) docW = 1;
        if (docH <= 0) docH = 1;
        float fit = Math.Min(surfaceW / docW, surfaceH / docH);
        float scale = fit * (float)zoom;
        float ox = (surfaceW - docW * scale) * 0.5f + (float)panX;
        float oy = (surfaceH - docH * scale) * 0.5f + (float)panY;
        return new ViewportTransform { Ox = ox, Oy = oy, Scale = scale, DocW = docW, DocH = docH };
    }
}
