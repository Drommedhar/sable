using Sable.Core;
using Sable.Engine.Layers;

namespace Sable.Engine;

/// <summary>
/// The edited image (PLAN §4). A document is a graph, not a pixel buffer: the
/// on-screen image is always a recompute of <see cref="Layers"/> by the GPU
/// compositor. Layers are ordered bottom→top (index 0 is the backdrop).
/// </summary>
public sealed class Document
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Print resolution metadata (dots per inch). Does not affect pixel data.</summary>
    public double Dpi { get; set; } = 96;

    /// <summary>Working precision per channel (Image ▸ Mode). Metadata + IO today; the float editing
    /// pipeline lands incrementally (bit-depth milestone, PLAN §6).</summary>
    public BitDepth Depth { get; set; } = BitDepth.Eight;

    /// <summary>Embedded ICC colour profile (raw bytes) carried through import → edit → export so a
    /// colour-managed document keeps its profile (roadmap Workstream 5). Null = untagged (assume sRGB).
    /// Captured from PSD image-resource 1039; re-embedded on PNG (iCCP) / TIFF (tag 34675) export and
    /// persisted in <c>.sable</c>. The working pipeline stays linear-float sRGB; this is metadata
    /// preservation, not yet a conversion pipeline.</summary>
    public byte[]? IccProfile { get; set; }

    /// <summary>Human-readable profile name (e.g. "sRGB IEC61966-2.1"), for the title bar / report.
    /// Best-effort; may be null even when <see cref="IccProfile"/> is set.</summary>
    public string? IccProfileName { get; set; }

    /// <summary>A stored selection coverage mask (Select ▸ Save/Load Selection, PLAN §3). Doc-sized.</summary>
    public byte[]? SavedSelection { get; set; }

    /// <summary>Vertical guide lines (constant document X). PLAN §2.5.</summary>
    public List<float> GuidesX { get; } = new();
    /// <summary>Horizontal guide lines (constant document Y).</summary>
    public List<float> GuidesY { get; } = new();

    /// <summary>Change the canvas dimensions (crop/resize). Layer buffers are rebuilt by the caller/command.</summary>
    public void SetSize(int width, int height)
    {
        Width = width;
        Height = height;
        MarkStructureChanged();
    }

    /// <summary>Bottom→top. Index 0 composites first.</summary>
    public List<Layer> Layers { get; } = new();

    /// <summary>
    /// Active selection bounding box (doc px), or null = no selection (whole document).
    /// For a plain rectangular marquee this is the whole selection (grips editable).
    /// For ellipse/lasso/wand it is the bounding box of <see cref="SelectionMask"/>.
    /// </summary>
    public SelRect? Selection { get; set; }

    /// <summary>
    /// Per-pixel selection coverage (doc-sized, 255 = selected), or null for a plain
    /// rectangle / no selection. When set, editing ops clip to it; <see cref="Selection"/>
    /// holds its bounding box for the overlay.
    /// </summary>
    public byte[]? SelectionMask { get; set; }

    /// <summary>Bumped whenever the selection mask changes, so the canvas re-uploads its overlay texture.</summary>
    public int SelectionVersion { get; private set; }

    /// <summary>Clear any active selection (rect + mask).</summary>
    public void ClearSelection() { Selection = null; SelectionMask = null; SelectionVersion++; }

    /// <summary>The current selection as a coverage mask (rasterizing a plain rect), or null if none.</summary>
    public byte[]? SnapshotSelectionMask()
    {
        if (SelectionMask is not null) return (byte[])SelectionMask.Clone();
        if (Selection is { } r && r.W > 0 && r.H > 0) return Selections.Rect(Width, Height, r);
        return null;
    }

    /// <summary>
    /// Live-update the selection coverage mask during quick-mask painting: assigns the mask + bounds
    /// and bumps the version WITHOUT clearing on empty (so an in-progress empty mask isn't dropped).
    /// </summary>
    public void SetSelectionMaskLive(byte[] mask)
    {
        SelectionMask = mask;
        Selection = Selections.Bounds(mask, Width, Height);
        SelectionVersion++;
    }

    /// <summary>Set a non-rectangular selection from a coverage mask (computes its bounds).</summary>
    public void SetMaskSelection(byte[] mask)
    {
        var b = Selections.Bounds(mask, Width, Height);
        if (b.W <= 0 || b.H <= 0) { ClearSelection(); return; }
        SelectionMask = mask;
        Selection = b;
        SelectionVersion++;
    }

    public Document(int width, int height)
    {
        Width = width;
        Height = height;
    }

    private bool _structureDirty = true;

    /// <summary>True if any layer param changed or the layer set was added/removed/reordered.</summary>
    public bool NeedsComposite => _structureDirty || AnyDirty(Layers);

    private static bool AnyDirty(List<Layer> layers)
    {
        foreach (var l in layers)
        {
            if (l.Dirty) return true;
            if (AnyDirty(l.Children)) return true;
        }
        return false;
    }

    /// <summary>Flag a structural change (add/remove/reorder) so the compositor reruns.</summary>
    public void MarkStructureChanged() => _structureDirty = true;

    public void ClearDirty()
    {
        _structureDirty = false;
        ClearDirty(Layers);
    }

    private static void ClearDirty(List<Layer> layers)
    {
        foreach (var l in layers)
        {
            l.Dirty = false;
            ClearDirty(l.Children);
        }
    }

    /// <summary>The child-list that directly contains <paramref name="layer"/> (top-level or a group), or null.</summary>
    public List<Layer>? FindParent(Layer layer) => FindParent(Layers, layer);

    private static List<Layer>? FindParent(List<Layer> list, Layer layer)
    {
        if (list.Contains(layer)) return list;
        foreach (var l in list)
        {
            var found = FindParent(l.Children, layer);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// Demo document used by the M0/M1 canvas: a gradient backdrop, a
    /// semi-transparent red disc (Normal), and a bright spot (Screen) to exercise
    /// blend modes, opacity, and layer ordering through the real compositor.
    /// </summary>
    public static Document CreateDemo(int w = 512, int h = 512)
    {
        var doc = new Document(w, h);

        var bg = new PixelLayer(w, h, "Background") { BlendMode = BlendMode.Normal };
        var disc = new PixelLayer(w, h, "Red Disc") { BlendMode = BlendMode.Normal };
        var spot = new PixelLayer(w, h, "Highlight") { BlendMode = BlendMode.Screen, Opacity = 0.9f };

        double cx = w / 2.0, cy = h / 2.0, r = w * 0.30;
        double sx = w * 0.66, sy = h * 0.35, sr = w * 0.22;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;

            // backdrop: blue->green diagonal gradient, opaque
            bg.Pixels[i + 0] = 40;
            bg.Pixels[i + 1] = (byte)(255 * x / (double)w);
            bg.Pixels[i + 2] = (byte)(255 * y / (double)h);
            bg.Pixels[i + 3] = 255;

            // red disc, ~60% alpha inside
            double dx = x - cx, dy = y - cy;
            bool inDisc = dx * dx + dy * dy <= r * r;
            disc.Pixels[i + 0] = 230;
            disc.Pixels[i + 1] = 30;
            disc.Pixels[i + 2] = 30;
            disc.Pixels[i + 3] = inDisc ? (byte)153 : (byte)0;

            // soft bright spot (Screen) — radial falloff
            double ex = x - sx, ey = y - sy;
            double dist = Math.Sqrt(ex * ex + ey * ey);
            double a = Math.Clamp(1.0 - dist / sr, 0, 1);
            byte av = (byte)(a * 255);
            spot.Pixels[i + 0] = 250;
            spot.Pixels[i + 1] = 240;
            spot.Pixels[i + 2] = 210;
            spot.Pixels[i + 3] = av;
        }

        // mask the disc with a vertical gradient: top fully revealed, bottom hidden
        disc.AddWhiteMask(w, h);
        for (int y = 0; y < h; y++)
        {
            byte cov = (byte)(255 * (1.0 - y / (double)h));
            for (int x = 0; x < w; x++)
                disc.Mask![(y * w + x) * 4] = cov;   // R channel = mask coverage
        }

        doc.Layers.Add(bg);
        doc.Layers.Add(disc);
        doc.Layers.Add(spot);
        // non-destructive adjustment on top: boost contrast of everything below
        doc.Layers.Add(new AdjustmentLayer(AdjustmentKind.BrightnessContrast)
        {
            Contrast = 1.35f,
            Brightness = 0.02f
        });
        return doc;
    }
}
