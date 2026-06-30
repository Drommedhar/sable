using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sable.App.Localization;
using Sable.Plugin.Sdk.Automation;

namespace Sable.App;

/// <summary>
/// Batch-processing setup dialog (capability <c>automation.batch</c>): pick a plugin-contributed
/// <see cref="BatchOperation"/>, queue input files, and run. The actual run (headless, progress +
/// cancel) is owned by MainWindow via the <c>onRun</c> callback — this window just collects the
/// operation + file list, then closes.
/// </summary>
public partial class BatchRunWindow : Window
{
    private readonly List<BatchOperation> _ops;
    private readonly Action<BatchOperation, IReadOnlyList<string>> _onRun;
    private readonly ObservableCollection<string> _files = new();

    public BatchRunWindow() : this(new List<BatchOperation>(), (_, _) => { }) { }

    public BatchRunWindow(IReadOnlyList<BatchOperation> ops, Action<BatchOperation, IReadOnlyList<string>> onRun)
    {
        InitializeComponent();
        _ops = ops.ToList();
        _onRun = onRun;

        OpCombo.ItemsSource = _ops.Select(o => string.IsNullOrWhiteSpace(o.Category) ? o.Title : $"{o.Category}: {o.Title}").ToList();
        if (_ops.Count > 0) OpCombo.SelectedIndex = 0;
        FilesList.ItemsSource = _files;
        UpdateState();
    }

    private void UpdateState()
    {
        CountText.Text = Loc.T("batchWindow.fileCount", _files.Count);
        RunBtn.IsEnabled = _files.Count > 0 && OpCombo.SelectedIndex >= 0;
        ClearBtn.IsEnabled = _files.Count > 0;
    }

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("batchWindow.addFiles"),
            AllowMultiple = true,
        });
        foreach (var f in picked)
            if (f.TryGetLocalPath() is { } p && !_files.Contains(p)) _files.Add(p);
        UpdateState();
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        _files.Clear();
        UpdateState();
    }

    private void OnRun(object? sender, RoutedEventArgs e)
    {
        int i = OpCombo.SelectedIndex;
        if (i < 0 || i >= _ops.Count || _files.Count == 0) return;
        _onRun(_ops[i], _files.ToList());
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
