using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sable.Ai.Download;
using Sable.Ai.Models;
using Sable.Core.Ai;
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
    private readonly ModelRegistry? _registry;
    private readonly ModelDownloader? _downloader;
    private StackPanel[] _panels = System.Array.Empty<StackPanel>();

    // keyboard page working state: command id → current gesture, plus the per-row display boxes
    private readonly Dictionary<string, string> _workKeys = new();
    private readonly Dictionary<string, TextBox> _keyBoxes = new();

    // language picker working state
    private List<string> _langCodes = new();
    private bool _loadingLang;

    public SettingsWindow() : this(new SableSettings(), "—", null) { }

    public SettingsWindow(SableSettings settings, string gpuName, ModelRegistry? registry)
    {
        InitializeComponent();
        _s = settings;
        _registry = registry;
        _downloader = registry is not null ? new ModelDownloader(registry) : null;
        _panels = new[] { PanelGeneral, PanelUI, PanelPerf, PanelColor, PanelAI, PanelUpdates, PanelKeys, PanelAbout };

        // General
        ReopenSwitch.IsChecked = _s.ReopenOnStartup;
        LimitZoomSwitch.IsChecked = _s.LimitInitialZoom;
        DpiBox.Text = ((int)_s.DefaultDpi).ToString();
        // UI
        ThemeCombo.SelectedIndex = (int)_s.Theme;
        // Language (discovered from the Locales folder; switching re-translates live)
        _langCodes = Sable.App.Localization.Loc.Instance.GetAvailableLanguages();
        _loadingLang = true;
        foreach (var code in _langCodes)
            LanguageCombo.Items.Add(new ComboBoxItem { Content = Sable.App.Localization.Loc.Instance.GetLanguageDisplayName(code) });
        int li = _langCodes.FindIndex(c => string.Equals(c, _s.Language, StringComparison.OrdinalIgnoreCase));
        LanguageCombo.SelectedIndex = li < 0 ? 0 : li;
        _loadingLang = false;
        GuideColorField.Hex = _s.GuideColor;
        SmartColorField.Hex = _s.SmartGuideColor;
        GridColorField.Hex = _s.GridColor;
        QuickMaskColorField.Hex = _s.QuickMaskColor;
        // Performance
        UndoSlider.Value = _s.UndoLimit;
        UndoLabel.Text = _s.UndoLimit.ToString();
        RendererLabel.Text = gpuName;
        // Machine Learning
        AiEnabledSwitch.IsChecked = _s.AiEnabled;
        SmartSelectCombo.SelectedIndex = (int)_s.SmartSelectQuality;
        // Updates
        AutoUpdateSwitch.IsChecked = _s.AutoCheckUpdates;
        // Keyboard
        BuildKeyRows();
        // About
        var ver = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionLabel.Text = $"Version {ver?.ToString(3) ?? "0.1.0"}  ·  net10.0  ·  Avalonia + wgpu";

        _aiInitializing = false;   // from here, toggling AI on triggers the licence cycle
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingLang || LanguageCombo is null) return;
        int i = LanguageCombo.SelectedIndex;
        if (i < 0 || i >= _langCodes.Count) return;
        var code = _langCodes[i];
        _s.Language = code;
        Sable.App.Localization.Loc.Instance.CurrentLanguage = code;   // live re-translate, no restart
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

    // ===== Machine Learning: enable → auto licence-cycle + install (PHASE8_AI) =====

    private bool _aiInitializing = true;   // suppress the auto-cycle when restoring the saved toggle state
    private readonly Dictionary<string, Button> _aiInstallBtns = new();

    private void OnAiEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (AiModelRows is null) return;   // fires during InitializeComponent before fields are wired
        bool on = AiEnabledSwitch.IsChecked == true;
        _s.AiEnabled = on;                 // live: persists even if the dialog is cancelled (install is a live action)
        AiModelRows.IsVisible = on;
        if (!on)
        {
            // disabling AI auto-deletes all downloaded models (user requirement)
            if (!_aiInitializing) _registry?.RemoveAll();
            AiModelRows.Children.Clear();
            _aiInstallBtns.Clear();
            return;
        }
        if (AiModelRows.Children.Count == 0) BuildAiRows(); else RefreshAiRows();
        // user just turned it ON → ensure the GPU runtime, then auto-cycle each licence + install
        // the accepted models (not on ctor restore).
        if (!_aiInitializing) _ = EnableAiFlow();
    }

    /// <summary>On AI enable: provision the GPU runtime (Linux/CUDA) first, then run the model licence cycle.</summary>
    private async Task EnableAiFlow()
    {
        await EnsureGpuRuntimeAsync();
        await RunCycle(NotInstalled());
    }

    /// <summary>
    /// Linux only: ensure a Blackwell-capable CUDA ONNX Runtime is installed. Prebuilt ORT has no
    /// kernels for newer NVIDIA archs (e.g. sm_120), so Sable downloads a matching build it published
    /// (<see cref="Sable.Core.Ai.GpuRuntimeCatalog"/>) and activates it at runtime. No-op off Linux,
    /// when already installed, with no NVIDIA GPU, or when no build is published for the arch (the
    /// user can build one with tools/build-ort-cuda.sh and install it).
    /// </summary>
    private async Task EnsureGpuRuntimeAsync()
    {
        if (!OperatingSystem.IsLinux() || Sable.Ai.Runtime.OrtCudaRuntime.IsInstalled) return;
        var probe = new Sable.Ai.Gpu.GpuProbe();
        if (!probe.IsNvidiaPresent) return;   // no NVIDIA → CUDA EP n/a; readiness explains NoGpu
        var art = Sable.Core.Ai.GpuRuntimeCatalog.ResolveFor(probe.ComputeArch);
        if (art is null || !art.HasUrl) return;   // unsupported arch / not yet published → skip

        bool ok = await ConfirmWindow.Ask(this, "GPU runtime",
            $"AI needs a GPU runtime for your {probe.AdapterName} (sm_{probe.ComputeArch}). " +
            $"Download ~{art.SizeBytes / 1_000_000} MB? Licence: {art.License}.");
        if (!ok) { AiEnabledSwitch.IsChecked = false; return; }   // declined → can't run GPU-only AI

        var cts = new System.Threading.CancellationTokenSource();
        var busy = BusyWindow.Begin(this, "Downloading GPU runtime…", cts);
        try
        {
            await new Sable.Ai.Runtime.OrtRuntimeProvisioner().ProvisionAsync(art, busy.Progress, cts.Token);
        }
        catch (System.Exception ex)
        {
            busy.Done();
            await ConfirmWindow.Ask(this, "GPU runtime", $"Couldn't install the GPU runtime: {ex.Message}");
            return;
        }
        busy.Done();
    }

    private IReadOnlyList<RecommendedModel> NotInstalled()
        => RecommendedModels.DefaultSet.Where(m => _registry?.IsInstalled(m.Id) != true).ToList();

    /// <summary>Open the sequential licence-cycle dialog (scroll-to-accept) which installs the accepted models.</summary>
    private async Task RunCycle(IReadOnlyList<RecommendedModel> models)
    {
        if (_downloader is null || models.Count == 0) { RefreshAiRows(); return; }
        var win = new LicenseCycleWindow(models, _downloader) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        await win.ShowDialog<List<RecommendedModel>?>(this);
        RefreshAiRows();
    }

    private void BuildAiRows()
    {
        AiModelRows.Children.Clear();
        _aiInstallBtns.Clear();
        foreach (var m in RecommendedModels.DefaultSet)
        {
            var install = new Button { Classes = { "opt" }, Padding = new Avalonia.Thickness(14, 0), Tag = m.Id, VerticalAlignment = VerticalAlignment.Center };
            install.Click += OnInstallModel;
            _aiInstallBtns[m.Id] = install;
            DockPanel.SetDock(install, Dock.Right);

            var left = new StackPanel { Spacing = 1 };
            left.Children.Add(AiText(m.Name, "ChromeText", 13));
            left.Children.Add(AiText($"Licence: {m.License}", "ChromeTextDim", 11, wrap: true));

            var top = new DockPanel();
            top.Children.Add(install);
            top.Children.Add(left);
            var border = new Border { CornerRadius = new Avalonia.CornerRadius(4), Padding = new Avalonia.Thickness(10, 8), Child = top };
            border.Bind(Border.BackgroundProperty, this.GetResourceObservable("ChromePanel2"));
            AiModelRows.Children.Add(border);
        }
        RefreshAiRows();
    }

    private void RefreshAiRows()
    {
        foreach (var (id, btn) in _aiInstallBtns)
        {
            bool installed = _registry?.IsInstalled(id) == true;
            btn.Content = installed ? "Installed" : "Install";
            btn.IsEnabled = !installed && _downloader is not null;
        }
    }

    private void OnInstallModel(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && RecommendedModels.ById(id) is { } m && _registry?.IsInstalled(id) != true)
            _ = RunCycle(new[] { m });
    }

    private TextBlock AiText(string text, string fgKey, double size, bool wrap = false)
    {
        var tb = new TextBlock { Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        tb.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(fgKey));
        return tb;
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
        _s.AiEnabled = AiEnabledSwitch.IsChecked == true;
        _s.SmartSelectQuality = (SmartSelectQuality)System.Math.Clamp(SmartSelectCombo.SelectedIndex, 0, 3);
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
