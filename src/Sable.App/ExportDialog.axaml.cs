using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Sable.Imaging;

using Sable.App.Localization;

namespace Sable.App;

/// <summary>
/// Export dialog (PLAN §16.12): pick format (PNG/JPEG/WebP), quality, scale; shows a
/// preview + estimated file size. Returns the chosen options; MainWindow runs the save
/// picker + writes via <see cref="DocumentIO.Export"/>.
/// </summary>
public partial class ExportDialog : Window
{
    private readonly int _srcW, _srcH;
    private readonly byte[] _rgba;

    public ImageCodec.ImageFormat Format { get; private set; }
    public int Quality { get; private set; } = 90;
    public int OutW { get; private set; }
    public int OutH { get; private set; }

    public ExportDialog() : this(1, 1, new byte[4]) { }

    public ExportDialog(int srcW, int srcH, byte[] rgba)
    {
        InitializeComponent();
        WindowEscapeHelper.AddEscapeClose(this);
        _srcW = srcW; _srcH = srcH; _rgba = rgba;
        OutW = srcW; OutH = srcH;

        try
        {
            using var ms = new MemoryStream(ImageCodec.EncodePngBytes(srcW, srcH, rgba));
            Preview.Source = new Bitmap(ms);
        }
        catch { /* preview is best-effort */ }

        Recompute();
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
        var fmt = CurrentFormat;
        bool lossy = fmt != ImageCodec.ImageFormat.Png;
        QualityRow.IsVisible = lossy;

        int q = (int)QualitySlider.Value;
        int scale = (int)ScaleSlider.Value;
        int ow = System.Math.Max(1, _srcW * scale / 100);
        int oh = System.Math.Max(1, _srcH * scale / 100);

        QualityLabel.Text = Loc.T("exportDialog.qualityFormat", q);
        ScaleLabel.Text = Loc.T("exportDialog.scaleFormat", scale);
        DimsLabel.Text = Loc.T("exportDialog.dimsFormat", ow, oh);

        try
        {
            int bytes = ImageCodec.EncodeScaled(fmt, _srcW, _srcH, _rgba, ow, oh, q).Length;
            SizeLabel.Text = Loc.T("exportDialog.estimatedSize", Human(bytes));
        }
        catch { SizeLabel.Text = ""; }
    }

    private static string Human(long b)
        => b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:0.0} KB" : $"{b / (1024.0 * 1024):0.00} MB";

    private void OnExport(object? sender, RoutedEventArgs e)
    {
        Format = CurrentFormat;
        Quality = (int)QualitySlider.Value;
        int scale = (int)ScaleSlider.Value;
        OutW = System.Math.Max(1, _srcW * scale / 100);
        OutH = System.Math.Max(1, _srcH * scale / 100);
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
