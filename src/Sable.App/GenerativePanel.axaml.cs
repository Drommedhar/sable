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

/// <summary>The parameters a Generative Fill run needs (PHASE8_AI_SIDECAR §4.1). Image/mask are added by
/// the host from the active layer + selection.</summary>
public sealed record GenFillRequest(
    string BaseId, string Prompt, string Negative, int Steps, double Cfg, long Seed, bool Offload,
    IReadOnlyList<AdapterRef> Loras,
    IReadOnlyList<string> EncoderIds, string? VaeId);   // assembled (diffusion_models) models: chosen encoder/VAE component ids

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
    private StackPanel _assembled = null!, _encoderRows = null!;
    private ComboBox _vaeCombo = null!;
    private TextBlock _status = null!;
    private Button _generate = null!;
    private readonly List<(string Id, CheckBox Cb, TextBox Weight)> _loras = new();
    private readonly List<(string Id, CheckBox Cb)> _encoders = new();

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
        Root.Children.Add(Label(Loc.T("generative.modelLabel")));
        _modelCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        // generative bases only (LaMa inpaint is the light tier — that's "Remove Object", not gen fill)
        foreach (var m in _reg.Catalog.ForTask(AiTaskKind.Inpaint).Where(m => m.Tier == AiTier.Generative))
            _modelCombo.Items.Add(new ComboBoxItem { Content = $"{m.Name}  ({m.SourceId ?? "native"})", Tag = m.Id });
        _modelCombo.SelectionChanged += (_, _) => { BuildLoras(); UpdateAssembled(); };
        if (_modelCombo.Items.Count > 0) _modelCombo.SelectedIndex = 0;
        Root.Children.Add(_modelCombo);

        // --- assembled model: text encoder(s) + VAE (shown only for diffusion_models/unet bases) ---
        _assembled = new StackPanel { Spacing = 2, IsVisible = false };
        _assembled.Children.Add(Label(Loc.T("generative.textEncoders")));
        _encoderRows = new StackPanel { Spacing = 1 };
        _assembled.Children.Add(_encoderRows);
        _assembled.Children.Add(Label(Loc.T("generative.vae")));
        _vaeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        _assembled.Children.Add(_vaeCombo);
        Root.Children.Add(_assembled);

        // --- LoRA stack ---
        Root.Children.Add(Label(Loc.T("generative.lorasLabel")));
        _loraRows = new StackPanel { Spacing = 2 };
        Root.Children.Add(_loraRows);

        // --- prompt / negative ---
        Root.Children.Add(Label(Loc.T("generative.prompt")));
        _prompt = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
        Root.Children.Add(_prompt);
        Root.Children.Add(Label(Loc.T("generative.negativePrompt")));
        _negative = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 40 };
        Root.Children.Add(_negative);

        // --- params ---
        _steps = NumRow(Loc.T("generative.steps"), "25", isSeed: false);
        _cfg = NumRow(Loc.T("generative.cfg"), "7.0", isSeed: false);
        _seed = NumRow(Loc.T("generative.seed"), "-1", out var randomBtn, isSeed: true);
        randomBtn!.Click += (_, _) => _seed.Text = new Random().Next(0, int.MaxValue).ToString(CultureInfo.InvariantCulture);

        _offload = new CheckBox { Content = Loc.T("generative.offload"), FontSize = 12, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        Root.Children.Add(_offload);

        // --- generate + status ---
        _generate = new Button { Content = Loc.T("generative.generate"), Classes = { "opt" }, Padding = new Avalonia.Thickness(18, 4), Margin = new Avalonia.Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        _generate.Click += OnGenerate;
        Root.Children.Add(_generate);

        _status = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        Fg(_status);
        Root.Children.Add(_status);

        if (_modelCombo.Items.Count == 0)
        {
            SetStatus(Loc.T("generative.noModel"));
            _generate.IsEnabled = false;
        }
        BuildLoras();
        UpdateAssembled();
    }

    private static bool IsEncoder(ModelManifest m)
        => m.Kind == ModelKind.Component && m.ComponentFamily is { } f
           && ((f.StartsWith("CLIP", StringComparison.Ordinal) && f != "CLIP-Vision") || f.StartsWith("T5", StringComparison.Ordinal));

    private static bool IsVae(ModelManifest m)
        => m.Kind == ModelKind.Component && (m.ComponentFamily?.StartsWith("VAE", StringComparison.Ordinal) ?? false);

    /// <summary>Show + populate the encoder/VAE pickers when the selected base is a standalone transformer.</summary>
    private void UpdateAssembled()
    {
        if (_assembled is null) return;
        var baseId = (_modelCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var m = baseId is null ? null : _reg.Catalog.ById(baseId);
        var file = m?.Files?.FirstOrDefault();
        bool unet = file is not null && Sable.Ai.Comfy.Workflow.WorkflowBuilder.KindForPath(file) == Sable.Ai.Comfy.Workflow.ComfyModelKind.Unet;
        _assembled.IsVisible = unet;
        _encoderRows.Children.Clear(); _encoders.Clear(); _vaeCombo.Items.Clear();
        if (!unet) return;

        foreach (var c in _reg.Catalog.All.Where(IsEncoder).OrderBy(c => c.Name))
        {
            var cb = new CheckBox { Content = $"{c.Name}  ({c.ComponentFamily})", FontSize = 11 };
            _encoderRows.Children.Add(cb);
            _encoders.Add((c.Id, cb));
        }
        if (_encoders.Count == 0)
        {
            var t = new TextBlock { Text = Loc.T("generative.noEncoders"), FontSize = 11 };
            t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ChromeTextFaint"));
            _encoderRows.Children.Add(t);
        }
        foreach (var v in _reg.Catalog.All.Where(IsVae).OrderBy(v => v.Name))
            _vaeCombo.Items.Add(new ComboBoxItem { Content = v.Name, Tag = v.Id });
        if (_vaeCombo.Items.Count > 0) _vaeCombo.SelectedIndex = 0;
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
        if (_loraRows is null) return;   // SelectedIndex set fires this during BuildUi before _loraRows exists
        _loraRows.Children.Clear();
        _loras.Clear();
        var baseId = (_modelCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var baseModel = baseId is null ? null : _reg.Catalog.ById(baseId);
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
        var baseId = (_modelCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrEmpty(baseId)) { SetStatus(Loc.T("generative.pickModelFirst")); return; }

        int steps = ParseInt(_steps.Text, 25);
        double cfg = ParseDouble(_cfg.Text, 7.0);
        long seed = ParseLong(_seed.Text, -1);

        var loras = new List<AdapterRef>();
        foreach (var (id, cb, weight) in _loras)
            if (cb.IsChecked == true) loras.Add(new AdapterRef(id, ParseDouble(weight.Text, 1.0)));

        var encoderIds = _encoders.Where(e => e.Cb.IsChecked == true).Select(e => e.Id).ToList();
        var vaeId = (_vaeCombo.SelectedItem as ComboBoxItem)?.Tag as string;

        GenerateRequested?.Invoke(new GenFillRequest(
            baseId, _prompt.Text ?? "", _negative.Text ?? "", steps, cfg, seed, _offload.IsChecked == true, loras, encoderIds, vaeId));
    }

    public void SetStatus(string text) => _status.Text = text;

    private static int ParseInt(string? s, int def) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    private static long ParseLong(string? s, long def) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    private static double ParseDouble(string? s, double def) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
}
