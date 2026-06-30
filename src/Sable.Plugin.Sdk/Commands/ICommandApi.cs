namespace Sable.Plugin.Sdk.Commands;

/// <summary>
/// A command a plugin contributes (capability <c>command.register</c>). Surfaced in the
/// command palette (and, if also requested, a menu). <see cref="Run"/> executes on the host
/// UI thread; long work should report progress / honour cancellation via its own mechanism.
/// </summary>
public sealed record PluginCommand
{
    /// <summary>Stable id, unique within the plugin (host namespaces it as "&lt;pluginId&gt;.&lt;id&gt;").</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Palette grouping label, e.g. "Export" or the plugin name. Optional.</summary>
    public string? Category { get; init; }

    public required Action Run { get; init; }
}

/// <summary>Command registration surface. Null when <c>command.register</c> not granted.</summary>
public interface ICommandApi
{
    void Register(PluginCommand command);
}
