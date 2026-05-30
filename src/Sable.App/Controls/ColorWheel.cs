using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;

namespace Sable.App;

/// <summary>
/// Affinity-style colour picker: an outer hue ring + an inner saturation/value
/// triangle that rotates with the hue (PLAN §13.3, bespoke pro control). Drag the
/// ring to set hue, drag the triangle for saturation/value. Raises
/// <see cref="ColorChanged"/> on user interaction (not on programmatic <see cref="Color"/> set).
/// </summary>
public sealed class ColorWheel : Control
{
    private double _h;          // 0..360
    private double _s = 1, _v = 1;
    private WriteableBitmap? _ring;     // static hue annulus
    private WriteableBitmap? _tri;      // sat/val triangle (regenerated per hue)
    private int _bmpSize;               // pixel size the bitmaps were built at
    private bool _triDirty = true;
    private bool _dragRing, _dragTri;

    public event Action<Color>? ColorChanged;

    public ColorWheel() { MinWidth = 120; MinHeight = 120; }

    // square: size to the available width so there's no horizontal empty space
    protected override Size MeasureOverride(Size available)
    {
        double s = double.IsInfinity(available.Width) ? 200 : Math.Max(MinWidth, available.Width);
        return new Size(s, s);
    }

    public Color Color
    {
        get { var (r, g, b) = HsvToRgb(_h, _s, _v); return Color.FromRgb(r, g, b); }
    }

    /// <summary>Set the displayed colour without raising <see cref="ColorChanged"/> (programmatic sync).</summary>
    public void SetColor(Color c)
    {
        var (h, s, v) = RgbToHsv(c.R, c.G, c.B);
        _h = h; _s = s; _v = v; _triDirty = true;
        InvalidateVisual();
    }

    public (int H, int S, int V) Hsv => ((int)Math.Round(_h), (int)Math.Round(_s * 100), (int)Math.Round(_v * 100));

    // --- geometry -------------------------------------------------------------
    private double Size => Math.Min(Bounds.Width, Bounds.Height);
    private Point Center => new(Bounds.Width / 2, Bounds.Height / 2);
    private double ROuter => Size / 2 - 2;
    private double RInner => ROuter * 0.74;

    private (Point a, Point b, Point c) TrianglePoints()
    {
        double r = RInner - 2;
        Point P(double deg) { double a = deg * Math.PI / 180; return Center + new Vector(Math.Cos(a) * r, Math.Sin(a) * r); }
        return (P(_h), P(_h + 120), P(_h + 240));   // hue, white, black
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        double dvx = p.X - Center.X, dvy = p.Y - Center.Y, d = Math.Sqrt(dvx * dvx + dvy * dvy);
        if (d <= ROuter && d >= RInner) { _dragRing = true; e.Pointer.Capture(this); SetHueFrom(p); }
        else { _dragTri = true; e.Pointer.Capture(this); SetSvFrom(p); }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_dragRing && !_dragTri) return;
        var p = e.GetPosition(this);
        if (_dragRing) SetHueFrom(p); else SetSvFrom(p);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    { _dragRing = _dragTri = false; e.Pointer.Capture(null); }

    private void SetHueFrom(Point p)
    {
        var v = p - Center;
        _h = (Math.Atan2(v.Y, v.X) * 180 / Math.PI + 360) % 360;
        _triDirty = true; InvalidateVisual();
        ColorChanged?.Invoke(Color);
    }

    private void SetSvFrom(Point p)
    {
        var (a, b, c) = TrianglePoints();
        var (wa, wb, wc) = Bary(p, a, b, c);
        wa = Math.Max(0, wa); wb = Math.Max(0, wb); wc = Math.Max(0, wc);
        double sum = wa + wb + wc; if (sum < 1e-6) return;
        wa /= sum; wb /= sum; wc /= sum;
        _v = Math.Clamp(wa + wb, 0, 1);
        _s = _v > 1e-4 ? Math.Clamp(wa / _v, 0, 1) : 0;
        InvalidateVisual();
        ColorChanged?.Invoke(Color);
    }

