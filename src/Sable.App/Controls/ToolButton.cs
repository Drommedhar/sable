using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sable.App;

/// <summary>
/// Reusable tool-strip / icon button (PLAN §13.3, CLAUDE UI conventions). Renders a Lucide-style
/// path icon inside a fixed 24×24 frame scaled by a Viewbox, so every icon shares the same
/// coordinate system and lands the same size + centred (plain `Path Stretch=Uniform` fits each
/// icon's own bbox, which makes asymmetric icons — magnifier, wand — drift). Use `Icon` (geometry
/// string) + the `tool`/`iconbtn` style classes as before.
/// </summary>
public sealed class ToolButton : Button
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<ToolButton, string?>(nameof(Icon));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<ToolButton, double>(nameof(IconSize), 24);

    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public double IconSize { get => GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    static ToolButton()
    {
        IconProperty.Changed.AddClassHandler<ToolButton>((b, _) => b.Rebuild());
        IconSizeProperty.Changed.AddClassHandler<ToolButton>((b, _) => b.Rebuild());
    }

    public ToolButton() => Rebuild();

    private void Rebuild()
    {
        if (string.IsNullOrWhiteSpace(Icon)) { Content = null; return; }
        var path = new Avalonia.Controls.Shapes.Path { Classes = { "icon" }, Data = Geometry.Parse(Icon), Stretch = Stretch.None };
        // 24×24 = the shared Lucide frame; the Viewbox scales it uniformly to IconSize and centres it
        var frame = new Avalonia.Controls.Canvas { Width = 24, Height = 24 };
        frame.Children.Add(path);
        Content = new Viewbox
        {
            Width = IconSize,
            Height = IconSize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = frame,
        };
    }
}
