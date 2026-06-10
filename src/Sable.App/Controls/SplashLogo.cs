using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Sable.App.Controls;

/// <summary>
/// Splash logo animation: the brush-stroke logo "paints itself in" stroke by stroke — the PNG is
/// segmented into connected alpha components at startup (each component = one brush stroke), and
/// each stroke wipes in quickly with a staggered start, head to tail, like a brush drawing them.
/// A light band then shimmers across the fresh paint once. Bespoke control because XAML keyframe
/// animations cannot animate gradient-stop offsets.
/// </summary>
public sealed class SplashLogo : Control
{
    // timeline (seconds)
    private const double PaintEnd = 1.0;
    private const double ShimmerEnd = 1.8;
    private const double SettleEnd = 1.8;   // short hold after the shimmer, then hand off

    private const double StrokeDur = 0.45;  // how long one stroke takes to draw in
    private const double Feather = 0.22;    // soft-edge width of the fallback whole-logo wipe
    private const double BandWidth = 0.18;  // shimmer band half-width (fraction of the sweep)
    private const int MaxStrokes = 40;      // merge surplus components into the nearest kept stroke
    private const int MinPixels = 25;       // components smaller than this merge into a neighbour

    private readonly Bitmap _logo;
    private readonly ImageBrush _strokeMask;   // logo alpha as a mask: keeps the shimmer inside the strokes
    private readonly StrokePart[]? _parts;     // null = segmentation failed -> whole-logo wipe fallback
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;
    private readonly System.Threading.Tasks.TaskCompletionSource _done = new();

    // Animation time advances by CAPPED frame deltas, not wall clock: if the UI thread stalls
    // (e.g. a heavy window ctor), the animation pauses instead of skipping to the end.
    private double _t;
    private double _lastElapsed;

    /// <summary>One brush stroke: its own cropped bitmap + bbox as fractions of the logo + draw window.</summary>
    private sealed class StrokePart
    {
        public WriteableBitmap Bmp = null!;
        public double Nx, Ny, Nw, Nh;   // bbox normalized to logo size
        public double Start, End;       // animation-time window
    }

    /// <summary>Completes when the intro timeline has fully played (frame-time, not wall time).</summary>
    public System.Threading.Tasks.Task IntroDone => _done.Task;

    public SplashLogo()
    {
        _logo = new Bitmap(AssetLoader.Open(new Uri("avares://Sable.App/Assets/logo_only.png")));
        _strokeMask = new ImageBrush(_logo) { Stretch = Stretch.Fill };
        _parts = TrySplitStrokes();
        // Render priority: at the default Background priority the timer is starved behind layout
        // and the animation stutters (same lesson as the canvas render loop).
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            var now = _clock.Elapsed.TotalSeconds;
            _t += Math.Min(now - _lastElapsed, 0.033);
            _lastElapsed = now;
            if (_t > SettleEnd)
            {
                _timer!.Stop();
                _done.TrySetResult();
            }
            InvalidateVisual();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _clock.Start();
        _lastElapsed = _clock.Elapsed.TotalSeconds;
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext ctx)
    {
        var dest = DestRect();
        if (dest.Width <= 0 || dest.Height <= 0)
            return;
        var t = _t;
        var src = new Rect(_logo.Size);

        // scale + drift about the logo centre: zoom-in while painting, then rest at 1:1
        double scale = 1.0, driftY = 0;
        if (t < PaintEnd)
        {
            var p = EaseInOutCubic(t / PaintEnd);
            scale = 0.94 + 0.06 * p;
            driftY = 10 * (1 - p);
        }

        var c = dest.Center;
        var m = Matrix.CreateTranslation(-c.X, -c.Y)
              * Matrix.CreateScale(scale, scale)
              * Matrix.CreateTranslation(c.X, c.Y + driftY);
        using var xf = ctx.PushTransform(m);

        if (t < PaintEnd)
        {
            if (_parts is { } parts)
            {
                // phase 1: each stroke wipes in over its own bbox, staggered head -> tail
                foreach (var part in parts)
                {
                    var q = (t - part.Start) / (part.End - part.Start);
                    if (q <= 0)
                        continue;
                    var dr = new Rect(
                        dest.X + part.Nx * dest.Width,
                        dest.Y + part.Ny * dest.Height,
                        part.Nw * dest.Width,
                        part.Nh * dest.Height);
                    if (q >= 1)
                    {
                        ctx.DrawImage(part.Bmp, new Rect(part.Bmp.Size), dr);
                    }
                    else
                    {
                        using var mask = ctx.PushOpacityMask(StrokeWipe(EaseOutQuad(q)), dr);
                        ctx.DrawImage(part.Bmp, new Rect(part.Bmp.Size), dr);
                    }
                }
            }
            else
            {
                // fallback: single soft wipe across the whole logo
                using var mask = ctx.PushOpacityMask(WipeMask(EaseInOutCubic(t / PaintEnd)), dest);
                ctx.DrawImage(_logo, src, dest);
            }
            return;
        }

        ctx.DrawImage(_logo, src, dest);

        if (t < ShimmerEnd)
        {
            // phase 2: shimmer — a white gradient band sweeps across, clipped to the strokes by the
            // logo's own alpha (ImageBrush opacity mask). NOTE: re-drawing the logo over itself is a
            // no-op (src-over of identical colours), so the highlight must be a separate white fill.
            var p = EaseInOutSine((t - PaintEnd) / (ShimmerEnd - PaintEnd));
            using var mask = ctx.PushOpacityMask(_strokeMask, dest);
            ctx.FillRectangle(BandBrush(p), dest);
        }
    }

