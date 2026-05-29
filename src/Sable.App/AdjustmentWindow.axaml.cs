using Avalonia.Controls;

namespace Sable.App;

/// <summary>
/// Modeless floating toolbox for adjustment-layer parameters. A plain window:
/// opened when an adjustment layer is active, closed when not. Bound to the same
/// DocumentViewModel as the main window.
/// </summary>
public partial class AdjustmentWindow : Window
{
    public AdjustmentWindow()
    {
        InitializeComponent();
    }
}
