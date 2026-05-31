namespace Sable.Engine;

/// <summary>
/// Builds the layer transform A = R·S·K (rotation · scale · shear) about the layer centre, and
/// its inverse (document → layer) map used by the compositor: each output pixel D samples layer
/// pixel L = m·D + b. Pure — unit-testable.
/// </summary>
public static class AffineMath
{
    /// <summary>The forward 2×2 matrix A = R·S·K (layer→doc, about centre), row-major [a00,a01,a10,a11].</summary>
    private static (float a00, float a01, float a10, float a11) Forward2x2(
        float scaleX, float scaleY, float rotationDeg, float shearX, float shearY)
    {
        float sx = MathF.Abs(scaleX) < 1e-3f ? 1e-3f * (scaleX < 0 ? -1 : 1) : scaleX;
        float sy = MathF.Abs(scaleY) < 1e-3f ? 1e-3f * (scaleY < 0 ? -1 : 1) : scaleY;
        float a = rotationDeg * MathF.PI / 180f;
        float c = MathF.Cos(a), s = MathF.Sin(a);
        // S·K = [[sx, sx·shx],[sy·shy, sy]]; A = R·(S·K)
        float a00 = c * sx - s * sy * shearY;
        float a01 = c * sx * shearX - s * sy;
        float a10 = s * sx + c * sy * shearY;
        float a11 = s * sx * shearX + c * sy;
        return (a00, a01, a10, a11);
    }

    /// <summary>
    /// Returns [m00,m01,m10,m11,b0,b1] (inverse, doc→layer) such that
    /// (lx,ly) = (m00·dx+m01·dy+b0, m10·dx+m11·dy+b1).
    /// </summary>
    public static float[] DocToLayer(int width, int height,
        float tx, float ty, float scaleX, float scaleY, float rotationDeg, float shearX = 0, float shearY = 0)
    {
        var (a00, a01, a10, a11) = Forward2x2(scaleX, scaleY, rotationDeg, shearX, shearY);
        float det = a00 * a11 - a01 * a10;
        if (MathF.Abs(det) < 1e-9f) det = det < 0 ? -1e-9f : 1e-9f;
        float inv = 1f / det;
        // Ainv (doc→layer 2×2)
        float m00 = a11 * inv, m01 = -a01 * inv;
        float m10 = -a10 * inv, m11 = a00 * inv;

        // pivot = layer centre; b = C - Ainv·(T + C)
        float cx = width * 0.5f, cy = height * 0.5f;
        float px = tx + cx, py = ty + cy;
        float b0 = cx - (m00 * px + m01 * py);
        float b1 = cy - (m10 * px + m11 * py);
        return new[] { m00, m01, m10, m11, b0, b1 };
    }

    /// <summary>Map a layer-local point to document space (forward transform).</summary>
    public static (float x, float y) LayerToDoc(int width, int height,
        float tx, float ty, float scaleX, float scaleY, float rotationDeg, float lx, float ly,
        float shearX = 0, float shearY = 0)
    {
        var (a00, a01, a10, a11) = Forward2x2(scaleX, scaleY, rotationDeg, shearX, shearY);
        float cx = width * 0.5f, cy = height * 0.5f;
        float qx = lx - cx, qy = ly - cy;
        float ax = a00 * qx + a01 * qy;
        float ay = a10 * qx + a11 * qy;
        return (tx + cx + ax, ty + cy + ay);
    }

    /// <summary>The layer's 4 transformed corners in document space: TL,TR,BR,BL (8 floats).</summary>
    public static float[] Corners(int width, int height,
        float tx, float ty, float scaleX, float scaleY, float rotationDeg, float shearX = 0, float shearY = 0)
    {
        var (x0, y0) = LayerToDoc(width, height, tx, ty, scaleX, scaleY, rotationDeg, 0, 0, shearX, shearY);
        var (x1, y1) = LayerToDoc(width, height, tx, ty, scaleX, scaleY, rotationDeg, width, 0, shearX, shearY);
        var (x2, y2) = LayerToDoc(width, height, tx, ty, scaleX, scaleY, rotationDeg, width, height, shearX, shearY);
        var (x3, y3) = LayerToDoc(width, height, tx, ty, scaleX, scaleY, rotationDeg, 0, height, shearX, shearY);
        return new[] { x0, y0, x1, y1, x2, y2, x3, y3 };
    }
}
