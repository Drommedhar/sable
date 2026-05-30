using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sable.App;

/// <summary>
/// Reusable adjustment/effect param row: a label over a <see cref="GradientSlider"/> + value box,
/// all two-way bound. Replaces the hand-rolled label+slider+TextBox Grid repeated across panels.
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

    public LabeledSlider() => InitializeComponent();
}
