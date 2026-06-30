using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Sable.Imaging;
using Sable.Plugin.Sdk.Export;

using Sable.App.Localization;

namespace Sable.App;

/// <summary>
/// Export dialog (PLAN §16.12): pick format (PNG/JPEG/WebP), quality, scale; shows a
/// preview + estimated file size. Returns the chosen options; MainWindow runs the save
/// picker + writes via <see cref="DocumentIO.Export"/>.
/// </summary>
public partial class ExportDialog : Window
{
    private const int BuiltInCount = 4;   // PNG / JPEG / WebP / TIFF in the XAML combo
    private readonly int _srcW, _srcH;
    private readonly byte[] _rgba;
    private readonly List<IExportProvider> _plugins = new();

    public ImageCodec.ImageFormat Format { get; private set; }
    public int Quality { get; private set; } = 90;
    public int OutW { get; private set; }
    public int OutH { get; private set; }

    /// <summary>Non-null when a plugin export format was chosen; encode via this instead of the built-in path.</summary>
    public IExportProvider? PluginProvider { get; private set; }

    public ExportDialog() : this(1, 1, new byte[4]) { }

    public ExportDialog(int srcW, int srcH, byte[] rgba, IEnumerable<IExportProvider>? pluginProviders = null)
    {
        InitializeComponent();
        WindowEscapeHelper.AddEscapeClose(this);
        _srcW = srcW; _srcH = srcH; _rgba = rgba;
        OutW = srcW; OutH = srcH;

        if (pluginProviders is not null)
            foreach (var p in pluginProviders)
            {
                _plugins.Add(p);
                FormatCombo.Items.Add(new ComboBoxItem { Content = p.Label });
            }

        try
        {
            using var ms = new MemoryStream(ImageCodec.EncodePngBytes(srcW, srcH, rgba));
            Preview.Source = new Bitmap(ms);
        }
        catch { /* preview is best-effort */ }

        Recompute();
    }

    /// <summary>The plugin provider for the current combo selection, or null for a built-in format.</summary>
    private IExportProvider? CurrentPlugin
    {
        get
        {
            int i = FormatCombo.SelectedIndex - BuiltInCount;
            return i >= 0 && i < _plugins.Count ? _plugins[i] : null;
        }
    }

    private ImageCodec.ImageFormat CurrentFormat => FormatCombo.SelectedIndex switch
    {
        1 => ImageCodec.ImageFormat.Jpeg,
        2 => ImageCodec.ImageFormat.Webp,
        3 => ImageCodec.ImageFormat.Tiff,
        _ => ImageCodec.ImageFormat.Png,
    };

    private void OnChanged(object? sender, RoutedEventArgs e) => Recompute();

    private void Recompute()
    {
        if (FormatCombo is null) return;
        var plugin = CurrentPlugin;
        QualityRow.IsVisible = plugin is not null || CurrentFormat != ImageCodec.ImageFormat.Png;

        int q = (int)QualitySlider.Value;
        int scale = (int)ScaleSlider.Value;
        int ow = System.Math.Max(1, _srcW * scale / 100);
        int oh = System.Math.Max(1, _srcH * scale / 100);

        QualityLabel.Text = Loc.T("exportDialog.qualityFormat", q);
        ScaleLabel.Text = Loc.T("exportDialog.scaleFormat", scale);
        DimsLabel.Text = Loc.T("exportDialog.dimsFormat", ow, oh);

        try
        {
            int bytes;
            if (plugin is not null)
            {
                var scaled = ImageCodec.ResizeRgba(_rgba, _srcW, _srcH, ow, oh);
                bytes = plugin.Encode(new ExportImage { Width = ow, Height = oh, Rgba = scaled },
                    new ExportOptions { Quality = q }).Length;
            }
            else bytes = ImageCodec.EncodeScaled(CurrentFormat, _srcW, _srcH, _rgba, ow, oh, q).Length;
            SizeLabel.Text = Loc.T("exportDialog.estimatedSize", Human(bytes));
        }
        catch { SizeLabel.Text = ""; }
    }

    private static string Human(long b)
        => b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:0.0} KB" : $"{b / (1024.0 * 1024):0.00} MB";

    private void OnExport(object? sender, RoutedEventArgs e)
    {
        PluginProvider = CurrentPlugin;
        Format = CurrentFormat;
        Quality = (int)QualitySlider.Value;
        int scale = (int)ScaleSlider.Value;
        OutW = System.Math.Max(1, _srcW * scale / 100);
        OutH = System.Math.Max(1, _srcH * scale / 100);
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
