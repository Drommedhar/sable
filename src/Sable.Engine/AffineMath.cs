namespace Sable.Engine;

/// <summary>
/// Builds the inverse (document → layer) affine map for a layer's transform
/// (translate + scale + rotation about the layer centre). The compositor samples
/// each output pixel D as layer pixel L = m·D + b. Pure — unit-testable.
/// </summary>
public static class AffineMath
{
    /// <summary>
    /// Returns [m00,m01,m10,m11,b0,b1] such that
    /// (lx,ly) = (m00·dx+m01·dy+b0, m10·dx+m11·dy+b1).
    /// </summary>
    public static float[] DocToLayer(int width, int height,
        float tx, float ty, float scaleX, float scaleY, float rotationDeg)
    {
        float sx = MathF.Abs(scaleX) < 1e-3f ? 1e-3f * MathF.Sign(scaleX == 0 ? 1 : scaleX) : scaleX;
        float sy = MathF.Abs(scaleY) < 1e-3f ? 1e-3f * MathF.Sign(scaleY == 0 ? 1 : scaleY) : scaleY;
        float a = rotationDeg * MathF.PI / 180f;
        float c = MathF.Cos(a), s = MathF.Sin(a);

        // inverse of A = R·S  →  Ainv = [[c/sx, s/sx], [-s/sy, c/sy]]
        float m00 = c / sx, m01 = s / sx;
        float m10 = -s / sy, m11 = c / sy;

        // pivot = layer centre; b = C - Ainv·(T + C)
        float cx = width * 0.5f, cy = height * 0.5f;
        float px = tx + cx, py = ty + cy;
        float b0 = cx - (m00 * px + m01 * py);
        float b1 = cy - (m10 * px + m11 * py);

        return new[] { m00, m01, m10, m11, b0, b1 };
    }

    /// <summary>Map a layer-local point to document space (forward transform).</summary>
    public static (float x, float y) LayerToDoc(int width, int height,
        float tx, float ty, float scaleX, float scaleY, float rotationDeg, float lx, float ly)
    {
        float a = rotationDeg * MathF.PI / 180f;
        float c = MathF.Cos(a), s = MathF.Sin(a);
        float cx = width * 0.5f, cy = height * 0.5f;
        // A = R·S, applied to (L - C), then + T + C
        float qx = lx - cx, qy = ly - cy;
        float ax = c * scaleX * qx - s * scaleY * qy;
        float ay = s * scaleX * qx + c * scaleY * qy;
        return (tx + cx + ax, ty + cy + ay);
    }

    /// <summary>The layer's 4 transformed corners in document space: TL,TR,BR,BL (8 floats).</summary>
    public static float[] Corners(int width, int height,
        float tx, float ty, float scaleX, float scaleY, float rotationDeg)
    {
        var (x0, y0) = LayerToDoc(width, height, tx, ty, scaleX, scaleY, rotationDeg, 0, 0);
        var (x1, y1) = LayerToDoc(width, height, tx, ty, scaleX, scaleY, rotationDeg, width, 0);
        var (x2, y2) = LayerToDoc(width, height, tx, ty, scaleX, scaleY, rotationDeg, width, height);
        var (x3, y3) = LayerToDoc(width, height, tx, ty, scaleX, scaleY, rotationDeg, 0, height);
        return new[] { x0, y0, x1, y1, x2, y2, x3, y3 };
    }
}
