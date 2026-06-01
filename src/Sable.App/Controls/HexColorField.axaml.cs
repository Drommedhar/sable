using Avalonia;
using Avalonia.Controls;

namespace Sable.App;

/// <summary>Reusable colour field: a live swatch + a "RRGGBB" hex text box (two-way <see cref="Hex"/>).</summary>
public partial class HexColorField : UserControl
{
    public static readonly StyledProperty<string> HexProperty =
        AvaloniaProperty.Register<HexColorField, string>(nameof(Hex), "000000",
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public string Hex { get => GetValue(HexProperty); set => SetValue(HexProperty, value); }

    public HexColorField() => InitializeComponent();
}
