using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
    private readonly ulong _freeVram;     // 0 = unknown (probe stub) → VRAM badges show requirement only
    private bool _busy;
    private bool _syncingDefault;         // guard ComboBox SelectionChanged fired while (re)building rows

    /// <summary>Raised when the user changes a per-task default — host refreshes which model serves each op.</summary>
    public event Action? DefaultsChanged;

    /// <summary>Raised when external model sources change — host persists them to settings (§2.1).</summary>
    public event Action? SourcesChanged;

    /// <summary>Raised when the user asks to diagnose the generative sidecar Python env (§3). Host resolves
    /// the interpreter (pinned / ComfyUI venv / own) and reports what it would use.</summary>
    public event Action? CheckSidecarRequested;

    public ModelsWindow() : this(new ModelRegistry(System.IO.Path.GetTempPath()), 0) { }

    public ModelsWindow(ModelRegistry registry, ulong freeVram = 0)
    {
        InitializeComponent();
        _registry = registry;
        _freeVram = freeVram;
        _downloader = new ModelDownloader(registry);
        FolderLabel.Text = $"Model folder: {registry.ModelsFolder}";
        BuildSources();
        BuildRecommended();
        BuildDefaults();
        BuildInstalled();
    }

    /// <summary>List external sources (ComfyUI/folder) with their model count + a Remove button.</summary>
    private void BuildSources()
    {
        SourceRows.Children.Clear();
        foreach (var src in _registry.Sources)
        {
            if (src.Kind == ModelSourceKind.Native) continue;
            int n = _registry.Catalog.All.Count(m => string.Equals(m.SourceId, src.Id, StringComparison.OrdinalIgnoreCase));

            var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(Text(src.Path, "ChromeTextDim", 12));
            info.Children.Add(Text($"{src.Kind} · {n} model(s)" + (src.ReadOnly ? " · read-only" : ""), "ChromeTextFaint", 11));

            var btn = new Button
            {
                Content = "Remove", Classes = { "opt" }, Padding = new Avalonia.Thickness(14, 0),
                Tag = src.Id, VerticalAlignment = VerticalAlignment.Center,
            };
            btn.Click += OnRemoveSource;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(info);
            Grid.SetColumn(btn, 1);
            row.Children.Add(btn);

            var border = new Border { CornerRadius = new Avalonia.CornerRadius(4), Padding = new Avalonia.Thickness(10, 6), Child = row };
            Bg(border, "ChromePanel2");
            SourceRows.Children.Add(border);
        }
    }

    private async void OnAddComfyFolder(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the ComfyUI folder (or its models folder)", AllowMultiple = false,
        });
        var raw = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(raw)) return;

        _busy = true;
        AddSourceBtn.IsEnabled = false;
        // resolve the ComfyUI root → its models folder, then scan off the UI thread (large/remote trees hang it)
        ShowStatus("Scanning models…");
        try
        {
            var path = await System.Threading.Tasks.Task.Run(() => SourceScanner.ResolveModelsRoot(raw));
            var id = UniqueSourceId(path);
            await System.Threading.Tasks.Task.Run(() => _registry.AddSource(ModelSource.Comfy(id, path)));
            SourcesChanged?.Invoke();
            DefaultsChanged?.Invoke();
            BuildSources();
            BuildDefaults();
            BuildInstalled();
            int n = _registry.Catalog.All.Count(m => m.SourceId == id);
            ShowStatus($"Added source ({n} model(s)): {path}");
        }
        catch (Exception ex) { ShowStatus($"Add source failed: {ex.Message}"); }
        finally { _busy = false; AddSourceBtn.IsEnabled = true; }
    }

    private async void OnRemoveSource(object? sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string id }) return;
        _busy = true;
        try { await System.Threading.Tasks.Task.Run(() => _registry.RemoveSource(id)); }
        finally { _busy = false; }
        SourcesChanged?.Invoke();
        DefaultsChanged?.Invoke();
        BuildSources();
        BuildDefaults();
        BuildInstalled();
    }

    /// <summary>Stable, collision-free source id from a folder path ("comfy-&lt;leaf&gt;", numbered on clash).</summary>
    private string UniqueSourceId(string path)
    {
        var leaf = new string((System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)) ?? "comfy")
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (leaf.Length == 0) leaf = "comfy";
        var baseId = $"comfy-{leaf.ToLowerInvariant()}";
        var id = baseId; int i = 2;
        while (_registry.Sources.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
            id = $"{baseId}-{i++}";
        return id;
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
            info.Children.Add(Text($"{rec.Family} · {Mb(rec.SizeBytes)} MB download", "ChromeTextDim", 11));
            info.Children.Add(VramBadgeText(rec.VramBytes));
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

    // A coloured "x.x GB VRAM · fits/tight/won't fit" badge (requirement only when free VRAM is unknown).
    private TextBlock VramBadgeText(long vramBytes)
    {
        var b = VramBadge.ForModel(vramBytes, _freeVram);
        var tb = new TextBlock { Text = b.Text, FontSize = 11 };
        if (b.Fit == VramFit.Unknown) Fg(tb, "ChromeTextFaint");
        else tb.Foreground = new SolidColorBrush(b.Fit switch
        {
            VramFit.Fits => Color.Parse("#FF5FB35F"),
            VramFit.Tight => Color.Parse("#FFD8A032"),
            _ => Color.Parse("#FFCF5B5B"),
        });
        return tb;
    }

    // Friendly task names for the defaults section (only the light-tier tasks have installed models).
    private static readonly (AiTaskKind Task, string Label)[] TaskLabels =
    {
        (AiTaskKind.Matte, "Background removal"),
        (AiTaskKind.Segment, "Smart selection"),
        (AiTaskKind.Upscale, "Upscale"),
        (AiTaskKind.Inpaint, "Object removal"),
    };

    /// <summary>Per-task default-model picker — only tasks with at least one installed model appear.</summary>
    private void BuildDefaults()
    {
        DefaultRows.Children.Clear();
        bool any = false;
        _syncingDefault = true;
        foreach (var (task, label) in TaskLabels)
        {
            var models = _registry.Catalog.ForTask(task).ToList();
            if (models.Count == 0) continue;
            any = true;

            var combo = new ComboBox { MinWidth = 240, FontSize = 12, Tag = task, VerticalAlignment = VerticalAlignment.Center };
            foreach (var m in models) combo.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Id });
            var def = _registry.DefaultFor(task);
            combo.SelectedIndex = def is null ? 0 : Math.Max(0, models.FindIndex(m => m.Id == def.Id));
            combo.SelectionChanged += OnDefaultChanged;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*") };
            row.Children.Add(Text(label, "ChromeTextDim", 12));
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            DefaultRows.Children.Add(row);
        }
        _syncingDefault = false;
        DefaultsHeader.IsVisible = any;
    }

    private void OnDefaultChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingDefault || sender is not ComboBox { Tag: AiTaskKind task, SelectedItem: ComboBoxItem { Tag: string id } }) return;
        _registry.SetDefault(task, id);
        DefaultsChanged?.Invoke();
    }

    private void BuildInstalled()
    {
        InstalledRows.Children.Clear();
        // NOTE: do NOT re-scan here — the registry is already loaded by the host; a Load() on every open
        // re-walks the whole (possibly remote) ComfyUI tree and hangs the dialog.
        var all = _registry.Catalog.All;
        if (all.Count == 0)
        {
            InstalledRows.Children.Add(Text("No models installed yet.", "ChromeTextFaint", 11));
            return;
        }
        foreach (var m in all)
        {
            var info = new StackPanel { Spacing = 1 };
            info.Children.Add(Text($"{m.Name}  ·  {m.Family}  ·  {string.Join(", ", m.Tasks)}", "ChromeTextDim", 12));
            info.Children.Add(VramBadgeText(m.VramBytes));
            InstalledRows.Children.Add(info);
        }
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
        BuildDefaults();
        BuildInstalled();
    }

    private void OnRemoveRecommended(object? sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string id }) return;
        _registry.Remove(id);
        ShowStatus($"Removed {RecommendedModels.ById(id)?.Name ?? id}.");
        DefaultsChanged?.Invoke();
        BuildRecommended();
        BuildDefaults();
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
            DefaultsChanged?.Invoke();
            BuildRecommended();
            BuildDefaults();
            BuildInstalled();
        }
        catch (Exception ex) { ShowStatus($"Download failed: {ex.Message}"); }
        finally { _busy = false; }
    }

    private void ShowStatus(string text) { Status.Text = text; Status.IsVisible = true; }

    private static long Mb(long bytes) => bytes / (1024 * 1024);

    private void OnCheckSidecar(object? sender, RoutedEventArgs e) => CheckSidecarRequested?.Invoke();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
