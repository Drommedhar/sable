using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Sable.App;

/// <summary>
/// Reusable numeric-amount prompt (e.g. Select ▸ Grow/Shrink/Feather radius). Slider + value box
/// kept in sync; returns the chosen integer or null on cancel.
/// </summary>
public partial class AmountDialog : Window
{
    private bool _sync;

    public AmountDialog() { InitializeComponent(); WindowEscapeHelper.AddEscapeClose(this); }

    /// <summary>Show the prompt. Returns the value (clamped to min..max) or null if cancelled.</summary>
    public static async System.Threading.Tasks.Task<int?> Ask(Window owner, string title, int initial,
        int min = 1, int max = 100, string? label = null, string? unit = null)
    {
        var w = new AmountDialog();
        w.TitleText.Text = title;
        if (label is not null) w.LabelText.Text = label;
        if (unit is not null) w.UnitText.Text = unit;
        w.ValueSlider.Minimum = min;
        w.ValueSlider.Maximum = max;
        w.ValueSlider.Value = System.Math.Clamp(initial, min, max);
        w.ValueBox.Text = ((int)w.ValueSlider.Value).ToString();
        w.ValueBox.TextChanged += w.OnBoxChanged;
        var ok = await w.ShowDialog<bool>(owner);
        if (!ok) return null;
        return Sable.Core.NumericExpression.TryEval(w.ValueBox.Text, w.ValueSlider.Value, out var v)
            ? System.Math.Clamp((int)System.Math.Round(v), min, max)
            : (int)w.ValueSlider.Value;
    }

    private void OnSlider(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_sync || ValueBox is null) return;
        _sync = true;
        ValueBox.Text = ((int)e.NewValue).ToString();
        _sync = false;
    }

    private void OnBoxChanged(object? sender, TextChangedEventArgs e)
    {
        if (_sync) return;
        if (int.TryParse(ValueBox.Text, out var v))
        {
            _sync = true;
            ValueSlider.Value = System.Math.Clamp(v, ValueSlider.Minimum, ValueSlider.Maximum);
            _sync = false;
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
