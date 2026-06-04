using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Sable.App;

/// <summary>
/// Modal "AI is working" overlay: a full-window transparent top-level that covers the owner with a
/// dim scrim (airspace-safe — a separate top-level renders above the native GPU-canvas HWND, unlike
/// an in-window scrim) and a centred card. Shown via <see cref="Window.ShowDialog"/> so the owner is
/// disabled (can't interact) for the duration. Drive the bar via <see cref="Progress"/>; call
/// <see cref="Done"/> when finished. Cancel cancels the supplied token.
/// </summary>
public partial class BusyWindow : Window
{
    private readonly CancellationTokenSource? _cts;
    private bool _closed;

    public BusyWindow() : this(null) { }

    public BusyWindow(CancellationTokenSource? cts)
    {
        InitializeComponent();
        // window transparency so the scrim's semi-black shows the dimmed owner beneath it
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        _cts = cts;
        if (cts is null) CancelBtn.IsVisible = false;
    }

    /// <summary>Show modally over <paramref name="owner"/> (owner is disabled while open).</summary>
    public static BusyWindow Begin(Window owner, string message, CancellationTokenSource? cts = null)
    {
        var w = new BusyWindow(cts);
        w.MessageText.Text = message;
        w.CoverOwner(owner);
        _ = w.ShowDialog(owner);   // modal: disables the owner; non-blocking (returns a Task)
        return w;
    }

    private void CoverOwner(Window owner)
    {
        // PointToScreen maps the owner's CLIENT (0,0) to a PHYSICAL screen pixel — DPI- and chrome-correct,
        // unlike owner.Position (the outer window top-left incl. border/shadow). ClientSize is DIP; same
        // monitor → same scaling → matching physical extent. (owner.Position mixed with a DIP size was the
        // misalignment.)
        Position = owner.PointToScreen(new Point(0, 0));
        Width = owner.ClientSize.Width;
        Height = owner.ClientSize.Height;
    }

    public void SetMessage(string message) => Dispatcher.UIThread.Post(() => { if (!_closed) MessageText.Text = message; });

    private IProgress<double>? _progress;

    /// <summary>An <see cref="IProgress{T}"/> that drives the bar 0..1 (switches it to determinate).</summary>
    public IProgress<double> Progress => _progress ??= new Progress<double>(p => Dispatcher.UIThread.Post(() =>
    {
        if (_closed) return;
        Bar.IsIndeterminate = false;
        Bar.Value = Math.Clamp(p, 0, 1);
    }));

    public void Done()
    {
        if (_closed) return;
        _closed = true;
        Dispatcher.UIThread.Post(Close);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        CancelBtn.IsEnabled = false;
        MessageText.Text = Sable.App.Localization.Loc.T("busyWindow.cancelling");
        _cts?.Cancel();
    }
}
