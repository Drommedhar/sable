namespace Sable.Tools;

/// <summary>
/// Flood fill (bucket): from a seed pixel, replaces the contiguous region of
/// similar color with the fill color (scanline 4-connected). Operates on an RGBA8
/// buffer. Returns the count of pixels changed (0 = no-op).
/// </summary>
public static class FillTool
{
    // originX/originY = document position of the buffer's (0,0); clip + mask are document-space,
    // so a buffer pixel (x,y) maps to doc (x+originX, y+originY). 0 = buffer aligned to the document.
    public static int Flood(byte[] px, int w, int h, int sx, int sy,
        byte r, byte g, byte b, byte a, int tolerance = 32, (int X, int Y, int W, int H)? clip = null,
        byte[]? mask = null, int maskW = 0, int originX = 0, int originY = 0)
    {
        // restrict to clip (selection) bounds if given (converted from doc space to buffer space)
        int minX = 0, minY = 0, maxX = w - 1, maxY = h - 1;
        if (clip is { } c)
        {
            minX = Math.Max(0, c.X - originX); minY = Math.Max(0, c.Y - originY);
            maxX = Math.Min(w - 1, c.X + c.W - 1 - originX); maxY = Math.Min(h - 1, c.Y + c.H - 1 - originY);
        }
        if (sx < minX || sy < minY || sx > maxX || sy > maxY) return 0;
        if (maskW == 0) maskW = w;
        int seed = (sy * w + sx) * 4;
        byte sr = px[seed], sg = px[seed + 1], sb = px[seed + 2], sa = px[seed + 3];

        // already the fill color → nothing to do (only safe when not mask-clipped)
        if (mask is null && sr == r && sg == g && sb == b && sa == a) return 0;

        bool Match(int i) =>
            Math.Abs(px[i] - sr) <= tolerance &&
            Math.Abs(px[i + 1] - sg) <= tolerance &&
            Math.Abs(px[i + 2] - sb) <= tolerance &&
            Math.Abs(px[i + 3] - sa) <= tolerance;

        // visited array decouples traversal from the write (so a selection mask can
        // block the write without re-queuing the same matching pixel forever)
        var visited = new bool[w * h];
        int changed = 0;
        var stack = new Stack<(int x, int y)>();
        stack.Push((sx, sy));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            if (x < minX || x > maxX || y < minY || y > maxY) continue;
            int p = y * w + x;
            if (visited[p]) continue;
            visited[p] = true;
            int i = p * 4;
            if (!Match(i)) continue;

            int dmx = x + originX, dmy = y + originY;   // doc-space for the selection mask
            bool inMask = mask is null || (dmx >= 0 && dmy >= 0 && dmx < maskW && mask[dmy * maskW + dmx] != 0);
            if (inMask)
            {
                px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = a;
                changed++;
            }
            stack.Push((x - 1, y)); stack.Push((x + 1, y));
            stack.Push((x, y - 1)); stack.Push((x, y + 1));
        }
        return changed;
    }
}
