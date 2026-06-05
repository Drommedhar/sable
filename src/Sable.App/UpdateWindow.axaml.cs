using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Sable.Core;
using Sable.Core.Services;

using Sable.App.Localization;

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
        VersionText.Text = Loc.T("updateWindow.availableFormat", update.TagName, VersionInfo.Version);
        if (!string.IsNullOrWhiteSpace(update.Body))
        {
            BuildNotes(update.Body);
            NotesBox.IsVisible = true;
        }
        // no downloadable asset for this platform → only offer the release page
        if (string.IsNullOrEmpty(update.DownloadUrl))
            DownloadButton.IsEnabled = false;
    }

    // chrome-density typography for the changelog (matches the rest of the app, not document markdown)
    private const double NoteFontSize = 12;
    private const double NoteLineHeight = 17;
    private const double IndentStep = 16;

    /// <summary>
    /// Renders the changelog as one tab per section (Added/Changed/Fixed…) with a collapsible expander
    /// per version inside each tab, so a user who skipped several versions can scan one category at a
    /// time and expand the versions they care about. Bullets are rendered as native controls at chrome
    /// density (not document-sized markdown) so the sizing is consistent. Falls back to plain text if
    /// the notes don't parse into versioned sections.
    /// </summary>
    private void BuildNotes(string markdown)
    {
        var versions = ChangelogParser.Parse(markdown);
        if (versions.Count == 0) { NotesHost.Children.Add(FallbackText(markdown)); return; }

        var tabs = new TabControl { Padding = new Thickness(0), Margin = new Thickness(0) };
        foreach (var section in ChangelogParser.SectionOrder(versions))
        {
            var stack = new StackPanel { Spacing = 6, Margin = new Thickness(2, 8, 2, 4) };
            var first = true;
            foreach (var v in versions)
            {
                var sec = v.Sections.FirstOrDefault(s => s.Name == section);
                if (sec is null) continue;
                stack.Children.Add(VersionExpander(v.Heading, sec.Markdown, first));
                first = false;   // newest version open, older ones collapsed
            }
            tabs.Items.Add(new TabItem
            {
                Header = section,
                FontSize = 13,
                Padding = new Thickness(10, 4, 10, 4),
                Content = new ScrollViewer
                {
                    MaxHeight = 340,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = stack,
                },
            });
        }
        NotesHost.Children.Add(tabs);
    }

    private Expander VersionExpander(string heading, string sectionMarkdown, bool open)
    {
        var header = new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold, FontSize = 12 };
        header.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ChromeText"));
        return new Expander
        {
            Header = header,
            IsExpanded = open,
            Padding = new Thickness(12, 4, 12, 8),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Content = Bullets(sectionMarkdown),
        };
    }

    private Control Bullets(string sectionMarkdown)
    {
        var panel = new StackPanel { Spacing = 5 };
        foreach (var b in ChangelogParser.Bullets(sectionMarkdown))
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = NoteFontSize, LineHeight = NoteLineHeight };
            text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ChromeText"));
            foreach (var s in ChangelogParser.Spans(b.Text))
                text.Inlines!.Add(new Run(s.Text) { FontWeight = s.Bold ? FontWeight.SemiBold : FontWeight.Normal });

            if (!b.IsBullet) { panel.Children.Add(text); continue; }

            var dot = new TextBlock { Text = "•", FontSize = NoteFontSize, Margin = new Thickness(0, 0, 8, 0) };
            dot.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ChromeTextDim"));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(b.Indent * IndentStep, 0, 0, 0) };
            Grid.SetColumn(text, 1);
            row.Children.Add(dot);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return panel;
    }

    private TextBlock FallbackText(string raw)
    {
        var tb = new TextBlock { Text = raw, TextWrapping = TextWrapping.Wrap, FontSize = NoteFontSize, Margin = new Thickness(8) };
        tb.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ChromeText"));
        return tb;
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
            ProgressText.Text = Loc.T("updateWindow.downloadingFormat", (int)(p * 100));
        }));

        try
        {
            var installer = await _service.DownloadUpdateAsync(_update, progress, _cts.Token);
            ProgressText.Text = Loc.T("updateWindow.launchingInstaller");
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
            ErrorText.Text = Loc.T("updateWindow.updateFailed", ex.Message);
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
