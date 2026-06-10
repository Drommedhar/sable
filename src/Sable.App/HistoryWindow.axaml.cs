using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sable.Core.Undo;
using Sable.UI.ViewModels;

using Sable.App.Localization;

namespace Sable.App;

/// <summary>
/// History panel (PLAN §16.13): a list of undo states (click to jump) + named snapshots. Modeless,
/// bound to the active DocumentViewModel; rebuilds from its <see cref="UndoStack"/> on every change.
/// </summary>
public partial class HistoryWindow : Window
{
    private DocumentViewModel? _vm;
    private bool _syncing;
    private int _snapCounter = 1;

    public HistoryWindow()
    {
        InitializeComponent();
        WindowEscapeHelper.AddEscapeClose(this);
        DataContextChanged += (_, _) => Bind(DataContext as DocumentViewModel);
        Bind(DataContext as DocumentViewModel);
    }

    private void Bind(DocumentViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm is not null) _vm.Undo.Changed -= Refresh;
        _vm = vm;
        if (_vm is not null) _vm.Undo.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        if (HistoryList is null) return;
        _syncing = true;
        if (_vm is { } vm)
        {
            var items = new List<string> { Loc.T("historyWindow.open") };
            items.AddRange(vm.Undo.History.Select(c => c.Name));
            HistoryList.ItemsSource = items;
            HistoryList.SelectedIndex = vm.Undo.Cursor;
            SnapshotList.ItemsSource = vm.Snapshots.Select(s => s.Name).ToList();
            Scrubber.Maximum = vm.Undo.History.Count;
            Scrubber.Value = vm.Undo.Cursor;
        }
        else { HistoryList.ItemsSource = null; SnapshotList.ItemsSource = null; Scrubber.Maximum = 0; }
        _syncing = false;
    }

    private void OnScrub(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || _vm is not { } vm) return;
        int target = (int)System.Math.Round(e.NewValue);
        if (target != vm.Undo.Cursor) vm.Undo.JumpTo(target);
    }

    private void OnHistorySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is not { } vm || HistoryList.SelectedIndex < 0) return;
        vm.Undo.JumpTo(HistoryList.SelectedIndex);   // index 0 = initial state
    }

    private void OnAddSnapshot(object? sender, RoutedEventArgs e)
    {
        _vm?.CaptureSnapshot(Loc.T("historyWindow.snapshotName", _snapCounter++));
        Refresh();
    }

    private void OnRestoreSnapshot(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm && SnapshotList.SelectedIndex >= 0) vm.RestoreSnapshot(SnapshotList.SelectedIndex);
    }
}
