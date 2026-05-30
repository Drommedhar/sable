using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Sable.App;

/// <summary>
/// Affinity-style slider: a static gradient track (e.g. hue rainbow, saturation
/// ramp, grey for levels) with a draggable thumb on top. The gradient is fixed —
/// only the thumb moves — unlike Fluent's value-fill slider. Two-way bindable
/// <see cref="Value"/> for VM wiring.
/// </summary>
public sealed class GradientSlider : Control
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<GradientSlider, double>(nameof(Minimum), 0);
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<GradientSlider, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<GradientSlider, double>(nameof(Value), 0,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<GradientSlider, IBrush?>(nameof(TrackBrush));

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    private bool _drag;

    public GradientSlider() { Height = 18; MinWidth = 80; }

    static GradientSlider()
    {
        AffectsRender<GradientSlider>(ValueProperty, MinimumProperty, MaximumProperty, TrackBrushProperty);
    }

    private const double TrackH = 8;
    private const double ThumbR = 6;

    private double Frac
    {
        get { double r = Maximum - Minimum; return r <= 1e-9 ? 0 : Math.Clamp((Value - Minimum) / r, 0, 1); }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _drag = true; e.Pointer.Capture(this); SetFromX(e.GetPosition(this).X);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag) SetFromX(e.GetPosition(this).X);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _drag = false; e.Pointer.Capture(null);
    }

    private void SetFromX(double x)
    {
        double w = Bounds.Width - 2 * ThumbR;
        if (w <= 0) return;
        double f = Math.Clamp((x - ThumbR) / w, 0, 1);
        Value = Minimum + f * (Maximum - Minimum);
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height, cy = h / 2;
        var track = new Rect(ThumbR, cy - TrackH / 2, Math.Max(0, w - 2 * ThumbR), TrackH);
        var brush = TrackBrush ?? new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
        ctx.DrawRectangle(brush, new Pen(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), 1),
                          new RoundedRect(track, 3));

        double tx = ThumbR + Frac * (w - 2 * ThumbR);
        var c = new Point(tx, cy);
        ctx.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), 1), c, ThumbR, ThumbR);
    }
}
