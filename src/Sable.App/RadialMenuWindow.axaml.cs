using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Sable.App;

/// <summary>
/// Quick-access radial menu (Krita-style, pen-friendly): a ring of action pills around the
/// cursor. Opened with ` (backquote); click an item to run it, the centre/Esc/focus-loss to
/// dismiss. A top-level window so it floats over the native canvas HWND (airspace-safe).
/// </summary>
public partial class RadialMenuWindow : Window
{
    public RadialMenuWindow() => InitializeComponent();

    private Window? _owner;

    public RadialMenuWindow(IReadOnlyList<(string Label, Action Run)> items) : this()
    {
        const double radius = 100;
        double cx = Width / 2, cy = Height / 2;
        for (int i = 0; i < items.Count; i++)
        {
            var (label, run) = items[i];
            double a = -Math.PI / 2 + i * 2 * Math.PI / items.Count;   // start at 12 o'clock
            var btn = new Button
            {
                Content = label,
                Classes = { "opt" },
                Padding = new Thickness(12, 3),
                FontSize = 12,
                CornerRadius = new CornerRadius(12),
            };
            // The window is shown WITHOUT activation (ShowActivated=False) so the editor keeps focus
            // the whole time — picking an item just runs + closes, focus never leaves the main window,
            // so there's no window-switch flash. Pointer events work without activation.
            btn.Click += (_, _) => { run(); Close(); };
            Root.Children.Add(btn);
            // centre each pill on its ring position once measured
            btn.Measure(Size.Infinity);
            double bw = btn.DesiredSize.Width, bh = btn.DesiredSize.Height;
            btn.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            btn.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            btn.Margin = new Thickness(
                cx + radius * Math.Cos(a) - bw / 2,
                cy + radius * Math.Sin(a) - bh / 2, 0, 0);
        }

        // not activated → no Deactivated/KeyDown here. Esc + re-press close it via MainWindow (it holds
        // the instance); the centre button + any item also close it.
    }

    private void OnCentre(object? sender, PointerPressedEventArgs e) => Close();

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint p);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }

    /// <summary>Show centred on the OS cursor (Windows); centred on the owner elsewhere.</summary>
    public void ShowAtCursor(Window owner)
    {
        _owner = owner;
        if (OperatingSystem.IsWindows() && GetCursorPos(out var p))
        {
            double s = (owner.RenderScaling > 0 ? owner.RenderScaling : 1.0);
            Position = new PixelPoint(p.X - (int)(Width * s / 2), p.Y - (int)(Height * s / 2));
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        Show(owner);   // ShowActivated=False → does not steal focus from the editor
    }
}
