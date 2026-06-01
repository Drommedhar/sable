using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Sable.App;

/// <summary>
/// Command palette (PLAN §16.14): Ctrl+K fuzzy-search + run any registered action. Modeless,
/// borderless, centred over the owner; Enter/double-click runs, Esc closes, up/down navigate.
/// </summary>
public partial class CommandPalette : Window
{
    private readonly List<(string Name, Action Run)> _all;

    public CommandPalette() : this(new List<(string, Action)>()) { }

    public CommandPalette(List<(string Name, Action Run)> actions)
    {
        InitializeComponent();
        _all = actions;
        Filter("");
        Opened += (_, _) => SearchBox.Focus();
        Deactivated += (_, _) => Close();
    }

    private void Filter(string q)
    {
        IEnumerable<(string Name, Action Run)> items = _all;
        if (!string.IsNullOrWhiteSpace(q))
        {
            string s = q.Trim();
            items = _all.Where(a => a.Name.Contains(s, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(a => a.Name.StartsWith(s, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        }
        ResultList.ItemsSource = items.Select(a => a.Name).ToList();
        if (ResultList.ItemCount > 0) ResultList.SelectedIndex = 0;
    }

    private void OnSearch(object? sender, TextChangedEventArgs e) => Filter(SearchBox.Text ?? "");

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: ResultList.SelectedIndex = Math.Min(ResultList.ItemCount - 1, ResultList.SelectedIndex + 1); e.Handled = true; break;
            case Key.Up: ResultList.SelectedIndex = Math.Max(0, ResultList.SelectedIndex - 1); e.Handled = true; break;
            case Key.Enter: RunSelected(); e.Handled = true; break;
            case Key.Escape: Close(); e.Handled = true; break;
        }
    }

    private void OnRunSelected(object? sender, RoutedEventArgs e) => RunSelected();

    private void RunSelected()
    {
        if (ResultList.SelectedItem is not string name) return;
        var action = _all.FirstOrDefault(a => a.Name == name).Run;
        Close();
        action?.Invoke();
    }
}
