using Avalonia.Controls;
using Avalonia.Input;

namespace Sable.App;

internal static class WindowEscapeHelper
{
    internal static void AddEscapeClose(Window w)
    {
        w.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                e.Handled = true;
                w.Close();
            }
        };
    }
}
