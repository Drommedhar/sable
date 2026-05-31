using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Sable.App;

/// <summary>Active tab → canvas-colour background; inactive → transparent (tab strip).</summary>
public sealed class BoolToTabBgConverter : IValueConverter
{
    public static readonly BoolToTabBgConverter Instance = new();
    private static readonly IBrush Active = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Active : Brushes.Transparent;

    public object? ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture) => null;
}
