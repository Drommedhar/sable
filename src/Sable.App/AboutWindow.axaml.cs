using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sable.Core;
using Sable.Core.Services;

using Sable.App.Localization;

namespace Sable.App;

/// <summary>Help ▸ About: version / licence / runtime info + a manual update check (PLAN §2.4).</summary>
public partial class AboutWindow : Window
{
    private readonly IUpdateService _updates = new UpdateService();

    public AboutWindow() : this("—") { }

    public AboutWindow(string gpuName)
    {
        InitializeComponent();
        VersionLabel.Text = Loc.T("aboutWindow.versionFormat", VersionInfo.Version);
        RuntimeLabel.Text = Loc.T("aboutWindow.runtimeFormat", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        GpuLabel.Text = Loc.T("aboutWindow.rendererFormat", gpuName);
    }

    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
    {
        UpdateBtn.IsEnabled = false;
        UpdateStatus.Text = Loc.T("aboutWindow.checking");
        try
        {
            var info = await _updates.CheckForUpdateAsync();
            if (info is null)
            {
                UpdateStatus.Text = Loc.T("aboutWindow.latest");
            }
            else
            {
                UpdateStatus.Text = Loc.T("aboutWindow.updateAvailableFormat", info.TagName);
                await new UpdateWindow(info, _updates).ShowDialog(this);
            }
        }
        catch
        {
            UpdateStatus.Text = Loc.T("aboutWindow.checkFailed");
        }
        finally
        {
            UpdateBtn.IsEnabled = true;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
