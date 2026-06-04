using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sable.Core.Settings;

namespace Sable.App;

/// <summary>
/// Snapping dialog (View ▸ Snapping) — Affinity-style. Edits the snapping fields of
/// <see cref="SableSettings"/> live and calls back the host to push them to the canvas + persist.
/// Exposes the options the snap engine actually backs (master toggle, tolerance, page snapping =
/// grid/guides/canvas, object snapping = bounding boxes / visible-only). Affinity options that need
/// subsystems Sable doesn't have yet (baseline grid, spreads, margins, gaps, key points, geometry)
/// are intentionally omitted rather than shown as dead toggles.
/// </summary>
public partial class SnappingWindow : Window
{
    private readonly SableSettings _s;
    private readonly Action _apply;
    private bool _loading;

    public SnappingWindow() : this(new SableSettings(), () => { }) { }

    public SnappingWindow(SableSettings settings, Action apply)
    {
        InitializeComponent();
        _s = settings;
        _apply = apply;
        _loading = true;
        EnableSwitch.IsChecked = _s.SnapEnabled;
        ToleranceBox.Value = (decimal)_s.SnapTolerance;
        GridCheck.IsChecked = _s.SnapToGrid;
        GuidesCheck.IsChecked = _s.SnapToGuides;
        CanvasCheck.IsChecked = _s.SnapToCanvas;
        ObjectsCheck.IsChecked = _s.SnapToObjects;
        VisibleCheck.IsChecked = _s.SnapVisibleOnly;
        _loading = false;
    }

    private void OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.SnapEnabled = EnableSwitch.IsChecked == true;
        _s.SnapTolerance = (double)(ToleranceBox.Value ?? 6m);
        _s.SnapToGrid = GridCheck.IsChecked == true;
        _s.SnapToGuides = GuidesCheck.IsChecked == true;
        _s.SnapToCanvas = CanvasCheck.IsChecked == true;
        _s.SnapToObjects = ObjectsCheck.IsChecked == true;
        _s.SnapVisibleOnly = VisibleCheck.IsChecked == true;
        _apply();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
