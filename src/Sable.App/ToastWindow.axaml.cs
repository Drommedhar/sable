using System;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sable.App;

/// <summary>One notification card (title + body + optional action button).</summary>
public sealed class ToastItem
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string? ActionLabel { get; init; }
    public Action? Action { get; init; }
    public bool HasAction => ActionLabel is not null;
}

/// <summary>
/// Affinity-style notification toasts: a stack of dismissable cards in the top-right
/// corner of the canvas (missing fonts, import notes, …). A separate top-level window
/// so it floats above the native canvas HWND (airspace-safe, same trick as
/// <see cref="TaskBarWindow"/>). Never takes focus; MainWindow positions it.
/// </summary>
public partial class ToastWindow : Window
{
    public ObservableCollection<ToastItem> Items { get; } = new();

    public ToastWindow()
    {
        InitializeComponent();
        List.ItemsSource = Items;
    }

    public void Push(string title, string body, string? actionLabel = null, Action? action = null)
        => Items.Add(new ToastItem { Title = title, Body = body, ActionLabel = actionLabel, Action = action });

    private void OnDismiss(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is ToastItem t) Remove(t);
    }

    private void OnAction(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not ToastItem t) return;
        t.Action?.Invoke();
        Remove(t);
    }

    private void Remove(ToastItem t)
    {
        Items.Remove(t);
        if (Items.Count == 0) Hide();
    }
}
