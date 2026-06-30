namespace Sable.Plugin.Sdk.Permissions;

/// <summary>Access level for a resource permission (PLUGIN_SDK_PLAN.md §12).</summary>
public enum PermissionScope
{
    /// <summary>Denied.</summary>
    None = 0,
    /// <summary>Granted, but the host confines it (e.g. filesystem limited to a plugin-owned dir).</summary>
    Scoped = 1,
    /// <summary>Granted without host-imposed confinement. Requires explicit user approval.</summary>
    Full = 2,
}

/// <summary>
/// The permission set a plugin declares in its manifest and the user approves
/// (PLUGIN_SDK_PLAN.md §12.1). A plugin gets only what it requests AND the user grants.
/// Filesystem access is scoped (None/Scoped/Full); the rest are on/off.
/// </summary>
public sealed record PluginPermissions
{
    public PermissionScope FilesystemRead { get; init; } = PermissionScope.None;
    public PermissionScope FilesystemWrite { get; init; } = PermissionScope.None;
    public bool Network { get; init; }
    public bool Gpu { get; init; }
    public bool Clipboard { get; init; }
    public bool ExternalProcess { get; init; }
    public bool DocumentMetadata { get; init; }

    /// <summary>A fully-denied permission set (default for a plugin that declares none).</summary>
    public static readonly PluginPermissions None = new();

    public static bool TryParseScope(string? text, out PermissionScope scope)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case null or "" or "none" or "false" or "no": scope = PermissionScope.None; return true;
            case "scoped": scope = PermissionScope.Scoped; return true;
            case "full" or "true" or "yes": scope = PermissionScope.Full; return true;
            default: scope = PermissionScope.None; return false;
        }
    }
}