    // --- rendering ------------------------------------------------------------
    public override void Render(DrawingContext ctx)
    {
        int sz = (int)Size;
        if (sz < 8) return;
        if (_ring is null || _bmpSize != sz) { _bmpSize = sz; BuildRing(sz); _triDirty = true; }
        if (_tri is null || _triDirty) { BuildTriangle(sz); _triDirty = false; }

        var origin = new Point(Center.X - sz / 2.0, Center.Y - sz / 2.0);
        var dest = new Rect(origin, new Size(sz, sz));
        ctx.DrawImage(_ring!, dest);
        ctx.DrawImage(_tri!, dest);

        // hue marker
        double mid = (RInner + ROuter) / 2, ha = _h * Math.PI / 180;
        var hp = Center + new Vector(Math.Cos(ha) * mid, Math.Sin(ha) * mid);
        ctx.DrawEllipse(null, new Pen(Brushes.White, 2), hp, 6, 6);
        ctx.DrawEllipse(null, new Pen(Brushes.Black, 1), hp, 7, 7);

        // sat/val marker
        var (a, b, c) = TrianglePoints();
        var sp = new Point(a.X * (_v * _s) + b.X * (_v * (1 - _s)) + c.X * (1 - _v),
                           a.Y * (_v * _s) + b.Y * (_v * (1 - _s)) + c.Y * (1 - _v));
        var (rr, gg, bb) = HsvToRgb(_h, _s, _v);
        ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(rr, gg, bb)), new Pen(Brushes.White, 2), sp, 5, 5);
    }

    private void BuildRing(int sz)
    {
        var bmp = new WriteableBitmap(new PixelSize(sz, sz), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var px = new byte[sz * sz * 4];
        double cx = sz / 2.0, cy = sz / 2.0, ro = sz / 2.0 - 2, ri = ro * 0.74;
        for (int y = 0; y < sz; y++)
        for (int x = 0; x < sz; x++)
        {
            double dx = x + 0.5 - cx, dy = y + 0.5 - cy, d = Math.Sqrt(dx * dx + dy * dy);
            int o = (y * sz + x) * 4;
            if (d <= ro && d >= ri)
            {
                double hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                var (r, g, b) = HsvToRgb(hue, 1, 1);
                double aa = Smooth(d, ro) * Smooth2(d, ri);   // antialias edges
                byte al = (byte)(aa * 255);
                px[o] = (byte)(b * aa); px[o + 1] = (byte)(g * aa); px[o + 2] = (byte)(r * aa); px[o + 3] = al;
            }
        }
        Copy(bmp, px); _ring = bmp;
    }

    private void BuildTriangle(int sz)
    {
        var bmp = new WriteableBitmap(new PixelSize(sz, sz), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var px = new byte[sz * sz * 4];
        double r = (sz / 2.0 * 0.74) - 2, cx = sz / 2.0, cy = sz / 2.0;
        Point Pt(double deg) { double a = deg * Math.PI / 180; return new Point(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r); }
        var a3 = Pt(_h); var b3 = Pt(_h + 120); var c3 = Pt(_h + 240);
        var (hr, hg, hb) = HsvToRgb(_h, 1, 1);
        for (int y = 0; y < sz; y++)
        for (int x = 0; x < sz; x++)
        {
            var (wa, wb, wc) = Bary(new Point(x + 0.5, y + 0.5), a3, b3, c3);
            if (wa < -0.01 || wb < -0.01 || wc < -0.01) continue;
            wa = Math.Max(0, wa); wb = Math.Max(0, wb); wc = Math.Max(0, wc);
            double r2 = hr * wa + 255 * wb, g2 = hg * wa + 255 * wb, b2 = hb * wa + 255 * wb;   // hue + white (+ black=0)
            int o = (y * sz + x) * 4;
            px[o] = (byte)Math.Clamp(b2, 0, 255); px[o + 1] = (byte)Math.Clamp(g2, 0, 255);
            px[o + 2] = (byte)Math.Clamp(r2, 0, 255); px[o + 3] = 255;
        }
        Copy(bmp, px); _tri = bmp;
    }

    private static void Copy(WriteableBitmap bmp, byte[] px)
    {
        using var fb = bmp.Lock();
        int stride = fb.RowBytes, w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
        if (stride == w * 4) Marshal.Copy(px, 0, fb.Address, px.Length);
        else for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, fb.Address + y * stride, w * 4);
    }

    private static double Smooth(double d, double r) => Math.Clamp(r - d, 0, 1);
    private static double Smooth2(double d, double r) => Math.Clamp(d - r, 0, 1);

    private static (double, double, double) Bary(Point p, Point a, Point b, Point c)
    {
        double det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (Math.Abs(det) < 1e-9) return (-1, -1, -1);
        double wa = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / det;
        double wb = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / det;
        return (wa, wb, 1 - wa - wb);
    }

    // --- colour conversion ----------------------------------------------------
    private static (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return ((byte)((r + m) * 255 + 0.5), (byte)((g + m) * 255 + 0.5), (byte)((b + m) * 255 + 0.5));
    }

    private static (double h, double s, double v) RgbToHsv(byte rb, byte gb, byte bb)
    {
        double r = rb / 255.0, g = gb / 255.0, b = bb / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 1e-6)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max < 1e-6 ? 0 : d / max, max);
    }
}
