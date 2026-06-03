using Avalonia.Controls;

namespace Sable.App;

/// <summary>
/// Optional floating window that hosts the reusable <see cref="AdjustmentPanel"/> (the same
/// editor embedded in the right panel). Kept for the Window ▸ Adjustments toggle; the panel
/// is the single source of truth, so there is no duplicated logic here.
/// </summary>
public partial class AdjustmentWindow : Window
{
    public AdjustmentWindow() => InitializeComponent();

    /// <summary>Forwarded to the hosted panel; supplies the Curves/Levels histogram source.</summary>
    public Func<byte[]?>? CompositeProvider
    {
        get => Panel.CompositeProvider;
        set { Panel.CompositeProvider = value; Panel.SyncPanels(); }
    }
}
