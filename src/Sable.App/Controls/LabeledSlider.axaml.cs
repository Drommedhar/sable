using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Sable.Core;

namespace Sable.App;

/// <summary>
/// Reusable adjustment/effect param row: a label over a <see cref="GradientSlider"/> + value box,
/// all two-way bound. Replaces the hand-rolled label+slider+TextBox Grid repeated across panels.
/// Modern input affordances: the value box evaluates expressions ("512/2", "+10", "50%") on
/// Enter/blur, and dragging the label horizontally scrubs the value (Shift = fine).
/// </summary>
public partial class LabeledSlider : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<LabeledSlider, string>(nameof(Label), "");
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<LabeledSlider, double>(nameof(Minimum), 0);
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<LabeledSlider, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<LabeledSlider, double>(nameof(Value), 0,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<LabeledSlider, IBrush?>(nameof(TrackBrush));

    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    private bool _scrubbing;
    private double _scrubStartX, _scrubStartValue;

    public LabeledSlider()
    {
        InitializeComponent();

        Box.Text = FormatValue(Value);

        Box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitBox(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Box.Text = FormatValue(Value); e.Handled = true; }
        };
        Box.LostFocus += (_, _) => CommitBox();

        LabelText.PointerPressed += OnLabelPressed;
        LabelText.PointerMoved += OnLabelMoved;
        LabelText.PointerReleased += OnLabelReleased;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty && Box is not null && !Box.IsFocused)
            Box.Text = FormatValue(Value);
    }

    private static string FormatValue(double v) => v.ToString("0.##");

    /// <summary>Evaluate the box as an expression against the current value, clamp, apply.</summary>
    private void CommitBox()
    {
        if (NumericExpression.TryEval(Box.Text, Value, out var v))
            Value = Math.Clamp(v, Minimum, Maximum);
        Box.Text = FormatValue(Value);
    }

    private void OnLabelPressed(object? sender, PointerPressedEventArgs e)
    {
        _scrubbing = true;
        _scrubStartX = e.GetPosition(this).X;
        _scrubStartValue = Value;
        e.Pointer.Capture(LabelText);
        e.Handled = true;
    }

    private void OnLabelMoved(object? sender, PointerEventArgs e)
    {
        if (!_scrubbing) return;
        double dx = e.GetPosition(this).X - _scrubStartX;
        double perPx = (Maximum - Minimum) / 200.0;                       // full range ≈ 200 px
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) perPx *= 0.1;     // fine scrub
        Value = Math.Clamp(_scrubStartValue + dx * perPx, Minimum, Maximum);
    }

    private void OnLabelReleased(object? sender, PointerReleasedEventArgs e)
    {
        _scrubbing = false;
        e.Pointer.Capture(null);
    }
}
