using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sable.App;

/// <summary>
/// Shape properties panel (PLAN §16.10): modeless, bound to the active DocumentViewModel.
/// Live-edits the selected ShapeLayer's fill/stroke/dash + per-kind params (sides / inner
/// radius / corner radius). Auto-shown when a shape layer is selected; closing hides it.
/// </summary>
public partial class ShapeWindow : Window
{
    public ShapeWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
