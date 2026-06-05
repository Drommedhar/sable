using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

using Sable.App.Localization;

namespace Sable.App;

/// <summary>A document preset (size + unit + DPI) shown as a thumbnail in the New dialog.</summary>
public sealed class DocPreset
{
    public string Name { get; }
    public double W { get; }
    public double H { get; }
    public string Unit { get; }     // "px" | "mm" | "in"
    public double Dpi { get; }

    public DocPreset(string name, double w, double h, string unit, double dpi)
    { Name = name; W = w; H = h; Unit = unit; Dpi = dpi; }

    public string Dims => Loc.T("newDocumentWindow.dimsFormat", Trim(W), Trim(H), Unit);
    private static string Trim(double v) => v.ToString(v % 1 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture);

    // thumbnail rectangle: longest side = 52px, preserves aspect
    public double ThumbW => W >= H ? 52 : 52 * (W / H);
    public double ThumbH => H >= W ? 52 : 52 * (H / W);
}

/// <summary>
/// Affinity-style New Document dialog (Phase 2): categorised preset thumbnail grid + a live
/// properties panel (units px/mm/in, DPI, width/height, orientation, background). Outputs pixel
/// dimensions + DPI + transparency for a blank <see cref="Sable.Engine.Document"/> in a new tab.
/// </summary>
public partial class NewDocumentWindow : Window
{
    public int DocWidth { get; private set; } = 1920;
    public int DocHeight { get; private set; } = 1080;
    public double Dpi { get; private set; } = 96;
    public bool Transparent { get; private set; }
    public Sable.Core.BitDepth DocDepth { get; private set; } = Sable.Core.BitDepth.Eight;

    private string _unit = "px";
    private bool _loading;
    private readonly List<ListBox> _lists = new();

    private static readonly (string Cat, DocPreset[] Presets)[] Catalog =
    {
        ("newDocumentWindow.catPrint", new[]
        {
            new DocPreset("A3", 297, 420, "mm", 300),
            new DocPreset("A4", 210, 297, "mm", 300),
            new DocPreset("A5", 148, 210, "mm", 300),
            new DocPreset("Letter", 8.5, 11, "in", 300),
            new DocPreset("Legal", 8.5, 14, "in", 300),
            new DocPreset("Tabloid", 11, 17, "in", 300),
        }),
        ("newDocumentWindow.catScreen", new[]
        {
            new DocPreset("HD 1080p", 1920, 1080, "px", 72),
            new DocPreset("QHD 1440p", 2560, 1440, "px", 72),
            new DocPreset("4K UHD", 3840, 2160, "px", 72),
            new DocPreset("HD 720p", 1280, 720, "px", 72),
            new DocPreset("Square", 1000, 1000, "px", 72),
        }),
        ("newDocumentWindow.catSocial", new[]
        {
            new DocPreset("Instagram Post", 1080, 1080, "px", 72),
            new DocPreset("Instagram Story", 1080, 1920, "px", 72),
            new DocPreset("Facebook Cover", 1200, 630, "px", 72),
            new DocPreset("Twitter Header", 1500, 500, "px", 72),
        }),
        ("newDocumentWindow.catPhoto", new[]
        {
            new DocPreset("4 x 6", 6, 4, "in", 300),
            new DocPreset("5 x 7", 7, 5, "in", 300),
            new DocPreset("8 x 10", 10, 8, "in", 300),
            new DocPreset("A4 Photo", 297, 210, "mm", 300),
        }),
    };

    public NewDocumentWindow() : this(96) { }

    public NewDocumentWindow(double defaultDpi)
    {
        InitializeComponent();
        BuildSections();
        _loading = true;
        DpiBox.Text = ((int)defaultDpi).ToString(CultureInfo.InvariantCulture);
        Dpi = defaultDpi;
        _loading = false;
        UpdatePxLabel();
    }

