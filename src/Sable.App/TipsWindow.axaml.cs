using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sable.App;

/// <summary>First-run orientation card: the five things a new user needs (tools, canvas
/// navigation, layers, the non-destructive model, shortcuts). Shown once; Help menu
/// has the full shortcut sheet for later.</summary>
public partial class TipsWindow : Window
{
    public TipsWindow() { InitializeComponent(); WindowEscapeHelper.AddEscapeClose(this); }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
