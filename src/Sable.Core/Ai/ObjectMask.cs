namespace Sable.Core.Ai;

/// <summary>
/// One precomputed object region from SAM2 automatic mask generation (PHASE8_AI §8.3b, Affinity-style
/// hover-to-select). Single-channel coverage at a fixed working resolution + its area and the decoder's
/// IoU score (used for NMS dedupe + picking the most specific object under the cursor).
/// </summary>
public sealed record ObjectMask(byte[] Coverage, int Width, int Height, int Area, float Score,
    int Bx, int By, int Bw, int Bh)   // tight bounding box — lets IoU/NMS skip non-overlapping masks fast
{
    /// <summary>True if (x,y) — in this mask's working resolution — is inside the object.</summary>
    public bool Contains(int x, int y)
        => x >= Bx && y >= By && x < Bx + Bw && y < By + Bh && Coverage[y * Width + x] > 127;
}
