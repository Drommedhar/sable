using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sable.App;

/// <summary>
/// A document ruler (PLAN §2.5). Draws "nice"-stepped ticks + numeric labels mapped from the
/// canvas viewport (DIP-space origin + scale, supplied by the host so it lines up with the GPU
/// surface), plus a live cursor marker. Horizontal or vertical.
/// </summary>
public sealed class Ruler : Control
{
    public enum Orient { Horizontal, Vertical }

    public Orient Orientation { get; set; } = Orient.Horizontal;

    private double _origin;          // doc (0,0) position in this ruler's DIP space
    private double _scale = 1;       // DIP per doc px
    private double _cursor = double.NaN;
    private float[] _guides = Array.Empty<float>();

    /// <summary>Guide positions (doc coord along this axis) to mark on the ruler.</summary>
    public void SetGuides(float[] guides)
    {
        if (guides.Length == _guides.Length)
        {
            bool same = true;
            for (int i = 0; i < guides.Length; i++) if (guides[i] != _guides[i]) { same = false; break; }
            if (same) return;
        }
        _guides = guides;
        InvalidateVisual();
    }

    /// <summary>Set the viewport mapping (doc-origin + scale, both DIP). Triggers a redraw.</summary>
    public void SetView(double origin, double scale)
    {
        if (origin == _origin && scale == _scale) return;
        _origin = origin; _scale = scale <= 0 ? 1 : scale;
        InvalidateVisual();
    }

    /// <summary>Set the cursor's document coordinate along this ruler's axis (NaN = hide).</summary>
    public void SetCursor(double docPos)
    {
        if (docPos.Equals(_cursor)) return;
        _cursor = docPos;
        InvalidateVisual();
    }

    /// <summary>Raised when the ruler is clicked, with the document coordinate along its axis (new guide).</summary>
    public event Action<double>? GuideRequested;

    public Ruler()
    {
        // chrome colours come from the active theme variant → re-draw when the theme changes
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        double pos = Orientation == Orient.Horizontal ? p.X : p.Y;
        GuideRequested?.Invoke((pos - _origin) / _scale);
    }

    private IBrush Res(string key, IBrush fallback)
        => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    public override void Render(DrawingContext ctx)
    {
        bool horiz = Orientation == Orient.Horizontal;
        double len = horiz ? Bounds.Width : Bounds.Height;
        double thick = horiz ? Bounds.Height : Bounds.Width;
        if (len <= 0) return;

        var bg = Res("ChromePanel2", Brushes.Black);
        var line = Res("ChromeBorder", new SolidColorBrush(Color.FromArgb(255, 70, 70, 70)));
        var text = Res("ChromeTextDim", new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)));
        var cursorBrush = new SolidColorBrush(Color.FromArgb(255, 0, 160, 230));
        var pen = new Pen(line, 1);

        ctx.FillRectangle(bg, new Rect(Bounds.Size));

        // choose a "nice" step so a labelled major tick is ~70 DIP apart
        double targetPx = 70;
        double rawStep = targetPx / _scale;                 // doc px per major tick
        double step = NiceStep(rawStep);
        double minor = step / 5.0;

        // visible doc range
        double doc0 = (0 - _origin) / _scale;
        double doc1 = (len - _origin) / _scale;
        double start = Math.Floor(doc0 / minor) * minor;

        var typeface = new Typeface(FontFamily.Default);
        for (double d = start; d <= doc1; d += minor)
        {
            double p = _origin + d * _scale;
            if (p < -2 || p > len + 2) continue;
            bool major = Math.Abs(d / step - Math.Round(d / step)) < 1e-6;
            double tlen = major ? thick * 0.55 : thick * 0.28;

            if (horiz) ctx.DrawLine(pen, new Point(p, thick - tlen), new Point(p, thick));
            else ctx.DrawLine(pen, new Point(thick - tlen, p), new Point(thick, p));

            if (major)
            {
                var ft = new FormattedText(((int)Math.Round(d)).ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 9, text);
                if (horiz) ctx.DrawText(ft, new Point(p + 2, 1));
                else
                {
                    // vertical: draw the number rotated 90° next to the tick
                    using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(1, p - 2)))
                        ctx.DrawText(ft, new Point(0, 0));
                }
            }
        }

        // guide markers (cyan line across the ruler at each guide position)
        if (_guides.Length > 0)
        {
            var gp = new Pen(cursorBrush, 1);
            foreach (var g in _guides)
            {
                double p = _origin + g * _scale;
                if (p < 0 || p > len) continue;
                if (horiz) ctx.DrawLine(gp, new Point(p, 0), new Point(p, thick));
                else ctx.DrawLine(gp, new Point(0, p), new Point(thick, p));
            }
        }

        // cursor marker
        if (!double.IsNaN(_cursor))
        {
            double p = _origin + _cursor * _scale;
            if (p >= 0 && p <= len)
            {
                var cp = new Pen(cursorBrush, 1);
                if (horiz) ctx.DrawLine(cp, new Point(p, 0), new Point(p, thick));
                else ctx.DrawLine(cp, new Point(0, p), new Point(thick, p));
            }
        }
    }

    // round a raw step up to the nearest 1/2/5 × 10ⁿ
    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw)) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double nice = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
        return Math.Max(1, nice * mag);
    }
}
