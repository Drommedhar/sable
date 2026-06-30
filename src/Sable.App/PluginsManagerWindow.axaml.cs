using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Sable.App.Localization;
using Sable.Plugins;

namespace Sable.App;

/// <summary>
/// Modeless plugin manager (PLUGIN_SDK_PLAN §24): lists installed plugins with their state and any
/// load errors, lets the user enable/disable each, reload the folder, and read the diagnostics log.
/// </summary>
public partial class PluginsManagerWindow : Window
{
    private readonly PluginManager _mgr;
    private readonly PluginLogHub _log;

    public PluginsManagerWindow() : this(null!, null!, "") { }

    public PluginsManagerWindow(PluginManager mgr, PluginLogHub log, string pluginsDir)
    {
        InitializeComponent();
        WindowEscapeHelper.AddEscapeClose(this);
        _mgr = mgr;
        _log = log;
        DirLabel.Text = pluginsDir;
        if (_mgr is not null) Rebuild();
    }

    private void Rebuild()
    {
        PluginRows.Children.Clear();
        var plugins = _mgr.Registry.All.ToList();
        if (plugins.Count == 0)
            PluginRows.Children.Add(Dim(Loc.T("pluginManager.empty")));

        foreach (var p in plugins)
        {
            var card = new Border
            {
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(3),
                Padding = new Avalonia.Thickness(10, 8),
            };
            card.Bind(Border.BackgroundProperty, this.GetResourceObservable("ChromeCanvas"));
            card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("ChromeBorder"));
            var col = new StackPanel { Spacing = 3 };

            var top = new DockPanel { LastChildFill = false };
            var name = Text(p.Manifest?.Name ?? p.Id, bold: true);
            DockPanel.SetDock(name, Dock.Left);
            top.Children.Add(name);

            var btn = new Button { Classes = { "opt" }, Padding = new Avalonia.Thickness(12, 0), Tag = p.Id };
            bool active = p.State == PluginState.Active;
            btn.Content = active ? Loc.T("pluginManager.disable") : Loc.T("pluginManager.enable");
            btn.IsEnabled = p.State is PluginState.Active or PluginState.Loaded or PluginState.Disabled;
            btn.Click += OnToggle;
            DockPanel.SetDock(btn, Dock.Right);
            top.Children.Add(btn);
            col.Children.Add(top);

            col.Children.Add(Dim($"{p.Id}  ·  {p.State}{(p.CrashCount > 0 ? $"  ·  {Loc.T("pluginManager.crashes", p.CrashCount)}" : "")}"));
            if (p.Manifest is { } m) col.Children.Add(Dim(Loc.T("pluginManager.capabilities", string.Join(", ", m.Capabilities))));
            foreach (var err in p.Errors) col.Children.Add(Warn(err));

            card.Child = col;
            PluginRows.Children.Add(card);
        }

        var entries = _log.Entries.TakeLast(60).Select(e => $"[{e.PluginId}] {e.Level}: {e.Message}");
        LogText.Text = string.Join("\n", entries);
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var p = _mgr.Registry.Get(id);
        if (p is null) return;
        if (p.State == PluginState.Active) _mgr.Disable(id);
        else _mgr.Enable(id);
        Rebuild();
    }

    private void OnReload(object? sender, RoutedEventArgs e)
    {
        _mgr.LoadAll();
        Rebuild();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private TextBlock Text(string s, bool bold = false)
    {
        var tb = new TextBlock { Text = s, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, FontSize = 12 };
        tb.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ChromeText"));
        return tb;
    }

    private TextBlock Dim(string s)
    {
        var tb = new TextBlock { Text = s, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        tb.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ChromeTextFaint"));
        return tb;
    }

    private TextBlock Warn(string s)
    {
        var tb = new TextBlock { Text = s, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        tb.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ChromeWarn"));
        return tb;
    }
}
