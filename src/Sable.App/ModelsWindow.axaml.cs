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
/// Model manager (PHASE8_AI §4 / §8.5). Two tabs: <b>ONNX</b> (the in-process light tier — a card
/// list of recommended + installed models with a VRAM-fit badge, install/remove, and per-task default
/// integrated onto each card) and <b>Generative</b> (a placeholder until the opt-in Diffusers sidecar
/// ships). Cards are built in code, with theme colours bound via resource observables (so they resolve
/// the active theme variant + re-theme live — a plain code-time lookup misses the variant).
/// </summary>
public partial class ModelsWindow : Window
{
    private readonly ModelRegistry _registry;
    private readonly ModelDownloader _downloader;
    private ulong _freeVram;     // 0 = unknown → VRAM badges show requirement only (filled by the bg probe)
    private bool _busy;
    private bool _closed;

    /// <summary>Raised when the user changes a per-task default — host refreshes which model serves each op.</summary>
    public event Action? DefaultsChanged;

    /// <summary>Raised after the model folder is changed + models moved (arg = the new folder). The host
    /// persists the choice and rebuilds the AI backend; the registry itself is already re-pointed.</summary>
    public event Action<string>? ModelsFolderChanged;

    public ModelsWindow() : this(new ModelRegistry(System.IO.Path.GetTempPath())) { }

    public ModelsWindow(ModelRegistry registry, Sable.Ai.Gpu.GpuProbe? probe = null)
    {
        InitializeComponent();
        _registry = registry;
        _downloader = new ModelDownloader(registry);
        FolderLabel.Text = $"Model folder: {registry.ModelsFolder}";
        Closed += (_, _) => _closed = true;
        BuildOnnxCards();   // instant; VRAM badges show requirement-only until the probe returns
        // free-VRAM probe shells out to nvidia-smi (slow, ~seconds cold) — never block the dialog on it.
        // Run it on a background thread and re-render the fit badges once it lands.
        if (probe is not null) _ = ProbeVramAsync(probe);
    }

    private async System.Threading.Tasks.Task ProbeVramAsync(Sable.Ai.Gpu.GpuProbe probe)
    {
        ulong free;
        try { free = await System.Threading.Tasks.Task.Run(probe.FreeVramBytes); }
        catch { return; }
        if (_closed || free == _freeVram) return;
        _freeVram = free;
        BuildOnnxCards();   // back on the UI thread (Avalonia sync context) → re-render with the fit verdict
    }

    // Friendly task names for the default-model chip on each installed card.
    private static readonly (AiTaskKind Task, string Label)[] TaskLabels =
    {
        (AiTaskKind.Matte, "Background removal"),
        (AiTaskKind.Segment, "Smart selection"),
        (AiTaskKind.Upscale, "Upscale"),
        (AiTaskKind.Inpaint, "Object removal"),
        (AiTaskKind.Denoise, "Denoise"),
    };

    private static string TaskLabel(AiTaskKind t) => TaskLabels.FirstOrDefault(x => x.Task == t).Label ?? t.ToString();

    // accent blue (matches App.axaml checked/selection accent)
    private static readonly Color Accent = Color.Parse("#FF3A6EA5");

