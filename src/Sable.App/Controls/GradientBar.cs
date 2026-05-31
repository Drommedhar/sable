using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Sable.Tools;
using ToolStop = Sable.Tools.GradientStop;   // disambiguate from Avalonia.Media.GradientStop

namespace Sable.App;

/// <summary>
/// Editable gradient strip: renders the gradient over a checkerboard (so alpha shows)
/// and draggable stop handles. Click a stop to select it, click empty bar to add one,
/// drag to reposition. The selected stop's colour is edited by the shared colour wheel
/// (see MainWindow). Mutates the bound <see cref="GradientDef"/> in place.
/// </summary>
public sealed class GradientBar : Control
{
    private const double BarH = 26;      // gradient strip height
    private const double HandleH = 12;   // handle row below the strip
    private const double HitPx = 7;

    private GradientDef _def = new(new ToolStop(0f, 0, 0, 0, 255), new ToolStop(1f, 255, 255, 255, 255));
    private bool _dragging;

    public GradientDef Def
    {
        get => _def;
        set { _def = value; Selected = 0; InvalidateVisual(); }
    }

    public int Selected { get; private set; }

    /// <summary>Raised when the gradient changes (stop moved/added/removed/recoloured).</summary>
    public event Action? Changed;
    /// <summary>Raised (stop index) when the selected stop changes — wire the colour wheel to it.</summary>
    public event Action<int>? StopSelected;

    public ToolStop SelectedStop =>
        Selected >= 0 && Selected < _def.Stops.Count ? _def.Stops[Selected] : default;

    /// <summary>Set the selected stop's colour (from the shared wheel).</summary>
    public void SetSelectedColor(byte r, byte g, byte b, byte a)
    {
        if (Selected < 0 || Selected >= _def.Stops.Count) return;
        var s = _def.Stops[Selected];
        s.R = r; s.G = g; s.B = b; s.A = a;
        _def.Stops[Selected] = s;
        InvalidateVisual();
        Changed?.Invoke();
    }

    public void AddStop()
    {
        // insert at the midpoint of the widest gap, colour sampled from there
        float pos = 0.5f;
        if (_def.Stops.Count >= 2)
        {
            float bestGap = -1; float bestPos = 0.5f;
            for (int i = 0; i < _def.Stops.Count - 1; i++)
            {
                float gap = _def.Stops[i + 1].Pos - _def.Stops[i].Pos;
                if (gap > bestGap) { bestGap = gap; bestPos = (_def.Stops[i].Pos + _def.Stops[i + 1].Pos) * 0.5f; }
            }
            pos = bestPos;
        }
        var (r, g, b, a) = _def.Sample(pos);
        _def.Stops.Add(new ToolStop(pos, r, g, b, a));
        _def.Sort();
        Selected = _def.Stops.FindIndex(s => s.Pos == pos);
        InvalidateVisual();
        Changed?.Invoke();
        StopSelected?.Invoke(Selected);
    }

    public void RemoveSelected()
    {
        if (_def.Stops.Count <= 2 || Selected < 0 || Selected >= _def.Stops.Count) return;
        _def.Stops.RemoveAt(Selected);
        Selected = Math.Clamp(Selected, 0, _def.Stops.Count - 1);
        InvalidateVisual();
        Changed?.Invoke();
        StopSelected?.Invoke(Selected);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        double w = Bounds.Width;
        // hit-test existing stops first
        for (int i = 0; i < _def.Stops.Count; i++)
        {
            double hx = _def.Stops[i].Pos * w;
            if (Math.Abs(p.X - hx) <= HitPx && p.Y >= BarH - 2)
            {
                Selected = i; _dragging = true;
                e.Pointer.Capture(this);
                InvalidateVisual();
                StopSelected?.Invoke(Selected);
                return;
            }
        }
        // empty bar → add a stop here
        if (p.Y <= BarH + HandleH)
        {
            float t = (float)Math.Clamp(p.X / Math.Max(1, w), 0, 1);
            var (r, g, b, a) = _def.Sample(t);
            _def.Stops.Add(new ToolStop(t, r, g, b, a));
            _def.Sort();
            Selected = _def.Stops.FindIndex(s => s.Pos == t);
            _dragging = true;
            e.Pointer.Capture(this);
            InvalidateVisual();
            Changed?.Invoke();
            StopSelected?.Invoke(Selected);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_dragging || Selected < 0 || Selected >= _def.Stops.Count) return;
        double w = Bounds.Width;
        float t = (float)Math.Clamp(e.GetPosition(this).X / Math.Max(1, w), 0, 1);
        var s = _def.Stops[Selected];
        s.Pos = t;
        _def.Stops[Selected] = s;
        // keep sorted; track the moved stop by identity of position
        _def.Sort();
        Selected = _def.Stops.FindIndex(x => x.Pos == t);
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width;
        var barRect = new Rect(0, 0, w, BarH);

        // checkerboard so alpha is visible
        for (int y = 0; y < BarH; y += 8)
        for (int x = 0; x < w; x += 8)
        {
            bool dark = ((x / 8) + (y / 8)) % 2 == 0;
            ctx.FillRectangle(new SolidColorBrush(dark ? Color.FromRgb(70, 70, 70) : Color.FromRgb(96, 96, 96)),
                new Rect(x, y, 8, Math.Min(8, BarH - y)));
        }

        // gradient fill
        var gs = new GradientStops();
        foreach (var s in _def.Stops)
            gs.Add(new Avalonia.Media.GradientStop(Color.FromArgb(s.A, s.R, s.G, s.B), s.Pos));
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = gs
        };
        ctx.FillRectangle(brush, barRect);
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(60, 60, 60))), barRect);

        // stop handles
        for (int i = 0; i < _def.Stops.Count; i++)
        {
            var s = _def.Stops[i];
            double hx = s.Pos * w;
            var fill = new SolidColorBrush(Color.FromRgb(s.R, s.G, s.B));
            var outline = new Pen(new SolidColorBrush(i == Selected ? Colors.White : Color.FromRgb(40, 40, 40)),
                i == Selected ? 2 : 1);
            var box = new Rect(hx - 5, BarH, 10, HandleH);
            ctx.FillRectangle(fill, box);
            ctx.DrawRectangle(null, outline, box);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(availableSize.Width, BarH + HandleH + 2);
}