    /// <summary>
    /// Segment the logo PNG into brush strokes: flood-fill connected components on alpha (8-way),
    /// merge specks and surplus components into their nearest neighbour, order left to right
    /// (head to tail), and bake each into a bbox-cropped bitmap with a staggered time window.
    /// Returns null on any failure (caller falls back to the whole-logo wipe).
    /// </summary>
    private unsafe StrokePart[]? TrySplitStrokes()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Sable.App/Assets/logo_only.png"));
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            if (Sable.Imaging.ImageCodec.DecodeRgbaBytes(ms.ToArray()) is not { } img)
                return null;
            var (w, h, rgba) = img;

            // connected components over alpha >= 16
            var comp = new int[w * h];
            var comps = new List<List<int>>();
            var stack = new Stack<int>();
            for (int i = 0; i < w * h; i++)
            {
                if (comp[i] != 0 || rgba[i * 4 + 3] < 16)
                    continue;
                var px = new List<int>();
                comp[i] = comps.Count + 1;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    var p = stack.Pop();
                    px.Add(p);
                    int x = p % w, y = p / w;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                                continue;
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                                continue;
                            int n = ny * w + nx;
                            if (comp[n] != 0 || rgba[n * 4 + 3] < 16)
                                continue;
                            comp[n] = comps.Count + 1;
                            stack.Push(n);
                        }
                }
                comps.Add(px);
            }
            if (comps.Count == 0)
                return null;

            // keep the biggest components as strokes; merge specks + surplus into the nearest kept one
            var keep = comps.Where(p => p.Count >= MinPixels)
                            .OrderByDescending(p => p.Count)
                            .Take(MaxStrokes)
                            .ToList();
            if (keep.Count == 0)
                keep.Add(comps.OrderByDescending(p => p.Count).First());
            foreach (var rest in comps.Where(p => !keep.Contains(p)))
            {
                var (cx, cy) = Centroid(rest, w);
                List<int>? best = null;
                double bestD = double.MaxValue;
                foreach (var k in keep)
                {
                    var (kx, ky) = Centroid(k, w);
                    var d = (kx - cx) * (kx - cx) + (ky - cy) * (ky - cy);
                    if (d < bestD) { bestD = d; best = k; }
                }
                best!.AddRange(rest);
            }

            // head (left) paints first
            keep.Sort((a, b) => Centroid(a, w).cx.CompareTo(Centroid(b, w).cx));

            var stagger = keep.Count > 1 ? (PaintEnd - StrokeDur) / (keep.Count - 1) : 0;
            var parts = new StrokePart[keep.Count];
            for (int i = 0; i < keep.Count; i++)
            {
                var px = keep[i];
                int minX = w, minY = h, maxX = 0, maxY = 0;
                foreach (var p in px)
                {
                    int x = p % w, y = p / w;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
                int bw = maxX - minX + 1, bh = maxY - minY + 1;

                var bmp = new WriteableBitmap(new PixelSize(bw, bh), new Vector(96, 96),
                    PixelFormats.Rgba8888, AlphaFormat.Unpremul);
                using (var fb = bmp.Lock())
                {
                    new Span<byte>((void*)fb.Address, fb.RowBytes * bh).Clear();
                    var basePtr = (byte*)fb.Address;
                    foreach (var p in px)
                    {
                        int x = p % w, y = p / w;
                        var dst = basePtr + (y - minY) * fb.RowBytes + (x - minX) * 4;
                        var s = p * 4;
                        dst[0] = rgba[s];
                        dst[1] = rgba[s + 1];
                        dst[2] = rgba[s + 2];
                        dst[3] = rgba[s + 3];
                    }
                }

                parts[i] = new StrokePart
                {
                    Bmp = bmp,
                    Nx = minX / (double)w, Ny = minY / (double)h,
                    Nw = bw / (double)w, Nh = bh / (double)h,
                    Start = i * stagger,
                    End = i * stagger + StrokeDur,
                };
            }
            return parts;
        }
        catch
        {
            return null;
        }
    }

    private static (double cx, double cy) Centroid(List<int> px, int w)
    {
        double sx = 0, sy = 0;
        foreach (var p in px) { sx += p % w; sy += p / w; }
        return (sx / px.Count, sy / px.Count);
    }

    /// <summary>Aspect-fit the logo into the control with a small margin.</summary>
    private Rect DestRect()
    {
        var b = Bounds.Size;
        var s = _logo.Size;
        if (s.Width <= 0 || b.Width <= 0 || b.Height <= 0)
            return default;
        var k = Math.Min(b.Width / s.Width, b.Height / s.Height) * 0.9;
        var w = s.Width * k;
        var h = s.Height * k;
        return new Rect((b.Width - w) / 2, (b.Height - h) / 2, w, h);
    }

    /// <summary>Per-stroke wipe, left to right across the stroke's own bbox.</summary>
    private static IBrush StrokeWipe(double p)
    {
        const double f = 0.35;   // fat feather: short strokes need a soft leading edge
        var e = p * (1 + f);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(Colors.White, Math.Clamp(e - f, 0, 1)),
                new GradientStop(Colors.Transparent, Math.Clamp(e, 0, 1)),
                new GradientStop(Colors.Transparent, 1),
            },
        };
    }

    /// <summary>Diagonal wipe mask, bottom-left (head) to top-right (tail): opaque behind a soft edge at progress p.</summary>
    private static IBrush WipeMask(double p)
    {
        var e = p * (1 + Feather);   // run past 1 so the feather fully clears the logo
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(Colors.White, Math.Clamp(e - Feather, 0, 1)),
                new GradientStop(Colors.Transparent, Math.Clamp(e, 0, 1)),
                new GradientStop(Colors.Transparent, 1),
            },
        };
    }

    /// <summary>Narrow white band sweeping head to tail; peak alpha = shimmer strength.</summary>
    private static IBrush BandBrush(double p)
    {
        var centre = -BandWidth + p * (1 + 2 * BandWidth);
        var peak = Color.FromArgb(140, 255, 255, 255);   // ~55% — light catching the wet paint
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Colors.Transparent, Math.Clamp(centre - BandWidth, 0, 1)),
                new GradientStop(peak, Math.Clamp(centre, 0, 1)),
                new GradientStop(Colors.Transparent, Math.Clamp(centre + BandWidth, 0, 1)),
                new GradientStop(Colors.Transparent, 1),
            },
        };
    }

    private static double EaseInOutCubic(double x) =>
        x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;

    private static double EaseInOutSine(double x) =>
        -(Math.Cos(Math.PI * x) - 1) / 2;

    private static double EaseOutQuad(double x) =>
        1 - (1 - x) * (1 - x);
}
