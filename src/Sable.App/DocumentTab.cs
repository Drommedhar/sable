using CommunityToolkit.Mvvm.ComponentModel;
using Sable.Engine;
using Sable.UI.ViewModels;

namespace Sable.App;

/// <summary>
/// One open document in the tab strip (PLAN §13.1 ① / Phase 2): its <see cref="Document"/>,
/// a dedicated <see cref="DocumentViewModel"/> (own undo stack), file path, title and
/// dirty flag. Switching tabs swaps the canvas + DataContext to the active tab.
/// </summary>
public sealed partial class DocumentTab : ObservableObject
{
    public Document Doc { get; }
    public DocumentViewModel Vm { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isActive;

    /// <summary>Last saved path (.sable), or null if never saved.</summary>
    public string? Path { get; set; }

    /// <summary>Stable id for the autosave/recovery file of this tab (PLAN §2.6).</summary>
    public string RecoveryId { get; } = System.Guid.NewGuid().ToString("N");

    public DocumentTab(Document doc, string? path, string title)
    {
        Doc = doc;
        Vm = new DocumentViewModel(doc);
        Path = path;
        _title = title;
        Vm.Undo.Changed += () => IsDirty = true;
    }

    public string DisplayTitle => IsDirty ? Title + " •" : Title;

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(DisplayTitle));
}
