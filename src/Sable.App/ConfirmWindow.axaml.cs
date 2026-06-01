using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sable.App;

/// <summary>Minimal yes/no confirm dialog (e.g. discard unsaved changes). Returns true on confirm.</summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow() => InitializeComponent();

    public static System.Threading.Tasks.Task<bool> Ask(Window owner, string title, string body)
    {
        var w = new ConfirmWindow();
        w.TitleText.Text = title;
        w.BodyText.Text = body;
        return w.ShowDialog<bool>(owner);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
