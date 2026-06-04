using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sable.App;

/// <summary>Minimal yes/no confirm dialog (e.g. discard unsaved changes). Returns true on confirm.</summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow() => InitializeComponent();

    /// <summary>Ask a confirm question. <paramref name="okText"/>/<paramref name="cancelText"/> override the
    /// default Discard/Cancel labels for true yes/no prompts (default = the discard-changes wording).</summary>
    public static System.Threading.Tasks.Task<bool> Ask(Window owner, string title, string body,
        string? okText = null, string? cancelText = null)
    {
        var w = new ConfirmWindow();
        w.TitleText.Text = title;
        w.BodyText.Text = body;
        if (okText is not null) w.OkBtn.Content = okText;
        if (cancelText is not null) w.CancelBtn.Content = cancelText;
        return w.ShowDialog<bool>(owner);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
