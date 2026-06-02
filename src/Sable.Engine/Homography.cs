namespace Sable.Engine;

/// <summary>
/// 3×3 projective transform (perspective/distort, PLAN §16.9). Solves the homography mapping 4
/// document-space corners back to the layer's axis-aligned rect, so the compositor can sample each
/// output pixel: (lx,ly) = (h0·dx+h1·dy+h2, h3·dx+h4·dy+h5) / (h6·dx+h7·dy+1). Pure — unit-testable.
/// </summary>
public static class Homography
{
    /// <summary>
    /// Build the inverse (doc → layer) homography for a layer of size W×H whose 4 corners have been
    /// dragged to <paramref name="docCorners"/> (TL,TR,BR,BL, 8 floats). Returns the 6-float top rows
    /// in the compositor's [h0,h1,h3,h4,h2,h5] order + the perspective row [h6,h7,1].
    /// </summary>
    public static (float[] inv6, float[] perspRow) DocToLayerQuad(int width, int height, float[] docCorners)
    {
        // source = doc corners (dragged quad), dest = layer rect corners
        double[] px = { docCorners[0], docCorners[2], docCorners[4], docCorners[6] };
        double[] py = { docCorners[1], docCorners[3], docCorners[5], docCorners[7] };
        double[] qx = { 0, width, width, 0 };
        double[] qy = { 0, 0, height, height };

        var h = Solve(px, py, qx, qy);   // h0..h7 (h8 = 1)
        // degenerate quad (e.g. 3 collinear corners) → near-singular top 2×2; fall back to identity so the
        // layer renders at its natural rect instead of collapsing to a point.
        double det = h[0] * h[4] - h[1] * h[3];
        if (System.Math.Abs(det) < 1e-9 || double.IsNaN(det))
            return (new[] { 1f, 0f, 0f, 1f, 0f, 0f }, new[] { 0f, 0f, 1f });
        var inv6 = new[] { (float)h[0], (float)h[1], (float)h[3], (float)h[4], (float)h[2], (float)h[5] };
        var perspRow = new[] { (float)h[6], (float)h[7], 1f };
        return (inv6, perspRow);
    }

    /// <summary>Solve the 8 homography params from 4 point correspondences p→q (Gaussian elimination).</summary>
    private static double[] Solve(double[] px, double[] py, double[] qx, double[] qy)
    {
        // 8×8 system A·h = b, h = [h0..h7]
        var a = new double[8, 8];
        var b = new double[8];
        for (int i = 0; i < 4; i++)
        {
            int r = i * 2;
            // qx row: px*h0 + py*h1 + h2 - qx*px*h6 - qx*py*h7 = qx
            a[r, 0] = px[i]; a[r, 1] = py[i]; a[r, 2] = 1; a[r, 3] = 0; a[r, 4] = 0; a[r, 5] = 0;
            a[r, 6] = -qx[i] * px[i]; a[r, 7] = -qx[i] * py[i]; b[r] = qx[i];
            // qy row
            int r2 = r + 1;
            a[r2, 0] = 0; a[r2, 1] = 0; a[r2, 2] = 0; a[r2, 3] = px[i]; a[r2, 4] = py[i]; a[r2, 5] = 1;
            a[r2, 6] = -qy[i] * px[i]; a[r2, 7] = -qy[i] * py[i]; b[r2] = qy[i];
        }
        return GaussSolve(a, b);
    }

    private static double[] GaussSolve(double[,] a, double[] b)
    {
        int n = b.Length;
        for (int col = 0; col < n; col++)
        {
            // partial pivot
            int piv = col;
            double best = System.Math.Abs(a[col, col]);
            for (int r = col + 1; r < n; r++)
            {
                double v = System.Math.Abs(a[r, col]);
                if (v > best) { best = v; piv = r; }
            }
            if (best < 1e-12) continue;   // singular column → leave 0
            if (piv != col)
            {
                for (int c = 0; c < n; c++) (a[col, c], a[piv, c]) = (a[piv, c], a[col, c]);
                (b[col], b[piv]) = (b[piv], b[col]);
            }
            double diag = a[col, col];
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = a[r, col] / diag;
                if (f == 0) continue;
                for (int c = col; c < n; c++) a[r, c] -= f * a[col, c];
                b[r] -= f * b[col];
            }
        }
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = System.Math.Abs(a[i, i]) > 1e-12 ? b[i] / a[i, i] : 0;
        return x;
    }
}
