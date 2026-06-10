using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sable.App;

/// <summary>
/// Contextual task bar: a small floating pill that appears under the active selection with
/// the most likely next actions (generative fill / mask / feather / invert / deselect).
/// A separate top-level window so it floats above the native canvas HWND (airspace-safe,
/// same trick as <see cref="BusyWindow"/>). Never takes focus; MainWindow positions it.
/// </summary>
public partial class TaskBarWindow : Window
{
    public event Action? GenFillClicked;
    public event Action? MaskClicked;
    public event Action? InvertClicked;
    public event Action? DeselectClicked;

    public TaskBarWindow() => InitializeComponent();

    /// <summary>Show/hide the Generative Fill action (gated on the generative tier being enabled).</summary>
    public void SetGenFillVisible(bool on) => GenFillBtn.IsVisible = on;

    private void OnGenFill(object? sender, RoutedEventArgs e) => GenFillClicked?.Invoke();
    private void OnMask(object? sender, RoutedEventArgs e) => MaskClicked?.Invoke();
    private void OnInvert(object? sender, RoutedEventArgs e) => InvertClicked?.Invoke();
    private void OnDeselect(object? sender, RoutedEventArgs e) => DeselectClicked?.Invoke();
}
