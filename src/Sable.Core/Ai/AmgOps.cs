namespace Sable.Core.Ai;

/// <summary>
/// Pure helpers for SAM2 automatic mask generation (PHASE8_AI §8.3b): the seed-point grid, mask IoU,
/// non-max suppression (dedupe overlapping object masks), and the hover lookup (smallest object under
/// the cursor). No ONNX — <see cref="Sable.Ai"/>'s Sam2Adapter runs the decoder; this is testable math.
/// </summary>
public static class AmgOps
{
    /// <summary>An n×n grid of seed points at cell centres, in [0,w)×[0,h) (document pixels).</summary>
    public static (float X, float Y)[] GridPoints(int w, int h, int n)
    {
        n = System.Math.Max(1, n);
        var pts = new (float, float)[n * n];
        for (int gy = 0; gy < n; gy++)
            for (int gx = 0; gx < n; gx++)
                pts[gy * n + gx] = ((gx + 0.5f) / n * w, (gy + 0.5f) / n * h);
        return pts;
    }

    /// <summary>
    /// Intersection-over-union of two same-resolution masks (coverage &gt; 127 = inside). Uses the
    /// bounding boxes to skip non-overlapping masks and to iterate only the overlap region — so NMS
    /// over a full grid of masks stays fast (the naive full-image version is O(n²·pixels)).
    /// </summary>
    public static float IoU(ObjectMask a, ObjectMask b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 0f;
        int w = a.Width;
        int x0 = System.Math.Max(a.Bx, b.Bx), y0 = System.Math.Max(a.By, b.By);
        int x1 = System.Math.Min(a.Bx + a.Bw, b.Bx + b.Bw), y1 = System.Math.Min(a.By + a.Bh, b.By + b.Bh);
        if (x0 >= x1 || y0 >= y1) return 0f;   // bounding boxes disjoint → no overlap

        int inter = 0;
        var ca = a.Coverage; var cb = b.Coverage;
        for (int y = y0; y < y1; y++)
        {
            int row = y * w;
            for (int x = x0; x < x1; x++)
                if (ca[row + x] > 127 && cb[row + x] > 127) inter++;
        }
        int union = a.Area + b.Area - inter;
        return union <= 0 ? 0f : (float)inter / union;
    }

    /// <summary>
    /// Greedy non-max suppression: keep masks by descending score, dropping any that overlap a kept
    /// mask above <paramref name="iouThresh"/>. Masks under <paramref name="minArea"/> are discarded.
    /// </summary>
    public static List<ObjectMask> Nms(IReadOnlyList<ObjectMask> masks, float iouThresh = 0.7f, int minArea = 1)
    {
        var sorted = masks.Where(m => m.Area >= minArea).OrderByDescending(m => m.Score).ToList();
        var kept = new List<ObjectMask>();
        foreach (var m in sorted)
        {
            bool dup = false;
            foreach (var k in kept) if (IoU(m, k) > iouThresh) { dup = true; break; }
            if (!dup) kept.Add(m);
        }
        return kept;
    }

    /// <summary>
    /// The object under (x,y): among masks containing the point, the SMALLEST area (most specific —
    /// so hovering a face inside a person picks the face if SAM produced it). Null if none.
    /// </summary>
    public static ObjectMask? BestAt(IReadOnlyList<ObjectMask> masks, int x, int y)
    {
        ObjectMask? best = null;
        foreach (var m in masks)
            if (m.Contains(x, y) && (best is null || m.Area < best.Area))
                best = m;
        return best;
    }
}
