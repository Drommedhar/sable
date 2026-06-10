using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sable.Core.Settings;

namespace Sable.App;

/// <summary>
/// Grid &amp; Axis dialog (View ▸ Grid Settings) — Affinity-style. Edits the grid fields of
/// <see cref="SableSettings"/> live (spacing, subdivisions, colour, visibility + snap-to-grid) and
/// calls back the host to push them to the canvas + persist. Controls are wired directly to the
/// settings POCO; an init guard stops the change handlers firing while seeding initial values.
/// </summary>
public partial class GridSettingsWindow : Window
{
    private readonly SableSettings _s;
    private readonly Action _apply;
    private bool _loading;

    public GridSettingsWindow() : this(new SableSettings(), () => { }) { }

    public GridSettingsWindow(SableSettings settings, Action apply)
    {
        InitializeComponent();
        WindowEscapeHelper.AddEscapeClose(this);
        _s = settings;
        _apply = apply;
        _loading = true;
        ShowGridCheck.IsChecked = _s.ShowGrid;
        SpacingBox.Value = (decimal)_s.GridSpacing;
        SubdivBox.Value = _s.GridSubdivisions;
        ColorField.Hex = _s.GridColor.TrimStart('#');
        SnapGridCheck.IsChecked = _s.SnapToGrid;
        _loading = false;
        // colour field has no Click/Changed event we can bind in XAML → observe its Hex property
        ColorField.PropertyChanged += (_, e) => { if (e.Property == HexColorField.HexProperty) OnChanged(null, null!); };
    }

    private void OnChanged(object? sender, RoutedEventArgs? e)
    {
        if (_loading) return;
        _s.ShowGrid = ShowGridCheck.IsChecked == true;
        _s.GridSpacing = (double)(SpacingBox.Value ?? 50m);
        _s.GridSubdivisions = (int)(SubdivBox.Value ?? 1m);
        _s.GridColor = "#" + ColorField.Hex.TrimStart('#');
        _s.SnapToGrid = SnapGridCheck.IsChecked == true;
        _apply();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
