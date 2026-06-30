namespace Sable.Plugin.Sdk.Ui;

/// <summary>
/// A menu item a plugin contributes (capability <c>ui.menu_command</c>). Items land under a
/// host-managed Plugins menu; <see cref="MenuPath"/> gives nested sub-menus.
/// </summary>
public sealed record MenuContribution
{
    /// <summary>Stable id, unique within the plugin.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>
    /// Slash-separated sub-menu path under the host Plugins menu, e.g. "Export/Batch".
    /// Empty/null = directly under Plugins.
    /// </summary>
    public string? MenuPath { get; init; }

    public required Action Run { get; init; }
}

/// <summary>Menu contribution surface. Null when <c>ui.menu_command</c> not granted.</summary>
public interface IMenuApi
{
    void AddCommand(MenuContribution item);
}
