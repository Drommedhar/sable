using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Sable.UI.ViewModels;

namespace Sable.App;

/// <summary>
/// Modeless floating toolbox for adjustment-layer parameters (Affinity-style:
/// header + Reset, gradient sliders with value boxes, histogram behind Curves/Levels,
/// footer Opacity + Blend Mode). Bound to the same DocumentViewModel as the main window.
/// </summary>
public partial class AdjustmentWindow : Window
{
    private DocumentViewModel? _vm;

    /// <summary>Supplies the current composite RGBA8 for the histogram (set by MainWindow).</summary>
    public Func<byte[]?>? CompositeProvider { get; set; }

    public AdjustmentWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // CurveEditor/sliders already flag the layer Dirty; the canvas timer polls AnyDirty.
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
    private void SyncPanels()
    {
        var sel = _vm?.SelectedLayer;
        Curve.SetTarget(sel?.CurvesAdjustment, Curve.Channel);

        // backdrop histogram for Curves + Levels graphs
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
