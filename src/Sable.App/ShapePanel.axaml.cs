using Avalonia.Controls;

namespace Sable.App;

/// <summary>
/// Shape properties editor (fill / stroke / dash + per-kind sides / inner radius / corner
/// radius). Embedded in the right panel (and reused by the floating ShapeWindow). Binds to
/// the active <see cref="Sable.UI.ViewModels.DocumentViewModel"/>'s SelectedLayer.
/// </summary>
public partial class ShapePanel : UserControl
{
    public ShapePanel() => InitializeComponent();
}
