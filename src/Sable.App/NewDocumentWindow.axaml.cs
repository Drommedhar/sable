using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sable.App;

/// <summary>New Document dialog (Phase 2): size + DPI + presets → a blank Document in a new tab.</summary>
public partial class NewDocumentWindow : Window
{
    public int DocWidth { get; private set; } = 1920;
    public int DocHeight { get; private set; } = 1080;
    public double Dpi { get; private set; } = 96;

    public NewDocumentWindow() => InitializeComponent();

    public NewDocumentWindow(double defaultDpi) : this() { DpiBox.Value = (decimal)defaultDpi; }

    private void OnPreset(object? sender, SelectionChangedEventArgs e)
    {
        switch (PresetCombo.SelectedIndex)
        {
            case 1: Set(1024, 1024, 96); break;
            case 2: Set(1920, 1080, 96); break;
            case 3: Set(3840, 2160, 96); break;
            case 4: Set(2480, 3508, 300); break;   // A4 @ 300 DPI
            case 5: Set(1080, 1080, 72); break;
        }
    }

    private void Set(int w, int h, int dpi) { WBox.Value = w; HBox.Value = h; DpiBox.Value = dpi; }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        DocWidth = (int)(WBox.Value ?? 1920);
        DocHeight = (int)(HBox.Value ?? 1080);
        Dpi = (double)(DpiBox.Value ?? 96);
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
