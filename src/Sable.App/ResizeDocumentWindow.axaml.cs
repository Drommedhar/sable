using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sable.App;

/// <summary>
/// Resize Document dialog: enter a new pixel size (optionally aspect-locked), DPI, and
/// resample method. Returns the chosen values; MainWindow runs the undoable ResizeCommand.
/// </summary>
public partial class ResizeDocumentWindow : Window
{
    private double _aspect = 1;
    private bool _syncing;

    public int NewW { get; private set; }
    public int NewH { get; private set; }
    public double Dpi { get; private set; }
    public bool Bilinear { get; private set; } = true;

    public ResizeDocumentWindow() : this(512, 512, 96) { }

    public ResizeDocumentWindow(int w, int h, double dpi)
    {
        InitializeComponent();
        _aspect = h > 0 ? (double)w / h : 1;
        WBox.Value = w;
        HBox.Value = h;
        DpiBox.Value = (decimal)dpi;
        UpdateDescription();
    }

    private void OnWidthChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_syncing) return;
        if (LinkBtn.IsChecked == true && WBox.Value is { } w)
        {
            _syncing = true;
            HBox.Value = (decimal)System.Math.Max(1, System.Math.Round((double)w / _aspect));
            _syncing = false;
        }
        UpdateDescription();
    }

    private void OnHeightChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_syncing) return;
        if (LinkBtn.IsChecked == true && HBox.Value is { } h)
        {
            _syncing = true;
            WBox.Value = (decimal)System.Math.Max(1, System.Math.Round((double)h * _aspect));
            _syncing = false;
        }
        UpdateDescription();
    }

    private void OnResampleToggled(object? sender, RoutedEventArgs e)
    {
        // Resample off → pixel size is locked (DPI-only change), like Affinity.
        bool on = ResampleCheck.IsChecked == true;
        WBox.IsEnabled = on;
        HBox.IsEnabled = on;
        LinkBtn.IsEnabled = on;
        ResampleCombo.IsEnabled = on;
    }

    private void UpdateDescription()
    {
        if (DescText is null) return;
        int w = (int)(WBox.Value ?? 0), h = (int)(HBox.Value ?? 0);
        double dpi = (double)(DpiBox.Value ?? 96);
        DescText.Text = $"{w} px × {h} px @ {dpi:0} DPI";
    }

    private void OnResize(object? sender, RoutedEventArgs e)
    {
        NewW = (int)(WBox.Value ?? 1);
        NewH = (int)(HBox.Value ?? 1);
        Dpi = (double)(DpiBox.Value ?? 96);
        Bilinear = ResampleCombo.SelectedIndex == 0;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
