namespace Sable.Ai.Imaging;

/// <summary>
/// Pure pre/post-processing for ONNX models (PHASE8_AI §7): RGBA8 ↔ normalized CHW float tensors,
/// bilinear resize, and mask extraction. No ONNX/GPU dependency, so the math is unit-testable
/// against synthetic data independent of any model weights.
/// </summary>
public static class ImageOps
{
    /// <summary>Bilinear-resize an RGBA8 image. Identity when sizes match.</summary>
    public static byte[] ResizeRgba(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh) return (byte[])src.Clone();
        var dst = new byte[dw * dh * 4];
        Resize(src, sw, sh, 4, dst, dw, dh);
        return dst;
    }

    /// <summary>Bilinear-resize a single-channel (1 byte/px) image.</summary>
    public static byte[] ResizeGray(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh) return (byte[])src.Clone();
        var dst = new byte[dw * dh];
        Resize(src, sw, sh, 1, dst, dw, dh);
        return dst;
    }

    private static void Resize(byte[] src, int sw, int sh, int ch, byte[] dst, int dw, int dh)
    {
        // map dst pixel centres back into src space (half-pixel correct)
        double fx = (double)sw / dw, fy = (double)sh / dh;
        for (int y = 0; y < dh; y++)
        {
            double syf = (y + 0.5) * fy - 0.5;
            int y0 = (int)Math.Floor(syf);
            double wy = syf - y0;
            int y0c = Math.Clamp(y0, 0, sh - 1), y1c = Math.Clamp(y0 + 1, 0, sh - 1);
            for (int x = 0; x < dw; x++)
            {
                double sxf = (x + 0.5) * fx - 0.5;
                int x0 = (int)Math.Floor(sxf);
                double wx = sxf - x0;
                int x0c = Math.Clamp(x0, 0, sw - 1), x1c = Math.Clamp(x0 + 1, 0, sw - 1);
                int dBase = (y * dw + x) * ch;
                for (int c = 0; c < ch; c++)
                {
                    double v00 = src[(y0c * sw + x0c) * ch + c];
                    double v10 = src[(y0c * sw + x1c) * ch + c];
                    double v01 = src[(y1c * sw + x0c) * ch + c];
                    double v11 = src[(y1c * sw + x1c) * ch + c];
                    double top = v00 + (v10 - v00) * wx;
                    double bot = v01 + (v11 - v01) * wx;
                    dst[dBase + c] = (byte)Math.Clamp(top + (bot - top) * wy + 0.5, 0, 255);
                }
            }
        }
    }

    /// <summary>
    /// RGBA8 → CHW float tensor (length 3·w·h), channel-normalized `(px/255 - mean)/std`. Channel
    /// order is R,G,B (set <paramref name="bgr"/> for models that want B,G,R). Alpha is dropped.
    /// </summary>
    public static float[] ToChwFloat(byte[] rgba, int w, int h, ReadOnlySpan<float> mean, ReadOnlySpan<float> std, bool bgr = false)
    {
        var t = new float[3 * w * h];
        int plane = w * h;
        for (int i = 0; i < plane; i++)
        {
            float r = rgba[i * 4] / 255f, g = rgba[i * 4 + 1] / 255f, b = rgba[i * 4 + 2] / 255f;
            float c0 = bgr ? b : r, c2 = bgr ? r : b;
            t[i] = (c0 - mean[0]) / std[0];
            t[plane + i] = (g - mean[1]) / std[1];
            t[2 * plane + i] = (c2 - mean[2]) / std[2];
        }
        return t;
    }

    /// <summary>
    /// Model output (1·1·h·w, row-major) → single-channel coverage 0..255. Applies a sigmoid when the
    /// model emits logits; otherwise clamps the already-0..1 output.
    /// </summary>
    public static byte[] MaskFromFloat(float[] data, int w, int h, bool sigmoid)
    {
        var m = new byte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            float v = data[i];
            if (sigmoid) v = 1f / (1f + MathF.Exp(-v));
            m[i] = (byte)Math.Clamp(v * 255f + 0.5f, 0, 255);
        }
        return m;
    }

    /// <summary>Crop an RGBA8 sub-rect (clamped to the source); out-of-bounds pixels are transparent.</summary>
    public static byte[] Crop(byte[] src, int sw, int sh, int x, int y, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int ty = 0; ty < h; ty++)
        {
            int syy = y + ty;
            if (syy < 0 || syy >= sh) continue;
            for (int tx = 0; tx < w; tx++)
            {
                int sxx = x + tx;
                if (sxx < 0 || sxx >= sw) continue;
                int s = (syy * sw + sxx) * 4, d = (ty * w + tx) * 4;
                dst[d] = src[s]; dst[d + 1] = src[s + 1]; dst[d + 2] = src[s + 2]; dst[d + 3] = src[s + 3];
            }
        }
        return dst;
    }

    /// <summary>CHW float (3 planes, 0..1) → RGBA8 (opaque). Inverse of <see cref="ToChwFloat"/> with mean 0 / std 1.</summary>
    public static byte[] ChwFloatToRgba(float[] chw, int w, int h, bool bgr = false)
    {
        var rgba = new byte[w * h * 4];
        int plane = w * h;
        for (int i = 0; i < plane; i++)
        {
            float c0 = chw[i], c1 = chw[plane + i], c2 = chw[2 * plane + i];
            float r = bgr ? c2 : c0, b = bgr ? c0 : c2;
            rgba[i * 4] = Q(r); rgba[i * 4 + 1] = Q(c1); rgba[i * 4 + 2] = Q(b); rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    private static byte Q(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0, 255);

    /// <summary>Pack single-channel coverage into an RGBA8 layer mask (R=G=B=coverage, A=255).</summary>
    public static byte[] CoverageToRgbaMask(byte[] coverage, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte c = coverage[i];
            rgba[i * 4] = c; rgba[i * 4 + 1] = c; rgba[i * 4 + 2] = c; rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }
}
