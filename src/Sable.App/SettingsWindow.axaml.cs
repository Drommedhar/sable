using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sable.Core.Settings;

namespace Sable.App;

/// <summary>
/// Affinity-style Settings dialog (PLAN §17.1): searchable category sidebar + grouped
/// right pane (toggle switches / sliders / dropdowns). Edits the shared
/// <see cref="SableSettings"/> in place; OK commits. Categories switch the right panels.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SableSettings _s;
    private StackPanel[] _panels = System.Array.Empty<StackPanel>();

    public SettingsWindow() : this(new SableSettings(), "—") { }

    public SettingsWindow(SableSettings settings, string gpuName)
    {
        InitializeComponent();
        _s = settings;
        _panels = new[] { PanelGeneral, PanelUI, PanelPerf, PanelColor, PanelAI, PanelUpdates, PanelAbout };

        // General
        ReopenSwitch.IsChecked = _s.ReopenOnStartup;
        LimitZoomSwitch.IsChecked = _s.LimitInitialZoom;
        DpiBox.Text = ((int)_s.DefaultDpi).ToString();
        // UI
        ThemeCombo.SelectedIndex = (int)_s.Theme;
        // Performance
        UndoSlider.Value = _s.UndoLimit;
        UndoLabel.Text = _s.UndoLimit.ToString();
        RendererLabel.Text = gpuName;
        // Updates
        AutoUpdateSwitch.IsChecked = _s.AutoCheckUpdates;
        // About
        var ver = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionLabel.Text = $"Version {ver?.ToString(3) ?? "0.1.0"}  ·  net10.0  ·  Avalonia + wgpu";
    }

    private void OnCategory(object? sender, SelectionChangedEventArgs e)
    {
        if (_panels.Length == 0) return;   // SelectedIndex=0 fires during XAML init before fields are wired
        int i = CatList.SelectedIndex;
        for (int p = 0; p < _panels.Length; p++) _panels[p].IsVisible = p == i;
    }

    private void OnSearch(object? sender, TextChangedEventArgs e)
    {
        if (CatList is null || _panels.Length == 0) return;
        var q = SearchBox.Text?.Trim() ?? "";
        var cats = CatList.Items.OfType<ListBoxItem>().ToList();

        if (q.Length == 0)   // cleared → restore everything, show the selected category
        {
            foreach (var it in cats) it.IsVisible = true;
            foreach (var p in _panels) ShowAllRows(p, true);
            OnCategory(this, null!);
            return;
        }

        // filter rows inside every page; a category shows only if it has a matching setting
        int firstMatch = -1;
        for (int i = 0; i < _panels.Length; i++)
        {
            bool catName = (cats[i].Content?.ToString() ?? "").Contains(q, System.StringComparison.OrdinalIgnoreCase);
            bool any = catName;
            foreach (var child in _panels[i].Children)
            {
                bool m = catName || RowMatches(child, q);
                child.IsVisible = m;
                any |= m;
            }
            if (catName) ShowAllRows(_panels[i], true);   // whole category matched by name
            if (i < cats.Count) cats[i].IsVisible = any;
            if (any && firstMatch < 0) firstMatch = i;
        }

        // jump to the first category that has a match, show its (filtered) page
        for (int p = 0; p < _panels.Length; p++) _panels[p].IsVisible = p == firstMatch;
        if (firstMatch >= 0) CatList.SelectedIndex = firstMatch;
    }

    private static void ShowAllRows(StackPanel panel, bool visible)
    {
        foreach (var child in panel.Children) child.IsVisible = visible;
    }

    /// <summary>True if any TextBlock under the control contains the query (case-insensitive).</summary>
    private static bool RowMatches(Control control, string q)
    {
        foreach (var tb in control.GetVisualDescendants().OfType<TextBlock>())
            if ((tb.Text ?? "").Contains(q, System.StringComparison.OrdinalIgnoreCase)) return true;
        if (control is TextBlock t) return (t.Text ?? "").Contains(q, System.StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private void OnUndoSlider(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (UndoLabel is not null) UndoLabel.Text = ((int)UndoSlider.Value).ToString();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        _s.ReopenOnStartup = ReopenSwitch.IsChecked == true;
        _s.LimitInitialZoom = LimitZoomSwitch.IsChecked == true;
        _s.DefaultDpi = int.TryParse(DpiBox.Text, out var dpi) ? System.Math.Clamp(dpi, 1, 2400) : _s.DefaultDpi;
        _s.Theme = (AppTheme)System.Math.Clamp(ThemeCombo.SelectedIndex, 0, 2);
        _s.UndoLimit = (int)UndoSlider.Value;
        _s.AutoCheckUpdates = AutoUpdateSwitch.IsChecked == true;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
