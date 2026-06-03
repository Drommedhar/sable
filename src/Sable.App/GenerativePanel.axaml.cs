using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Sable.Ai.Models;
using Sable.Core.Ai;

namespace Sable.App;

/// <summary>The parameters a Generative Fill run needs (PHASE8_AI_SIDECAR §4.1). Image/mask are added by
/// the host from the active layer + selection.</summary>
public sealed record GenFillRequest(
    string BaseId, string Prompt, string Negative, int Steps, double Cfg, long Seed, bool Offload,
    IReadOnlyList<AdapterRef> Loras);

/// <summary>
/// Modeless Generative Fill panel (PHASE8_AI_SIDECAR §4.1): pick the inpaint base (across all sources,
/// ComfyUI checkpoints included), a compatible LoRA stack with per-LoRA weights, prompt/negative, and
/// steps/cfg/seed/offload. The host (MainWindow) reads the active layer + selection, runs
/// <c>AiService.GenerativeFillAsync</c>, and deposits the result as an undoable layer. Rows are built in
/// code with theme-bound colours (a plain code-time lookup misses the active theme variant).
/// </summary>
public partial class GenerativePanel : Window
{
    private readonly ModelRegistry _reg;
    private ComboBox _modelCombo = null!;
    private TextBox _prompt = null!, _negative = null!, _steps = null!, _cfg = null!, _seed = null!;
    private CheckBox _offload = null!;
    private StackPanel _loraRows = null!;
    private TextBlock _status = null!;
    private Button _generate = null!;
    private readonly List<(string Id, CheckBox Cb, TextBox Weight)> _loras = new();

    /// <summary>Raised when the user clicks Generate with a valid base model.</summary>
    public event Action<GenFillRequest>? GenerateRequested;

    public GenerativePanel() : this(new ModelRegistry(System.IO.Path.GetTempPath())) { }

    public GenerativePanel(ModelRegistry reg)
    {
        InitializeComponent();
        _reg = reg;
        BuildUi();
    }

    private void Fg(TextBlock tb) => tb.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ChromeText"));

    private TextBlock Label(string text, double size = 12)
    {
        var tb = new TextBlock { Text = text, FontSize = size, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        Fg(tb);
        return tb;
    }

    private void BuildUi()
    {
        // --- model ---
        Root.Children.Add(Label("Model (inpaint base)"));
        _modelCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        // generative bases only (LaMa inpaint is the light tier — that's "Remove Object", not gen fill)
        foreach (var m in _reg.Catalog.ForTask(AiTaskKind.Inpaint).Where(m => m.Tier == AiTier.Generative))
            _modelCombo.Items.Add(new ComboBoxItem { Content = $"{m.Name}  ({m.SourceId ?? "native"})", Tag = m.Id });
        _modelCombo.SelectionChanged += (_, _) => BuildLoras();
        if (_modelCombo.Items.Count > 0) _modelCombo.SelectedIndex = 0;
        Root.Children.Add(_modelCombo);

        // --- LoRA stack ---
        Root.Children.Add(Label("LoRAs (compatible)"));
        _loraRows = new StackPanel { Spacing = 2 };
        Root.Children.Add(_loraRows);

        // --- prompt / negative ---
        Root.Children.Add(Label("Prompt"));
        _prompt = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
        Root.Children.Add(_prompt);
        Root.Children.Add(Label("Negative prompt"));
        _negative = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 40 };
        Root.Children.Add(_negative);

        // --- params ---
        _steps = NumRow("Steps", "25");
        _cfg = NumRow("CFG scale", "7.0");
        _seed = NumRow("Seed (-1 = random)", "-1", out var randomBtn);
        randomBtn!.Click += (_, _) => _seed.Text = new Random().Next(0, int.MaxValue).ToString(CultureInfo.InvariantCulture);

        _offload = new CheckBox { Content = "CPU offload (fit big models)", FontSize = 12, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        Root.Children.Add(_offload);

        // --- generate + status ---
        _generate = new Button { Content = "Generate", Classes = { "opt" }, Padding = new Avalonia.Thickness(18, 4), Margin = new Avalonia.Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        _generate.Click += OnGenerate;
        Root.Children.Add(_generate);

        _status = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        Fg(_status);
        Root.Children.Add(_status);

        if (_modelCombo.Items.Count == 0)
        {
            SetStatus("No inpaint-capable model. Add a ComfyUI source with checkpoints, or install one in Models.");
            _generate.IsEnabled = false;
        }
        BuildLoras();
    }

    private TextBox NumRow(string label, string def) => NumRow(label, def, out _);

    private TextBox NumRow(string label, string def, out Button? randomBtn)
    {
        randomBtn = null;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110"), Margin = new Avalonia.Thickness(0, 2, 0, 0) };
        var lbl = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Fg(lbl);
        grid.Children.Add(lbl);
        var box = new TextBox { Text = def, FontSize = 12, MinWidth = 70 };
        if (label.StartsWith("Seed", StringComparison.Ordinal))
        {
            var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            inner.Children.Add(box);
            randomBtn = new Button { Content = "Random", Classes = { "opt" }, Padding = new Avalonia.Thickness(8, 0), Margin = new Avalonia.Thickness(4, 0, 0, 0), FontSize = 11 };
            Grid.SetColumn(randomBtn, 1);
            inner.Children.Add(randomBtn);
            Grid.SetColumn(inner, 1);
            grid.Children.Add(inner);
        }
        else
        {
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
        }
        Root.Children.Add(grid);
        return box;
    }

    private void BuildLoras()
    {
        _loraRows.Children.Clear();
        _loras.Clear();
        var baseId = (_modelCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var baseModel = baseId is null ? null : _reg.Catalog.ById(baseId);
        if (baseModel is null) return;

        var compat = _reg.Catalog.AdaptersFor(baseModel).ToList();
        if (compat.Count == 0)
        {
            var none = new TextBlock { Text = "(no compatible LoRAs found)", FontSize = 11 };
            none.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ChromeTextFaint"));
            _loraRows.Children.Add(none);
            return;
        }
        foreach (var l in compat)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,70") };
            var cb = new CheckBox { Content = l.Name, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(cb);
            var w = new TextBox { Text = l.DefaultWeight.ToString("0.0#", CultureInfo.InvariantCulture), FontSize = 11, MinWidth = 50 };
            Grid.SetColumn(w, 1);
            row.Children.Add(w);
            _loraRows.Children.Add(row);
            _loras.Add((l.Id, cb, w));
        }
    }

    private void OnGenerate(object? sender, RoutedEventArgs e)
    {
        var baseId = (_modelCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrEmpty(baseId)) { SetStatus("Pick a model first."); return; }

        int steps = ParseInt(_steps.Text, 25);
        double cfg = ParseDouble(_cfg.Text, 7.0);
        long seed = ParseLong(_seed.Text, -1);

        var loras = new List<AdapterRef>();
        foreach (var (id, cb, weight) in _loras)
            if (cb.IsChecked == true) loras.Add(new AdapterRef(id, ParseDouble(weight.Text, 1.0)));

        GenerateRequested?.Invoke(new GenFillRequest(
            baseId, _prompt.Text ?? "", _negative.Text ?? "", steps, cfg, seed, _offload.IsChecked == true, loras));
    }

    public void SetStatus(string text) => _status.Text = text;

    private static int ParseInt(string? s, int def) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    private static long ParseLong(string? s, long def) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    private static double ParseDouble(string? s, double def) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
}
