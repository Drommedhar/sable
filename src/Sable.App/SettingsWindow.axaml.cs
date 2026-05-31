using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
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

    // keyboard page working state: command id → current gesture, plus the per-row display boxes
    private readonly Dictionary<string, string> _workKeys = new();
    private readonly Dictionary<string, TextBox> _keyBoxes = new();

    public SettingsWindow() : this(new SableSettings(), "—") { }

    public SettingsWindow(SableSettings settings, string gpuName)
    {
        InitializeComponent();
        _s = settings;
        _panels = new[] { PanelGeneral, PanelUI, PanelPerf, PanelColor, PanelAI, PanelUpdates, PanelKeys, PanelAbout };

        // General
        ReopenSwitch.IsChecked = _s.ReopenOnStartup;
        LimitZoomSwitch.IsChecked = _s.LimitInitialZoom;
        DpiBox.Text = ((int)_s.DefaultDpi).ToString();
        // UI
        ThemeCombo.SelectedIndex = (int)_s.Theme;
        GuideColorField.Hex = _s.GuideColor;
        SmartColorField.Hex = _s.SmartGuideColor;
        GridColorField.Hex = _s.GridColor;
        QuickMaskColorField.Hex = _s.QuickMaskColor;
        // Performance
        UndoSlider.Value = _s.UndoLimit;
        UndoLabel.Text = _s.UndoLimit.ToString();
        RendererLabel.Text = gpuName;
        // Updates
        AutoUpdateSwitch.IsChecked = _s.AutoCheckUpdates;
        // Keyboard
        BuildKeyRows();
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
        _s.GuideColor = NormHex(GuideColorField.Hex, _s.GuideColor);
        _s.SmartGuideColor = NormHex(SmartColorField.Hex, _s.SmartGuideColor);
        _s.GridColor = NormHex(GridColorField.Hex, _s.GridColor);
        _s.QuickMaskColor = NormHex(QuickMaskColorField.Hex, _s.QuickMaskColor);
        _s.UndoLimit = (int)UndoSlider.Value;
        _s.AutoCheckUpdates = AutoUpdateSwitch.IsChecked == true;
        // keyboard: store only the overrides that differ from the catalog default (keeps JSON small)
        _s.KeyBindings.Clear();
        foreach (var c in KeyCommands.Catalog)
            if (_workKeys.TryGetValue(c.Id, out var g) && g != c.DefaultGesture)
                _s.KeyBindings[c.Id] = g;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    // ===== Keyboard page (PLAN §17.1 rebindable hotkeys) =====

    private void BuildKeyRows()
    {
        KeyRows.Children.Clear();
        _keyBoxes.Clear();
        _workKeys.Clear();
        string? cat = null;
        foreach (var c in KeyCommands.Catalog)
        {
            _workKeys[c.Id] = _s.GestureFor(c.Id);
            if (c.Category != cat)
            {
                cat = c.Category;
                KeyRows.Children.Add(new TextBlock { Text = cat, Classes = { "settingHeader" }, Margin = new Avalonia.Thickness(0, 8, 0, 0) });
            }

            var box = new TextBox
            {
                Width = 150, IsReadOnly = true, Focusable = true,
                Text = _workKeys[c.Id], Tag = c.Id, PlaceholderText = "unbound",
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            box.AddHandler(KeyDownEvent, OnGestureKeyDown, RoutingStrategies.Tunnel);
            _keyBoxes[c.Id] = box;

            var reset = new Button { Content = "Reset", Classes = { "opt" }, Tag = c.Id, Padding = new Avalonia.Thickness(10, 0) };
            reset.Click += OnResetGesture;

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(box);
            content.Children.Add(reset);
            KeyRows.Children.Add(new SettingRow { Label = c.Label, Content = content });
        }
    }

    /// <summary>Capture the pressed chord as a gesture; tunnel so the read-only box never types.</summary>
    private void OnGestureKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (sender is not TextBox box || box.Tag is not string id) return;

        // clear (unbind) on Backspace/Delete; ignore lone modifier presses
        if (e.Key is Key.Back or Key.Delete) { SetGesture(id, ""); return; }
        if (IsModifierKey(e.Key)) return;

        var mods = e.KeyModifiers;
        bool fkey = e.Key >= Key.F1 && e.Key <= Key.F24;
        // command hotkeys need a modifier (or be a function key) so they don't shadow tool letters
        if (mods == KeyModifiers.None && !fkey)
        {
            ShowWarn("Add Ctrl/Alt/Shift (tool letters stay fixed).");
            return;
        }
        AssignGesture(id, Canonical(mods, e.Key));
    }

    private void OnResetGesture(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            SetGesture(id, KeyCommands.Catalog.First(c => c.Id == id).DefaultGesture);
    }

    /// <summary>Assign a gesture, stealing it from whoever currently holds it (steal + warn).</summary>
    private void AssignGesture(string id, string gesture)
    {
        var owner = _workKeys.FirstOrDefault(kv => kv.Key != id &&
                        string.Equals(kv.Value, gesture, System.StringComparison.OrdinalIgnoreCase));
        if (owner.Key is not null)
        {
            SetGesture(owner.Key, "");
            var label = KeyCommands.Catalog.First(c => c.Id == owner.Key).Label;
            ShowWarn($"{gesture} unbound from “{label}”.");
        }
        else KeyWarn.IsVisible = false;
        SetGesture(id, gesture);
    }

    private void SetGesture(string id, string gesture)
    {
        _workKeys[id] = gesture;
        if (_keyBoxes.TryGetValue(id, out var box)) box.Text = gesture;
    }

    private void ShowWarn(string msg) { KeyWarn.Text = msg; KeyWarn.IsVisible = true; }

    private static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin or Key.System;

    /// <summary>Format a chord to KeyGesture.Parse grammar: "Ctrl+Shift+C", "Ctrl+OemPlus".</summary>
    private static string Canonical(KeyModifiers mods, Key key)
    {
        var parts = new List<string>(4);
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Meta");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Normalise a hex field to "#RRGGBB"; keep the previous value if invalid.</summary>
    private static string NormHex(string? hex, string fallback)
    {
        var s = (hex ?? "").Trim().TrimStart('#');
        if (s.Length == 8) s = s.Substring(2);
        if (s.Length == 6 && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out _))
            return "#" + s.ToUpperInvariant();
        return fallback;
    }
}
