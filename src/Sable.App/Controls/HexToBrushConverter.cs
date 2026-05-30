using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Sable.App;

/// <summary>Binds a "RRGGBB" hex string to a SolidColorBrush for effect colour swatches.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string)?.TrimStart('#') ?? "";
        if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, null, out int rgb))
            return new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture) => null;
}