    private void BuildSections()
    {
        foreach (var (cat, presets) in Catalog)
        {
            Sections.Children.Add(new TextBlock
            {
                Text = Loc.T(cat),
                FontSize = 12,
                Margin = new Avalonia.Thickness(2, 6, 0, 2),
                Foreground = this.FindResource("ChromeTextDim") as IBrush,
            });
            var list = new ListBox
            {
                ItemsSource = presets,
                ItemTemplate = (IDataTemplate)Resources["PresetTemplate"]!,
                Background = Brushes.Transparent,
                Tag = presets,
            };
            list.ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal });
            list.SelectionChanged += OnPresetSelected;
            _lists.Add(list);
            Sections.Children.Add(list);
        }
    }

    private void OnPresetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (sender is not ListBox lb || lb.SelectedItem is not DocPreset p) return;

        // single selection across all category lists
        _loading = true;
        foreach (var other in _lists) if (!ReferenceEquals(other, lb)) other.SelectedItem = null;
        _loading = false;

        _unit = p.Unit;
        Dpi = p.Dpi;
        SelName.Text = p.Name;
        UnitCombo.SelectedIndex = p.Unit switch { "mm" => 1, "in" => 2, _ => 0 };
        _loading = true;
        DpiBox.Text = ((int)p.Dpi).ToString(CultureInfo.InvariantCulture);
        WBox.Text = Fmt(p.W);
        HBox.Text = Fmt(p.H);
        WUnit.Text = HUnit.Text = p.Unit;
        _loading = false;
        UpdatePxLabel();
    }

    private void OnUnitChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || WBox is null) return;
        var newUnit = UnitCombo.SelectedIndex switch { 1 => "mm", 2 => "in", _ => "px" };
        if (newUnit == _unit) return;
        double dpi = ParseDpi();
        // convert the current W/H from the old unit to the new one (via pixels)
        double w = FromPx(ToPx(ParseD(WBox.Text), _unit, dpi), newUnit, dpi);
        double h = FromPx(ToPx(ParseD(HBox.Text), _unit, dpi), newUnit, dpi);
        _unit = newUnit;
        _loading = true;
        WBox.Text = Fmt(w); HBox.Text = Fmt(h);
        WUnit.Text = HUnit.Text = newUnit;
        _loading = false;
        UpdatePxLabel();
    }

    private void OnFieldChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        UpdatePxLabel();
    }

    private void OnPortrait(object? sender, RoutedEventArgs e) => SetOrientation(portrait: true);
    private void OnLandscape(object? sender, RoutedEventArgs e) => SetOrientation(portrait: false);

    private void SetOrientation(bool portrait)
    {
        if (WBox is null) return;
        double w = ParseD(WBox.Text), h = ParseD(HBox.Text);
        bool isPortrait = h >= w;
        if (isPortrait != portrait)
        {
            _loading = true;
            WBox.Text = Fmt(h); HBox.Text = Fmt(w);
            _loading = false;
            UpdatePxLabel();
        }
    }

    private void OnSearch(object? sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text?.Trim() ?? "";
        for (int i = 0; i < Catalog.Length; i++)
        {
            var presets = Catalog[i].Presets;
            var filtered = q.Length == 0
                ? presets
                : presets.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray();
            _lists[i].ItemsSource = filtered;
            // hide the header + list when a category has no match
            bool any = filtered.Length > 0;
            _lists[i].IsVisible = any;
            Sections.Children[i * 2].IsVisible = any;   // the category header TextBlock
        }
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        double dpi = ParseDpi();
        DocWidth = Math.Clamp(ToPx(ParseD(WBox.Text), _unit, dpi), 1, 16384);
        DocHeight = Math.Clamp(ToPx(ParseD(HBox.Text), _unit, dpi), 1, 16384);
        Dpi = dpi;
        Transparent = BgCombo.SelectedIndex == 1;
        DocDepth = DepthCombo.SelectedIndex switch { 1 => Sable.Core.BitDepth.Sixteen, 2 => Sable.Core.BitDepth.ThirtyTwo, _ => Sable.Core.BitDepth.Eight };
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    // --- helpers ---
    private void UpdatePxLabel()
    {
        if (PxLabel is null) return;
        double dpi = ParseDpi();
        int w = ToPx(ParseD(WBox.Text), _unit, dpi);
        int h = ToPx(ParseD(HBox.Text), _unit, dpi);
        PxLabel.Text = Loc.T("newDocumentWindow.pxDpiFormat", w, h, (int)dpi);
        UpdateOrientationHL();
    }

    private void UpdateOrientationHL()
    {
        if (PortraitBtn is null) return;
        bool portrait = ParseD(HBox.Text) >= ParseD(WBox.Text);
        var on = this.FindResource("ChromeBorder2") as IBrush;
        PortraitBtn.Background = portrait ? on : Brushes.Transparent;
        LandscapeBtn.Background = portrait ? Brushes.Transparent : on;
    }

    private double ParseDpi() => Math.Clamp(ParseD(DpiBox?.Text) is var d && d > 0 ? d : 96, 1, 2400);

    private static double ParseD(string? s)
        => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string Fmt(double v) => v.ToString(v % 1 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture);

    private static int ToPx(double v, string unit, double dpi) => unit switch
    {
        "in" => (int)Math.Round(v * dpi),
        "mm" => (int)Math.Round(v / 25.4 * dpi),
        _ => (int)Math.Round(v),
    };

    private static double FromPx(int px, string unit, double dpi) => unit switch
    {
        "in" => px / dpi,
        "mm" => px / dpi * 25.4,
        _ => px,
    };
}
