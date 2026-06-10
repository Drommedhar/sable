using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Sable.App.Localization;
using Sable.Core.Settings;

namespace Sable.App;

/// <summary>
/// Keyboard-shortcut cheat sheet (Help menu): the rebindable command catalog with the user's
/// effective gestures, plus the fixed tool-cycle letters and canvas keys. Read-only — rebinding
/// lives in Preferences ▸ Keyboard.
/// </summary>
public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow() : this(new SableSettings()) { }

    public ShortcutsWindow(SableSettings settings)
    {
        InitializeComponent();
        WindowEscapeHelper.AddEscapeClose(this);
        Build(settings);
    }

    private void Build(SableSettings s)
    {
        // rebindable commands, grouped by category, showing the EFFECTIVE gesture (override or default)
        foreach (var group in KeyCommands.Catalog.GroupBy(c => c.Category))
        {
            AddHeader(group.Key);
            foreach (var c in group)
            {
                var g = s.GestureFor(c.Id);
                if (!string.IsNullOrEmpty(g)) AddRow(c.Label, PrettyGesture(g));
            }
        }

        // fixed tool-cycle letters (re-press cycles within the group)
        AddHeader(Loc.T("shortcutsWindow.tools"));
        foreach (var (key, tools) in ToolGroups())
            AddRow(string.Join(" / ", tools.Select(Loc.T)), key);

        // canvas / colour keys
        AddHeader(Loc.T("shortcutsWindow.canvas"));
        AddRow(Loc.T("shortcutsWindow.swap"), "X");
        AddRow(Loc.T("shortcutsWindow.reset"), "D");
        AddRow(Loc.T("shortcutsWindow.quickMask"), "Q");
        AddRow(Loc.T("shortcutsWindow.editMask"), "K");
        AddRow(Loc.T("shortcutsWindow.zoomInOut"), "+ / -");
        AddRow(Loc.T("shortcutsWindow.fitView"), "0");
        AddRow(Loc.T("shortcutsWindow.pan"), Loc.T("shortcutsWindow.arrows"));
        AddRow(Loc.T("shortcutsWindow.altPick"), "Alt+Click");
        AddRow(Loc.T("shortcutsWindow.wheelZoom"), Loc.T("shortcutsWindow.wheel"));
        AddRow(Loc.T("shortcutsWindow.middlePan"), Loc.T("shortcutsWindow.middleDrag"));
        AddRow(Loc.T("shortcutsWindow.brushHud"), "Ctrl+Alt+Drag");
    }

    private static IEnumerable<(string Key, string[] Tools)> ToolGroups() => new (string, string[])[]
    {
        ("V", new[] { "tools.move" }),
        ("M", new[] { "tools.rectangleMarquee", "tools.ellipticalMarquee" }),
        ("L", new[] { "tools.lasso", "tools.polygonalLasso" }),
        ("W", new[] { "tools.magicWand", "tools.colourRange", "tools.smartSelect" }),
        ("B", new[] { "tools.brush", "tools.pencil" }),
        ("E", new[] { "tools.eraser" }),
        ("G", new[] { "tools.fill", "tools.gradient" }),
        ("C", new[] { "tools.crop" }),
        ("U", new[] { "tools.rectangle", "tools.roundedRectangle", "tools.ellipse", "tools.polygon", "tools.star", "tools.line", "tools.arrow" }),
        ("S", new[] { "tools.cloneStamp", "tools.healingBrush", "tools.spotHeal", "tools.patch" }),
        ("O", new[] { "tools.dodge", "tools.burn", "tools.sponge", "tools.blur", "tools.sharpen", "tools.smudge" }),
        ("T", new[] { "tools.text" }),
        ("Y", new[] { "tools.liquify", "tools.meshWarp" }),
        ("P", new[] { "tools.pen", "tools.node" }),
        ("I", new[] { "tools.eyedropper" }),
        ("H", new[] { "tools.hand" }),
        ("Z", new[] { "tools.zoom" }),
    };

    /// <summary>Turn the stored gesture grammar into a friendlier display ("Ctrl+OemPlus" → "Ctrl++").</summary>
    private static string PrettyGesture(string g) => g
        .Replace("OemPlus", "+").Replace("OemMinus", "-")
        .Replace("D0", "0").Replace("D1", "1");

    private void AddHeader(string text)
    {
        Root.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, Root.Children.Count == 0 ? 0 : 14, 0, 4),
        }.WithChromeForeground(this, "ChromeText"));
    }

    private void AddRow(string label, string gesture)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var lbl = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
            .WithChromeForeground(this, "ChromeTextDim");
        var key = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(7, 1),
            Child = new TextBlock { Text = gesture, FontSize = 11 }.WithChromeForeground(this, "ChromeText"),
        };
        key.Bind(Border.BackgroundProperty, this.GetResourceObservable("ChromePanel3"));
        Grid.SetColumn(key, 1);
        grid.Children.Add(lbl);
        grid.Children.Add(key);
        grid.Margin = new Thickness(0, 1);
        Root.Children.Add(grid);
    }
}

internal static class ShortcutsWindowExtensions
{
    /// <summary>Bind Foreground to a Chrome theme token (resolves the active variant + re-themes live).</summary>
    public static TextBlock WithChromeForeground(this TextBlock tb, Control scope, string token)
    {
        tb.Bind(TextBlock.ForegroundProperty, scope.GetResourceObservable(token));
        return tb;
    }
}
