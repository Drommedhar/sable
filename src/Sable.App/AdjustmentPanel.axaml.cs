using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Sable.UI.ViewModels;

namespace Sable.App;

/// <summary>
/// Adjustment / live-filter parameter editor (Affinity-style: header + Reset, gradient
/// sliders with value boxes, histogram behind Curves/Levels, per-kind param groups).
/// Embedded in the right panel (and reused by the floating AdjustmentWindow). Binds to the
/// active <see cref="DocumentViewModel"/>; follows its <c>SelectedLayer</c>.
/// </summary>
public partial class AdjustmentPanel : UserControl
{
    private DocumentViewModel? _vm;

    /// <summary>Supplies the current composite RGBA8 for the Curves/Levels histogram (set by MainWindow).</summary>
    public Func<byte[]?>? CompositeProvider { get; set; }

    public AdjustmentPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as DocumentViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        SyncPanels();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.SelectedLayer)) SyncPanels();
    }

    /// <summary>Point the curve editor + histograms at the selected layer.</summary>
    public void SyncPanels()
    {
        var sel = _vm?.SelectedLayer;
        Curve.SetTarget(sel?.CurvesAdjustment, Curve.Channel);

        int[]? hist = null;
        if (sel is not null && (sel.IsCurves || sel.IsLevels) && CompositeProvider?.Invoke() is { } px)
            hist = Histogram.Compute(px);
        Curve.SetHistogram(hist);
        LevelsHist.SetBins(hist);
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        _vm?.SelectedLayer?.ResetAdjustment();
        Curve.InvalidateVisual();
    }

    private void OnCurveChannel(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        int ch = int.Parse(btn.Tag?.ToString() ?? "0");
        Curve.Channel = ch;
        ChRgb.IsChecked = ch == 0; ChR.IsChecked = ch == 1; ChG.IsChecked = ch == 2; ChB.IsChecked = ch == 3;
    }
}
