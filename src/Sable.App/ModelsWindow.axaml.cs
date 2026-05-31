using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sable.Ai.Download;
using Sable.Ai.Models;
using Sable.Core.Ai;

namespace Sable.App;

/// <summary>
/// Minimal model manager (PHASE8_AI §4): curated "recommended" downloads (pointers — licence shown,
/// weights fetched from the source), a paste-any-URL box, and the installed list. The full panel
/// (per-task defaults, VRAM badges, LoRA stacks) is slice 8.5; this gives users a way to acquire a
/// model now. Rows are built in code, with theme colours bound via resource observables (so they
/// resolve the active theme variant + re-theme live — a plain code-time lookup misses the variant).
/// </summary>
public partial class ModelsWindow : Window
{
    private readonly ModelRegistry _registry;
    private readonly ModelDownloader _downloader;
    private bool _busy;

    public ModelsWindow() : this(new ModelRegistry(System.IO.Path.GetTempPath())) { }

    public ModelsWindow(ModelRegistry registry)
    {
        InitializeComponent();
        _registry = registry;
        _downloader = new ModelDownloader(registry);
        FolderLabel.Text = $"Model folder: {registry.ModelsFolder}";
        BuildRecommended();
        BuildInstalled();
    }

    private void Fg(TextBlock tb, string key) => tb.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(key));
    private void Bg(Border b, string key) => b.Bind(Border.BackgroundProperty, this.GetResourceObservable(key));

    private TextBlock Text(string text, string fgKey, double size = 13, bool wrap = false)
    {
        var tb = new TextBlock { Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        Fg(tb, fgKey);
        return tb;
    }

    private void BuildRecommended()
    {
        RecoRows.Children.Clear();
        foreach (var rec in RecommendedModels.All)
        {
            var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(Text(rec.Name, "ChromeText"));
            info.Children.Add(Text($"{rec.Family} · {Mb(rec.SizeBytes)} MB download · ~{Mb(rec.VramBytes)} MB VRAM", "ChromeTextDim", 11));
            info.Children.Add(Text(rec.License, "ChromeTextFaint", 11, wrap: true));

            bool installed = _registry.IsInstalled(rec.Id);
            var btn = new Button
            {
                Content = installed ? "Remove" : "Download", Classes = { "opt" },
                Padding = new Avalonia.Thickness(14, 0), Tag = rec.Id, VerticalAlignment = VerticalAlignment.Center,
            };
            if (installed) btn.Click += OnRemoveRecommended;
            else btn.Click += OnDownloadRecommended;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(info);
            Grid.SetColumn(btn, 1);
            row.Children.Add(btn);

            var border = new Border
            {
                CornerRadius = new Avalonia.CornerRadius(4),
                Padding = new Avalonia.Thickness(10, 8),
                Child = row,
            };
            Bg(border, "ChromePanel2");
            RecoRows.Children.Add(border);
        }
    }

    private void BuildInstalled()
    {
        InstalledRows.Children.Clear();
        try { _registry.Load(); } catch { /* ignore */ }
        var all = _registry.Catalog.All;
        if (all.Count == 0)
        {
            InstalledRows.Children.Add(Text("No models installed yet.", "ChromeTextFaint", 11));
            return;
        }
        foreach (var m in all)
            InstalledRows.Children.Add(Text($"{m.Name}  ·  {m.Family}  ·  {string.Join(", ", m.Tasks)}", "ChromeTextDim", 12));
    }

    private async void OnDownloadRecommended(object? sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string id } || RecommendedModels.ById(id) is not { } rec) return;
        await RunCycle(new[] { rec });   // licence cycle (scroll-to-accept) installs it
    }

    /// <summary>Run the sequential licence-cycle dialog (it installs the accepted models), then refresh.</summary>
    private async System.Threading.Tasks.Task RunCycle(IReadOnlyList<RecommendedModel> models)
    {
        if (models.Count == 0) return;
        var win = new LicenseCycleWindow(models, _downloader) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        await win.ShowDialog<List<RecommendedModel>?>(this);
        BuildRecommended();
        BuildInstalled();
    }

    private void OnRemoveRecommended(object? sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string id }) return;
        _registry.Remove(id);
        ShowStatus($"Removed {RecommendedModels.ById(id)?.Name ?? id}.");
        BuildRecommended();
        BuildInstalled();
    }

    private async void OnDownloadSet(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var notInstalled = RecommendedModels.DefaultSet.Where(m => !_registry.IsInstalled(m.Id)).ToList();
        if (notInstalled.Count == 0) { ShowStatus("All recommended models already installed."); return; }
        await RunCycle(notInstalled);   // licence cycle installs the accepted ones
    }

    private async void OnDownloadUrl(object? sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text?.Trim();
        if (_busy || string.IsNullOrWhiteSpace(url)) return;
        await RunDownload(url, p => _downloader.DownloadAsync(url, p, CancellationToken.None));
    }

    private async System.Threading.Tasks.Task RunDownload(string label, Func<IProgress<double>, System.Threading.Tasks.Task<ModelManifest>> run)
    {
        _busy = true;
        ShowStatus($"Downloading {label}…");
        var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => ShowStatus($"Downloading {label}… {p * 100:0}%")));
        try
        {
            var m = await run(progress);
            ShowStatus($"Installed {m.Name}.");
            BuildRecommended();
            BuildInstalled();
        }
        catch (Exception ex) { ShowStatus($"Download failed: {ex.Message}"); }
        finally { _busy = false; }
    }

    private void ShowStatus(string text) { Status.Text = text; Status.IsVisible = true; }

    private static long Mb(long bytes) => bytes / (1024 * 1024);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