    private void Fg(TextBlock tb, string key) => tb.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(key));
    private void Bg(Border b, string key) => b.Bind(Border.BackgroundProperty, this.GetResourceObservable(key));
    private void Bd(Border b, string key) => b.Bind(Border.BorderBrushProperty, this.GetResourceObservable(key));

    private TextBlock Text(string text, string fgKey, double size = 13, bool wrap = false, FontWeight weight = FontWeight.Normal)
    {
        var tb = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        Fg(tb, fgKey);
        return tb;
    }

    // ---- ONNX card list ---------------------------------------------------------------

    private void BuildOnnxCards()
    {
        OnnxCards.Children.Clear();
        try { _registry.Load(); } catch { /* ignore */ }

        // recommended catalog (light tier) — one card each, install state from the registry
        foreach (var rec in RecommendedModels.All.Where(m => m.Tier == AiTier.Light))
        {
            bool installed = _registry.IsInstalled(rec.Id);
            OnnxCards.Children.Add(MakeCard(
                rec.Name, rec.Family, rec.SizeBytes, rec.VramBytes, rec.Tasks, rec.License,
                installed, rec.Id,
                download: async () => await RunCycle(new[] { rec }),   // licence cycle installs it
                remove: () => RemoveModel(rec.Id, rec.Name)));
        }

        // installed models not in the catalog (custom URL / HF downloads)
        var known = RecommendedModels.All.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _registry.Catalog.All.Where(m => m.Tier == AiTier.Light && !known.Contains(m.Id)))
        {
            var id = m.Id;
            OnnxCards.Children.Add(MakeCard(
                m.Name, m.Family, 0, m.VramBytes, m.Tasks, license: null,
                installed: true, id,
                download: null,
                remove: () => RemoveModel(id, m.Name)));
        }
    }

    /// <summary>One model card: name + VRAM pill, meta line, licence, and the action row
    /// (install/remove + the per-task "default" chip when installed).</summary>
    private Border MakeCard(
        string name, string family, long sizeBytes, long vramBytes,
        IReadOnlyList<AiTaskKind> tasks, string? license,
        bool installed, string id, Func<System.Threading.Tasks.Task>? download, Action remove)
    {
        var stack = new StackPanel { Spacing = 4 };

        // header: name (left) + VRAM pill (right)
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(Text(name, "ChromeText", 14, weight: FontWeight.SemiBold));
        var pill = MakeVramPill(vramBytes);
        Grid.SetColumn(pill, 1);
        header.Children.Add(pill);
        stack.Children.Add(header);

        // meta: family · size · tasks
        var meta = new List<string> { family };
        if (sizeBytes > 0) meta.Add($"{Mb(sizeBytes)} MB");
        if (tasks.Count > 0) meta.Add(string.Join(", ", tasks.Select(TaskLabel)));
        stack.Children.Add(Text(string.Join("  ·  ", meta), "ChromeTextDim", 11));

        if (!string.IsNullOrEmpty(license))
            stack.Children.Add(Text(license!, "ChromeTextFaint", 11, wrap: true));

        // action row: install/remove + default chip
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(0, 4, 0, 0) };
        var actionBtn = new Button
        {
            Content = installed ? "Remove" : "Download",
            Classes = { "opt" },
            Padding = new Avalonia.Thickness(14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (installed) actionBtn.Click += (_, _) => remove();
        else if (download is not null) actionBtn.Click += async (_, _) => { if (!_busy) await download(); };
        actions.Children.Add(actionBtn);

        if (installed && tasks.Count > 0) actions.Children.Add(DefaultChip(tasks[0], id));
        stack.Children.Add(actions);

        var card = new Border
        {
            CornerRadius = new Avalonia.CornerRadius(6),
            BorderThickness = new Avalonia.Thickness(1),
            Padding = new Avalonia.Thickness(12, 10),
            Child = stack,
        };
        Bg(card, "ChromePanel2");
        Bd(card, "ChromeBorder");
        return card;
    }

    /// <summary>Per-task default control on an installed card: an accent chip when it IS the default,
    /// else a clickable "Set default" pill (only meaningful when ≥2 models serve the task).</summary>
    private Control DefaultChip(AiTaskKind task, string id)
    {
        int rivals = _registry.Catalog.ForTask(task).Count();
        bool isDefault = _registry.DefaultFor(task)?.Id == id;
        string label = $"Default · {TaskLabel(task)}";

        if (isDefault)
        {
            var chip = new Border
            {
                CornerRadius = new Avalonia.CornerRadius(10),
                Padding = new Avalonia.Thickness(10, 2),
                Background = new SolidColorBrush(Color.FromArgb(0x33, Accent.R, Accent.G, Accent.B)),
                BorderThickness = new Avalonia.Thickness(1),
                BorderBrush = new SolidColorBrush(Accent),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Accent) },
            };
            return chip;
        }

        // not the default but alternatives exist → let the user switch
        var btn = new Button
        {
            Content = $"Set default · {TaskLabel(task)}",
            Classes = { "opt" },
            FontSize = 11,
            Padding = new Avalonia.Thickness(10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        btn.Click += (_, _) =>
        {
            _registry.SetDefault(task, id);
            DefaultsChanged?.Invoke();
            BuildOnnxCards();
        };
        // hide the switch when there is no choice to make (single provider is already the default)
        btn.IsVisible = rivals > 1;
        return btn;
    }

    /// <summary>Coloured VRAM-fit pill: requirement always shown; fit verdict (fits/tight/won't fit)
    /// only when free VRAM is known (probe stub returns 0 → neutral requirement-only pill).</summary>
    private Border MakeVramPill(long vramBytes)
    {
        var b = VramBadge.ForModel(vramBytes, _freeVram);
        var tb = new TextBlock { Text = b.Text, FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var pill = new Border
        {
            CornerRadius = new Avalonia.CornerRadius(10),
            Padding = new Avalonia.Thickness(9, 2),
            BorderThickness = new Avalonia.Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = tb,
        };
        if (b.Fit == VramFit.Unknown)
        {
            Bg(pill, "ChromePanel3");
            Bd(pill, "ChromeBorder");
            Fg(tb, "ChromeTextDim");
        }
        else
        {
            var c = b.Fit switch
            {
                VramFit.Fits => Color.Parse("#FF5FB35F"),
                VramFit.Tight => Color.Parse("#FFD8A032"),
                _ => Color.Parse("#FFCF5B5B"),
            };
            tb.Foreground = new SolidColorBrush(c);
            pill.Background = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
            pill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, c.R, c.G, c.B));
        }
        return pill;
    }

    // ---- actions ----------------------------------------------------------------------

    /// <summary>Run the sequential licence-cycle dialog (it installs the accepted models), then refresh.</summary>
    private async System.Threading.Tasks.Task RunCycle(IReadOnlyList<RecommendedModel> models)
    {
        if (_busy || models.Count == 0) return;
        _busy = true;
        try
        {
            var win = new LicenseCycleWindow(models, _downloader) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
            await win.ShowDialog<List<RecommendedModel>?>(this);
            DefaultsChanged?.Invoke();
            BuildOnnxCards();
        }
        finally { _busy = false; }
    }

    private void RemoveModel(string id, string name)
    {
        if (_busy) return;
        _registry.Remove(id);
        ShowStatus($"Removed {name}.");
        DefaultsChanged?.Invoke();
        BuildOnnxCards();
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
        _busy = true;
        ShowStatus($"Downloading {url}…");
        var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => ShowStatus($"Downloading {url}… {p * 100:0}%")));
        try
        {
            var m = await _downloader.DownloadAsync(url, progress, CancellationToken.None);
            ShowStatus($"Installed {m.Name}.");
            DefaultsChanged?.Invoke();
            BuildOnnxCards();
        }
        catch (Exception ex) { ShowStatus($"Download failed: {ex.Message}"); }
        finally { _busy = false; }
    }

    /// <summary>Pick a new model folder and move the installed models there (paths inside each
    /// model.json are rewritten by the registry). Confirmed first; runs off the UI thread with progress.</summary>
    private async void OnChangeFolder(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var picks = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Choose model folder",
            AllowMultiple = false,
        });
        var target = picks.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(target)) return;
        if (string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(_registry.ModelsFolder),
            StringComparison.OrdinalIgnoreCase)) return;   // same folder → nothing to do

        bool ok = await ConfirmWindow.Ask(this, "Move models",
            $"Move installed models from\n{_registry.ModelsFolder}\nto\n{target}?");
        if (!ok) return;

        _busy = true;
        var busy = BusyWindow.Begin(this, "Moving models…");
        try
        {
            var progress = busy.Progress;
            await System.Threading.Tasks.Task.Run(() => _registry.MoveTo(target, progress));
            FolderLabel.Text = $"Model folder: {_registry.ModelsFolder}";
            ModelsFolderChanged?.Invoke(_registry.ModelsFolder);
            BuildOnnxCards();
            ShowStatus($"Models moved to {_registry.ModelsFolder}.");
        }
        catch (Exception ex) { ShowStatus($"Move failed: {ex.Message}"); }
        finally { busy.Done(); _busy = false; }
    }

    private void ShowStatus(string text) { Status.Text = text; Status.IsVisible = true; }

    private static long Mb(long bytes) => bytes / (1024 * 1024);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
