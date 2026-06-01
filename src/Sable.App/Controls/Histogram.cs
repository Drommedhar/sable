using Avalonia;
using Avalonia.Media;

namespace Sable.App;

/// <summary>
/// 256-bin per-channel histogram of an RGBA8 buffer, plus a draw helper. Used as
/// the backdrop behind the Curves and Levels adjustment graphs (Affinity-style).
/// Layout: int[768] = R[0..255] · G[256..511] · B[512..767].
/// </summary>
public static class Histogram
{
    public static int[] Compute(byte[] rgba)
    {
        var bins = new int[768];
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] == 0) continue;               // skip transparent
            bins[rgba[i]]++;
            bins[256 + rgba[i + 1]]++;
            bins[512 + rgba[i + 2]]++;
        }
        return bins;
    }

    // channelMask bits: 1=R, 2=G, 4=B
    public static void Draw(DrawingContext ctx, Rect area, int[] bins, int channelMask)
    {
        if (bins.Length < 768 || area.Width < 2 || area.Height < 2) return;

        // peak across the enabled channels (sqrt-compressed so spikes don't flatten the rest)
        double peak = 1;
        for (int ch = 0; ch < 3; ch++)
        {
            if ((channelMask & (1 << ch)) == 0) continue;
            int b = ch * 256;
            for (int i = 0; i < 256; i++) peak = Math.Max(peak, bins[b + i]);
        }
        double inv = 1.0 / Math.Sqrt(peak);

        Color[] cols = { Color.FromArgb(0x88, 0xE0, 0x55, 0x55), Color.FromArgb(0x88, 0x55, 0xC0, 0x55), Color.FromArgb(0x88, 0x55, 0x88, 0xE0) };
        for (int ch = 0; ch < 3; ch++)
        {
            if ((channelMask & (1 << ch)) == 0) continue;
            int b = ch * 256;
            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                gc.BeginFigure(new Point(area.X, area.Bottom), true);
                for (int i = 0; i < 256; i++)
                {
                    double x = area.X + i / 255.0 * area.Width;
                    double hN = Math.Sqrt(bins[b + i]) * inv;            // 0..1
                    double y = area.Bottom - hN * area.Height;
                    gc.LineTo(new Point(x, y));
                }
                gc.LineTo(new Point(area.Right, area.Bottom));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(new SolidColorBrush(cols[ch]), null, geo);
        }
    }
}
