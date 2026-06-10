using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sable.App;

/// <summary>Reusable single-line text prompt (e.g. "name this brush preset"). Null on cancel.</summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow() => InitializeComponent();

    public static async System.Threading.Tasks.Task<string?> Ask(Window owner, string title, string initial = "")
    {
        var w = new TextPromptWindow();
        w.TitleText.Text = title;
        w.ValueBox.Text = initial;
        w.Opened += (_, _) => { w.ValueBox.Focus(); w.ValueBox.SelectAll(); };
        var ok = await w.ShowDialog<bool>(owner);
        var text = w.ValueBox.Text?.Trim();
        return ok && !string.IsNullOrEmpty(text) ? text : null;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
