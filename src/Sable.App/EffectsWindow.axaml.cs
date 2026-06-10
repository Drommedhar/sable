using Avalonia.Controls;
using Avalonia.Interactivity;
using Sable.Engine.Layers;
using Sable.UI.ViewModels;

namespace Sable.App;

/// <summary>
/// Affinity-style Layer Effects dialog (PLAN §16.6): master list of effects (left,
/// enable checkboxes) + params of the selected effect (right). Modeless, bound to
/// the same DocumentViewModel; edits the selected layer's effect stack.
/// </summary>
public partial class EffectsWindow : Window
{
    public EffectsWindow()
    {
        InitializeComponent();
        WindowEscapeHelper.AddEscapeClose(this);
        FxList.SelectedIndex = 0;
    }

    private void OnFxSelected(object? sender, SelectionChangedEventArgs e)
    {
        int i = FxList.SelectedIndex;
        PanelShadow.IsVisible = i == 0;
        PanelGlow.IsVisible = i == 1;
        PanelStroke.IsVisible = i == 2;
        PanelOverlay.IsVisible = i == 3;
        PanelInnerShadow.IsVisible = i == 4;
        PanelInnerGlow.IsVisible = i == 5;
        PanelGradient.IsVisible = i == 6;
        PanelBevel.IsVisible = i == 7;
    }

    // FxList row index → effect kind (must match the ListBoxItem Tags / panel order)
    private static readonly LayerEffectKind[] _kinds =
    {
        LayerEffectKind.DropShadow, LayerEffectKind.OuterGlow, LayerEffectKind.Stroke,
        LayerEffectKind.ColorOverlay, LayerEffectKind.InnerShadow, LayerEffectKind.InnerGlow,
        LayerEffectKind.GradientOverlay, LayerEffectKind.Bevel,
    };

    private void Move(int dir)
    {
        int i = FxList.SelectedIndex;
        if (i < 0 || i >= _kinds.Length) return;
        (DataContext as DocumentViewModel)?.SelectedLayer?.MoveEffect(_kinds[i], dir);
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object? sender, RoutedEventArgs e) => Move(+1);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
