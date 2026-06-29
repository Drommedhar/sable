using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sable.App.Localization;
using Sable.Engine.IO;
using Sable.Engine.Layers;
using Sable.Imaging;

namespace Sable.App;

/// <summary>
/// Batch asset export (ROADMAP P3): pick an output folder, format/quality, one or more scale
/// variants, and which top-level layers to export. MainWindow renders + writes each selected
/// layer × each scale via <see cref="AssetExport"/>. Returns the chosen options on OK.
/// </summary>
public partial class BatchExportDialog : Window
{
    public string Folder { get; private set; } = "";
    public ImageCodec.ImageFormat Format { get; private set; }
    public int Quality { get; private set; } = 90;
    public bool Trim { get; private set; } = true;
    public List<ScaleVariant> Scales { get; private set; } = new();
    public List<Layer> SelectedLayers { get; private set; } = new();

    private readonly List<(Layer Layer, CheckBox Box)> _rows = new();

    public BatchExportDialog() : this(new List<Layer>()) { }

    public BatchExportDialog(IReadOnlyList<Layer> topLevelLayers)
    {
        InitializeComponent();
        WindowEscapeHelper.AddEscapeClose(this);

        foreach (var layer in topLevelLayers)
        {
            var box = new CheckBox
            {
                Content = string.IsNullOrWhiteSpace(layer.Name) ? Loc.T("batchExport.unnamed") : layer.Name,
                IsChecked = layer.Visible,
            };
            _rows.Add((layer, box));
            LayerList.Children.Add(box);
        }
        UpdateExportEnabled();
    }

    private ImageCodec.ImageFormat CurrentFormat => FormatCombo.SelectedIndex switch
    {
        1 => ImageCodec.ImageFormat.Jpeg,
        2 => ImageCodec.ImageFormat.Webp,
        3 => ImageCodec.ImageFormat.Tiff,
        _ => ImageCodec.ImageFormat.Png,
    };

    private void OnFormatChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (QualityRow is not null) QualityRow.IsVisible = CurrentFormat != ImageCodec.ImageFormat.Png;
    }

    private void OnQuality(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (QualityLabel is not null) QualityLabel.Text = Loc.T("batchExport.qualityFormat", (int)QualitySlider.Value);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = Loc.T("batchExport.pickFolder"), AllowMultiple = false });
        var raw = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            Folder = raw;
            FolderBox.Text = raw;
            UpdateExportEnabled();
        }
    }

    private void OnSelectAll(object? sender, RoutedEventArgs e) { foreach (var (_, b) in _rows) b.IsChecked = true; }
    private void OnSelectNone(object? sender, RoutedEventArgs e) { foreach (var (_, b) in _rows) b.IsChecked = false; }

    private void UpdateExportEnabled()
    {
        if (ExportButton is not null) ExportButton.IsEnabled = !string.IsNullOrWhiteSpace(Folder);
    }

    private List<ScaleVariant> CollectScales()
    {
        var s = new List<ScaleVariant>();
        if (Scale05.IsChecked == true) s.Add(new ScaleVariant("@0.5x", 50));
        if (Scale1.IsChecked == true) s.Add(new ScaleVariant("", 100));
        if (Scale2.IsChecked == true) s.Add(new ScaleVariant("@2x", 200));
        if (Scale3.IsChecked == true) s.Add(new ScaleVariant("@3x", 300));
        return s;
    }

    private void OnExport(object? sender, RoutedEventArgs e)
    {
        Format = CurrentFormat;
        Quality = (int)QualitySlider.Value;
        Trim = TrimCheck.IsChecked == true;
        Scales = CollectScales();
        SelectedLayers = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Layer).ToList();

        if (string.IsNullOrWhiteSpace(Folder) || Scales.Count == 0 || SelectedLayers.Count == 0)
            return;   // nothing to do — keep dialog open
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
