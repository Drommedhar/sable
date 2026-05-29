using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;

namespace Sable.App;

/// <summary>
/// Self-contained colour picker: Affinity-style hue ring + saturation/value triangle
/// (<see cref="ColorWheel"/>) plus a hex field and an H/S/V readout, kept in sync.
/// Reusable anywhere — exposes <see cref="Color"/>, <see cref="SetColor"/> (no event),
/// and <see cref="ColorChanged"/> (raised only on user interaction).
/// </summary>
public partial class ColorPicker : UserControl
{
    private bool _syncing;

    public event Action<Color>? ColorChanged;

    public ColorPicker()
    {
        InitializeComponent();
        Wheel.ColorChanged += OnWheelChanged;
    }

    public Color Color => Wheel.Color;

    /// <summary>Set the colour without raising <see cref="ColorChanged"/> (programmatic sync).</summary>
    public void SetColor(Color c)
    {
        Wheel.SetColor(c);
        Reflect(c);
    }

    private void OnWheelChanged(Color c)
    {
        Reflect(c);
        ColorChanged?.Invoke(c);
    }

    private void OnHexChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        var t = (HexBox.Text ?? "").TrimStart('#');
        if (t.Length is 6 or 8 && uint.TryParse(t, NumberStyles.HexNumber, null, out var v))
        {
            var c = Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
            Wheel.SetColor(c);
            Reflect(c);
            ColorChanged?.Invoke(c);
        }
    }

    private void Reflect(Color c)
    {
        _syncing = true;
        HexBox.Text = $"{c.R:X2}{c.G:X2}{c.B:X2}";
        var (h, s, v) = Wheel.Hsv;
        Readout.Text = $"H {h}  S {s}  V {v}";
        _syncing = false;
    }
}
