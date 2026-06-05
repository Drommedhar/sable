using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Sable.Ai.Models;
using Sable.App.Localization;
using Sable.Core.Ai;

namespace Sable.App;

/// <summary>What a Generative run needs: the chosen preset (base + encoder(s) + VAE + workflow) + prompt +
/// params + a LoRA stack. The image/mask are added by the host from the active layer + selection.</summary>
public sealed record GenFillRequest(
    GenerativePreset Preset, string Prompt, string Negative, int Steps, double Cfg, long Seed, double Denoise,
    bool Offload, IReadOnlyList<AdapterRef> Loras);

/// <summary>
/// Modeless Generative dialog (PHASE8_AI_COMFY). The model is chosen from the user's configured PRESETS
/// (Models ▸ Generative) — which pin base + encoder(s) + VAE + workflow — so here the user only picks a
/// preset, a LoRA stack, the prompt, and a few params. Rows built in code with theme-bound colours.
/// </summary>
public partial class GenerativePanel : Window
{
    private readonly ModelRegistry _reg;
    private readonly List<GenerativePreset> _presets;
    private ComboBox _presetCombo = null!;
    private TextBox _prompt = null!, _negative = null!, _steps = null!, _cfg = null!, _seed = null!, _denoise = null!;
    private CheckBox _offload = null!;
    private StackPanel _loraRows = null!;
    private TextBlock _status = null!;
    private Button _generate = null!;
    private readonly List<(string Id, CheckBox Cb, TextBox Weight)> _loras = new();

    public event Action<GenFillRequest>? GenerateRequested;

    /// <summary>This panel's mode: true = text-to-image presets, false = fill/edit presets.</summary>
    public bool TextToImage { get; }

    public GenerativePanel() : this(new ModelRegistry(System.IO.Path.GetTempPath()), System.Array.Empty<GenerativePreset>()) { }

    public GenerativePanel(ModelRegistry reg, IReadOnlyList<GenerativePreset> presets, bool textToImage = false)
    {
        InitializeComponent();
        _reg = reg;
        TextToImage = textToImage;
        _presets = presets.Where(p => p.IsTextToImage == textToImage).ToList();
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
        Root.Children.Add(Label(Loc.T("generative.modelLabel")));
        _presetCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        foreach (var p in _presets) _presetCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p });
        _presetCombo.SelectionChanged += (_, _) => { BuildLoras(); LoadWorkflowDefaults(); };
        if (_presetCombo.Items.Count > 0) _presetCombo.SelectedIndex = 0;
        Root.Children.Add(_presetCombo);

        Root.Children.Add(Label(Loc.T("generative.lorasLabel")));
        _loraRows = new StackPanel { Spacing = 2 };
        Root.Children.Add(_loraRows);

        Root.Children.Add(Label(Loc.T("generative.prompt")));
        _prompt = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
        Root.Children.Add(_prompt);
        Root.Children.Add(Label(Loc.T("generative.negativePrompt")));
        _negative = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 40 };
        Root.Children.Add(_negative);

        _steps = NumRow(Loc.T("generative.steps"), "25", isSeed: false);
        _cfg = NumRow(Loc.T("generative.cfg"), "7.0", isSeed: false);
        _denoise = NumRow(Loc.T("generative.denoise"), "1.0", isSeed: false);
        _seed = NumRow(Loc.T("generative.seed"), "-1", out var randomBtn, isSeed: true);
        randomBtn!.Click += (_, _) => _seed.Text = new Random().Next(0, int.MaxValue).ToString(CultureInfo.InvariantCulture);

        _offload = new CheckBox { Content = Loc.T("generative.offload"), FontSize = 12, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        Root.Children.Add(_offload);

        _generate = new Button { Content = Loc.T("generative.generate"), Classes = { "opt" }, Padding = new Avalonia.Thickness(18, 4), Margin = new Avalonia.Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        _generate.Click += OnGenerate;
        Root.Children.Add(_generate);

        _status = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        Fg(_status);
        Root.Children.Add(_status);

        if (_presetCombo.Items.Count == 0)
        {
            SetStatus(Loc.T("generative.noPresets"));
            _generate.IsEnabled = false;
        }
        BuildLoras();
        LoadWorkflowDefaults();
    }

    /// <summary>Pre-fill steps/cfg from the selected preset's workflow file (best-effort).</summary>
    private void LoadWorkflowDefaults()
    {
        if (_steps is null || _cfg is null) return;
        var wf = SelectedPreset?.WorkflowFile;
        if (string.IsNullOrEmpty(wf) || !System.IO.File.Exists(wf)) return;
        try
        {
            var (s, c) = Sable.Ai.Comfy.Workflow.WorkflowTemplate.ReadDefaults(System.IO.File.ReadAllText(wf));
            if (s > 0) _steps.Text = s.ToString(CultureInfo.InvariantCulture);
            if (c > 0) _cfg.Text = c.ToString("0.##", CultureInfo.InvariantCulture);
        }
        catch { }
    }

    private TextBox NumRow(string label, string def, bool isSeed) => NumRow(label, def, out _, isSeed);

    private TextBox NumRow(string label, string def, out Button? randomBtn, bool isSeed)
    {
        randomBtn = null;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110"), Margin = new Avalonia.Thickness(0, 2, 0, 0) };
        var lbl = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Fg(lbl);
        grid.Children.Add(lbl);
        var box = new TextBox { Text = def, FontSize = 12, MinWidth = 70 };
        if (isSeed)
        {
            var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            inner.Children.Add(box);
            randomBtn = new Button { Content = Loc.T("generative.random"), Classes = { "opt" }, Padding = new Avalonia.Thickness(8, 0), Margin = new Avalonia.Thickness(4, 0, 0, 0), FontSize = 11 };
            Grid.SetColumn(randomBtn, 1);
            inner.Children.Add(randomBtn);
            Grid.SetColumn(inner, 1);
            grid.Children.Add(inner);
        }
        else { Grid.SetColumn(box, 1); grid.Children.Add(box); }
        Root.Children.Add(grid);
        return box;
    }

    private GenerativePreset? SelectedPreset => (_presetCombo.SelectedItem as ComboBoxItem)?.Tag as GenerativePreset;

    private void BuildLoras()
    {
        if (_loraRows is null) return;
        _loraRows.Children.Clear();
        _loras.Clear();
        var baseModel = SelectedPreset is { } p ? _reg.Catalog.ById(p.BaseModelId) : null;
        if (baseModel is null) return;

        var compat = _reg.Catalog.AdaptersFor(baseModel).ToList();
        if (compat.Count == 0)
        {
            var none = new TextBlock { Text = Loc.T("generative.noCompatibleLoras"), FontSize = 11 };
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
        if (SelectedPreset is not { } preset) { SetStatus(Loc.T("generative.pickModelFirst")); return; }

        var loras = new List<AdapterRef>();
        foreach (var (id, cb, weight) in _loras)
            if (cb.IsChecked == true) loras.Add(new AdapterRef(id, ParseDouble(weight.Text, 1.0)));

        GenerateRequested?.Invoke(new GenFillRequest(
            preset, _prompt.Text ?? "", _negative.Text ?? "",
            ParseInt(_steps.Text, 25), ParseDouble(_cfg.Text, 7.0), ParseLong(_seed.Text, -1),
            Math.Clamp(ParseDouble(_denoise.Text, 1.0), 0.0, 1.0), _offload.IsChecked == true, loras));
    }

    public void SetStatus(string text) => _status.Text = text;

    private static int ParseInt(string? s, int def) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    private static long ParseLong(string? s, long def) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    private static double ParseDouble(string? s, double def) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
}
