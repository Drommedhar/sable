using Avalonia.Controls;
using Avalonia.Interactivity;
using Sable.UI.ViewModels;

namespace Sable.App;

/// <summary>
/// Numeric Transform panel (PLAN §16.9): modeless, bound to the active DocumentViewModel.
/// Live-edits the selected layer's offset / scale / rotation / shear. Auto-shown while the
/// Transform tool is active; closing hides it.
/// </summary>
public partial class TransformWindow : Window
{
    public TransformWindow() { InitializeComponent(); WindowEscapeHelper.AddEscapeClose(this); }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DocumentViewModel { SelectedLayer: { } vm })
        {
            var l = vm.Model;
            l.OffsetX = 0; l.OffsetY = 0; l.ScaleX = 1; l.ScaleY = 1; l.Rotation = 0; l.ShearX = 0; l.ShearY = 0;
            l.Perspective = false; l.PerspCorners = null;
            l.Dirty = true;
            vm.RefreshTransform();
        }
    }
}
