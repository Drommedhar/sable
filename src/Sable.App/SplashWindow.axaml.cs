using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Sable.App;

/// <summary>
/// Borderless transparent splash: its own top-level window (airspace-safe over the native canvas
/// HWND, same reason as BusyWindow). Hosts the SplashLogo paint-in animation, then cross-fades
/// into MainWindow.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    /// <summary>Completes when the logo intro has fully played (frame-time — pauses if the UI thread stalls).</summary>
    public Task IntroDone => Logo.IntroDone;

    public async Task FadeOutAsync()
    {
        await FadeAsync(this, 1, 0, 350);
        Close();
    }

    /// <summary>Smooth-step a window's opacity on the UI thread (cross-fade helper, also used for MainWindow fade-in).</summary>
    public static async Task FadeAsync(Window w, double from, double to, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var p = sw.ElapsedMilliseconds / (double)ms;
            w.Opacity = from + (to - from) * (p * p * (3 - 2 * p));
            await Task.Delay(16);
        }
        w.Opacity = to;
    }
}
