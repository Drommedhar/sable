using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;

using Sable.App.Localization;

namespace Sable.App;

/// <summary>
/// Resize Canvas dialog: change the canvas bounds without resampling. Layers keep their
/// pixel size; growing pads transparent, shrinking crops. The 9-point anchor decides
/// where the existing content sits. MainWindow turns the result into a CropCommand.
/// </summary>
public partial class ResizeCanvasWindow : Window
{
    private readonly ToggleButton[] _anchors = new ToggleButton[9];
    private int _anchor = 4;   // 0..8, default centre
    private int _oldW, _oldH;

    public int NewW { get; private set; }
    public int NewH { get; private set; }
    /// <summary>Anchor column 0=left,1=centre,2=right.</summary>
    public int AnchorX => _anchor % 3;
    /// <summary>Anchor row 0=top,1=middle,2=bottom.</summary>
    public int AnchorY => _anchor / 3;

    public ResizeCanvasWindow() : this(512, 512) { }

    public ResizeCanvasWindow(int w, int h)
    {
        InitializeComponent();
        WindowEscapeHelper.AddEscapeClose(this);
        _oldW = w; _oldH = h;
        WBox.Value = w;
        HBox.Value = h;
        for (int i = 0; i < 9; i++)
        {
            int idx = i;
            var tb = new ToggleButton
            {
                Margin = new Avalonia.Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsChecked = idx == _anchor
            };
            tb.Click += (_, _) => SelectAnchor(idx);
            _anchors[i] = tb;
            AnchorGrid.Children.Add(tb);
        }
        UpdateDescription();
    }

    private void SelectAnchor(int idx)
    {
        _anchor = idx;
        for (int i = 0; i < 9; i++) _anchors[i].IsChecked = i == idx;
        UpdateDescription();
    }

    private void OnSizeChanged(object? sender, NumericUpDownValueChangedEventArgs e) => UpdateDescription();

    private void UpdateDescription()
    {
        if (DescText is null) return;
        int w = (int)(WBox.Value ?? 0), h = (int)(HBox.Value ?? 0);
        DescText.Text = Loc.T("resizeCanvasWindow.descFormat", _oldW, _oldH, w, h);
    }

    private void OnResize(object? sender, RoutedEventArgs e)
    {
        NewW = (int)(WBox.Value ?? 1);
        NewH = (int)(HBox.Value ?? 1);
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
