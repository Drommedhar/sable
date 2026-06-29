using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sable.App.Localization;
using Sable.Format;

namespace Sable.App;

/// <summary>
/// Import compatibility report (roadmap §15): a persistent, modeless view of everything
/// <see cref="PsdReader"/> could not preserve 1:1 — rasterised features, partial mappings,
/// skipped layers, structural issues, and missing fonts. Replaces the transient toast as the
/// authoritative record; the toast keeps a "View report" action that opens this window.
/// </summary>
public partial class CompatibilityReportWindow : Window
{
    private CompatibilityReport? _report;

    public CompatibilityReportWindow()
    {
        InitializeComponent();
        WindowEscapeHelper.AddEscapeClose(this);
    }

    /// <summary>Populate the window from a built report and show it modelessly.</summary>
    public void Show(CompatibilityReport report, Window owner)
    {
        _report = report;
        Populate(report);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        base.Show(owner);
    }

    /// <summary>Re-populate from a new report (e.g. after re-opening the same tab's PSD).</summary>
    public void Update(CompatibilityReport report)
    {
        _report = report;
        Populate(report);
    }

    private void Populate(CompatibilityReport r)
    {
        DocNameLabel.Text = r.DocumentName;

        // missing fonts
        bool hasFonts = r.MissingFonts.Count > 0;
        FontsSection.IsVisible = hasFonts;
        FontsList.ItemsSource = hasFonts ? r.MissingFonts : null;

        Fill(r, CompatibilityReport.Severity.Rasterised, RasterisedSection, RasterisedHeader, RasterisedList,
             Loc.T("compatReport.rasterised"));
        Fill(r, CompatibilityReport.Severity.Partial, PartialSection, PartialHeader, PartialList,
             Loc.T("compatReport.partial"));
        Fill(r, CompatibilityReport.Severity.Skipped, SkippedSection, SkippedHeader, SkippedList,
             Loc.T("compatReport.skipped"));
        Fill(r, CompatibilityReport.Severity.Structural, StructuralSection, StructuralHeader, StructuralList,
             Loc.T("compatReport.structural"));

        CleanLabel.IsVisible = !r.HasIssues;
    }

    private static void Fill(CompatibilityReport r, CompatibilityReport.Severity s,
        StackPanel section, TextBlock header, ItemsControl list, string label)
    {
        var entries = r.Entries.Where(e => e.Kind == s).ToList();
        bool any = entries.Count > 0;
        section.IsVisible = any;
        if (!any) return;
        header.Text = $"{label} ({entries.Count})";
        list.ItemsSource = entries.Select(e =>
            string.IsNullOrEmpty(e.Layer) ? e.Message : $"\"{e.Layer}\": {e.Message}").ToList();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
