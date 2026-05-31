namespace Sable.Tools;

/// <summary>A colour stop on a gradient: position 0..1 along the line + straight-alpha RGBA.</summary>
public struct GradientStop
{
    public float Pos;          // 0..1
    public byte R, G, B, A;
    public GradientStop(float pos, byte r, byte g, byte b, byte a) { Pos = pos; R = r; G = g; B = b; A = a; }
}

/// <summary>
/// An editable multi-stop gradient (PLAN §14.4 Gradient). Stops are kept sorted by
/// position; <see cref="Sample"/> linearly interpolates colour + alpha at any t. The
/// gradient tool samples this along the drag line.
/// </summary>
public sealed class GradientDef
{
    public List<GradientStop> Stops { get; } = new();

    /// <summary>Default gradient: opaque foreground at 0 → transparent (same colour) at 1.</summary>
    public static GradientDef ForegroundToTransparent(byte r, byte g, byte b)
        => new(new GradientStop(0f, r, g, b, 255), new GradientStop(1f, r, g, b, 0));

    public GradientDef() { }
    public GradientDef(params GradientStop[] stops) { Stops.AddRange(stops); Sort(); }

    public void Sort() => Stops.Sort((a, b) => a.Pos.CompareTo(b.Pos));

    /// <summary>Interpolated RGBA at <paramref name="t"/> (0..1). Clamps outside the stop range.</summary>
    public (byte r, byte g, byte b, byte a) Sample(float t)
    {
        if (Stops.Count == 0) return (0, 0, 0, 0);
        if (Stops.Count == 1) { var s = Stops[0]; return (s.R, s.G, s.B, s.A); }

        if (t <= Stops[0].Pos) { var s = Stops[0]; return (s.R, s.G, s.B, s.A); }
        var last = Stops[^1];
        if (t >= last.Pos) return (last.R, last.G, last.B, last.A);

        for (int i = 0; i < Stops.Count - 1; i++)
        {
            var a = Stops[i]; var b = Stops[i + 1];
            if (t >= a.Pos && t <= b.Pos)
            {
                float span = b.Pos - a.Pos;
                float f = span > 1e-6f ? (t - a.Pos) / span : 0f;
                return (Lerp(a.R, b.R, f), Lerp(a.G, b.G, f), Lerp(a.B, b.B, f), Lerp(a.A, b.A, f));
            }
        }
        return (last.R, last.G, last.B, last.A);
    }

    private static byte Lerp(byte a, byte b, float f) => (byte)(a + (b - a) * f + 0.5f);
}
