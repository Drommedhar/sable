using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sable.Ai.Download;
using Sable.Core.Ai;

namespace Sable.App;

/// <summary>
/// Sequential licence cycle + install (user spec): on AI enable, show each model's ORIGINAL licence
/// one at a time (fetched from its source), require the user to SCROLL TO THE BOTTOM before Accept
/// enables — no accept, no install. Decline skips that model. After the last licence, the accepted
/// models download (progress in-dialog). Returns the accepted+installed models.
/// </summary>
public partial class LicenseCycleWindow : Window
{
    private readonly IReadOnlyList<RecommendedModel> _models;
    private readonly ModelDownloader _downloader;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly List<RecommendedModel> _accepted = new();
    private int _i;
    private bool _installing;

    public LicenseCycleWindow() : this(System.Array.Empty<RecommendedModel>(), null!) { }

    public LicenseCycleWindow(IReadOnlyList<RecommendedModel> models, ModelDownloader downloader)
    {
        InitializeComponent();
        _models = models;
        _downloader = downloader;
        Opened += async (_, _) => await ShowCurrent();
    }

    private async Task ShowCurrent()
    {
        if (_i >= _models.Count) { await StartInstall(); return; }
        var m = _models[_i];
        HeaderText.Text = $"Licence {_i + 1} of {_models.Count}  —  {m.Name}";
        AcceptBtn.IsEnabled = false;
        LicText.Text = "Loading licence…";
        LicScroll.Offset = default;

        LicText.Text = await FetchLicence(m);
        // re-check after layout: a short licence (no scrollbar) is already "at the bottom"
        Dispatcher.UIThread.Post(CheckScroll, DispatcherPriority.Loaded);
    }

    private void OnScroll(object? sender, ScrollChangedEventArgs e) => CheckScroll();

    private void CheckScroll()
    {
        if (_installing) return;
        double max = LicScroll.Extent.Height - LicScroll.Viewport.Height;
        bool atBottom = max <= 1 || LicScroll.Offset.Y >= max - 2;
        if (atBottom) AcceptBtn.IsEnabled = true;
    }

    private async void OnAccept(object? sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _accepted.Add(_models[_i]);
        _i++;
        await ShowCurrent();
    }

    private async void OnDecline(object? sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _i++;
        await ShowCurrent();
    }

    private async Task StartInstall()
    {
        _installing = true;
        DeclineBtn.IsVisible = false;
        AcceptBtn.IsVisible = false;
        if (_accepted.Count == 0) { Close(_accepted); return; }

        HintText.Text = "";
        LicText.Text = "";
        HeaderText.Text = "Installing models…";
        InstallBar.IsVisible = true;

        for (int k = 0; k < _accepted.Count; k++)
        {
            var m = _accepted[k];
            int idx = k;
            StatusText.Text = $"Downloading {m.Name}  ({k + 1}/{_accepted.Count})…";
            var prog = new Progress<double>(p => Dispatcher.UIThread.Post(() => InstallBar.Value = (idx + p) / _accepted.Count));
            try { await _downloader.DownloadAsync(m, prog, CancellationToken.None); }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed: {m.Name} — {ex.Message}";
                await Task.Delay(1500);
            }
        }
        StatusText.Text = "Done.";
        await Task.Delay(400);
        Close(_accepted);
    }

    private async Task<string> FetchLicence(RecommendedModel m)
    {
        if (string.IsNullOrEmpty(m.LicenseUrl)) return m.License;
        try { return await _http.GetStringAsync(m.LicenseUrl); }
        catch (Exception ex) { return $"Could not fetch the licence ({ex.Message}).\n\nLicence: {m.License}\nSource: {m.LicenseUrl}\n\n(You must still accept to install.)"; }
    }
}
