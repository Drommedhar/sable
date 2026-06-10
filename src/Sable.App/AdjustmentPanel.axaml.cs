using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Sable.Engine.Layers;
using Sable.Tools;
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

    private AdjustmentLayer? _gradMap;   // the Gradient Map layer the bar is editing
    private bool _gmSync;                // guards the bar ↔ hex-field feedback loop

    /// <summary>Press-and-hold compare (before/after): MainWindow hides all adjustments while held.</summary>
    public event System.Action? CompareStarted;
    public event System.Action? CompareEnded;

    public AdjustmentPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        CompareBtn.AddHandler(PointerPressedEvent, (_, e) => { CompareStarted?.Invoke(); e.Handled = true; },
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        CompareBtn.AddHandler(PointerReleasedEvent, (_, _) => CompareEnded?.Invoke(),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        CompareBtn.PointerCaptureLost += (_, _) => CompareEnded?.Invoke();

        GradMapBar.Changed += OnGradMapChanged;
        GradMapBar.StopSelected += OnGradMapStopSelected;
        GradMapColor.PropertyChanged += (_, e) =>
        {
            if (e.Property == HexColorField.HexProperty && !_gmSync) ApplyGradMapHex();
        };
    }

    /// <summary>Push the bar's stops back into the Gradient Map layer (live recomposite).</summary>
    private void OnGradMapChanged()
    {
        if (_gradMap is null) return;
        _gradMap.GradientStops.Clear();
        foreach (var s in GradMapBar.Def.Stops)
            _gradMap.GradientStops.Add((s.Pos, s.R, s.G, s.B));
        _gradMap.Dirty = true;
        OnGradMapStopSelected(GradMapBar.Selected);
    }

    private void OnGradMapStopSelected(int idx)
    {
        var s = GradMapBar.SelectedStop;
        _gmSync = true;
        GradMapColor.Hex = $"{s.R:X2}{s.G:X2}{s.B:X2}";
        _gmSync = false;
    }

    private void ApplyGradMapHex()
    {
        var hex = GradMapColor.Hex.TrimStart('#');
        if (hex.Length != 6
            || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return;
        GradMapBar.SetSelectedColor((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
    }

    private void OnGradMapAddStop(object? sender, RoutedEventArgs e) => GradMapBar.AddStop();
    private void OnGradMapDeleteStop(object? sender, RoutedEventArgs e) => GradMapBar.RemoveSelected();

    /// <summary>Load the selected layer's gradient-map stops into the editor bar.</summary>
    private void SyncGradMap()
    {
        _gradMap = _vm?.SelectedLayer?.GradientMapAdjustment;
        if (_gradMap is null) return;
        var def = new GradientDef(_gradMap.GradientStops
            .Select(s => new GradientStop(s.Pos, s.R, s.G, s.B, 255)).ToArray());
        GradMapBar.Def = def;
        OnGradMapStopSelected(GradMapBar.Selected);
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
        SyncGradMap();
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        _vm?.SelectedLayer?.ResetAdjustment();
        Curve.InvalidateVisual();
        SyncGradMap();
    }

    private void OnCurveChannel(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        int ch = int.Parse(btn.Tag?.ToString() ?? "0");
        Curve.Channel = ch;
        ChRgb.IsChecked = ch == 0; ChR.IsChecked = ch == 1; ChG.IsChecked = ch == 2; ChB.IsChecked = ch == 3;
    }
}
