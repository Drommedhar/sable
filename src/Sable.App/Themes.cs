using Avalonia.Styling;

namespace Sable.App;

/// <summary>
/// Theme variants for the chrome (PLAN §17.1). Dark/Light are Fluent's built-ins; Gray is a
/// custom variant that inherits Dark (Fluent controls stay dark) but supplies its own lighter
/// chrome brushes from Theme.axaml's ThemeDictionaries. `MainWindow.ApplyTheme` sets
/// <c>RequestedThemeVariant</c> from the <c>SableSettings.Theme</c> enum.
/// </summary>
public static class Themes
{
    public static readonly ThemeVariant Gray = new("Gray", ThemeVariant.Dark);
}
