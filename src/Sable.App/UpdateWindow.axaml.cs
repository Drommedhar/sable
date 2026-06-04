using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sable.Core;
using Sable.Core.Services;

namespace Sable.App;

/// <summary>
/// Update flow (PLAN §2.4, Novalist-style): shows the available version + release notes, then
/// downloads the per-OS asset with a progress bar, launches the installer, and shuts the app down
/// so the installer can replace files. "Release Page" opens the browser as a manual fallback.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _update;
    private readonly IUpdateService _service;
    private CancellationTokenSource? _cts;
    private bool _downloading;

    public UpdateWindow() : this(new UpdateInfo(), new UpdateService()) { }

    public UpdateWindow(UpdateInfo update, IUpdateService service)
    {
        InitializeComponent();
        _update = update;
        _service = service;
        VersionText.Text = $"Sable {update.TagName} is available — you have {VersionInfo.Version}.";
        if (!string.IsNullOrWhiteSpace(update.Body))
        {
            NotesScroll.Markdown = update.Body;
            NotesBox.IsVisible = true;
        }
        // no downloadable asset for this platform → only offer the release page
        if (string.IsNullOrEmpty(update.DownloadUrl))
            DownloadButton.IsEnabled = false;
    }

    private async void OnDownload(object? sender, RoutedEventArgs e)
    {
        if (_downloading) return;
        _downloading = true;
        DownloadButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        ProgressPanel.IsVisible = true;
        ErrorText.IsVisible = false;

        _cts = new CancellationTokenSource();
        var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() =>
        {
            DownloadProgress.Value = p * 100;
            ProgressText.Text = $"Downloading… {(int)(p * 100)}%";
        }));

        try
        {
            var installer = await _service.DownloadUpdateAsync(_update, progress, _cts.Token);
            ProgressText.Text = "Launching installer…";
            _service.LaunchInstaller(installer);

            // close + shut the app down so the installer can replace files, then it relaunches Sable
            Close();
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch (OperationCanceledException)
        {
            ResetAfterFailure();
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Update failed: {ex.Message}";
            ErrorText.IsVisible = true;
            ResetAfterFailure();
        }
    }

    private void ResetAfterFailure()
    {
        ProgressPanel.IsVisible = false;
        _downloading = false;
        DownloadButton.IsEnabled = !string.IsNullOrEmpty(_update.DownloadUrl);
        LaterButton.IsEnabled = true;
    }

    private void OnLater(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }
}
