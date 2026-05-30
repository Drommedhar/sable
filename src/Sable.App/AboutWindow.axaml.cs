using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sable.Core;
using Sable.Core.Services;

namespace Sable.App;

/// <summary>Help ▸ About: version / licence / runtime info + a manual update check (PLAN §2.4).</summary>
public partial class AboutWindow : Window
{
    private readonly IUpdateService _updates = new UpdateService();

    public AboutWindow() : this("—") { }

    public AboutWindow(string gpuName)
    {
        InitializeComponent();
        VersionLabel.Text = $"Version {VersionInfo.Version}";
        RuntimeLabel.Text = $"net10.0  ·  Avalonia + wgpu  ·  {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";
        GpuLabel.Text = $"Renderer: {gpuName}";
    }

    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
    {
        UpdateBtn.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        try
        {
            var info = await _updates.CheckForUpdateAsync();
            if (info is null)
            {
                UpdateStatus.Text = "You're on the latest version.";
            }
            else
            {
                UpdateStatus.Text = $"Update available: {info.TagName}.";
                await new UpdateWindow(info, _updates).ShowDialog(this);
            }
        }
        catch
        {
            UpdateStatus.Text = "Couldn't check for updates (offline or repo unavailable).";
        }
        finally
        {
            UpdateBtn.IsEnabled = true;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
