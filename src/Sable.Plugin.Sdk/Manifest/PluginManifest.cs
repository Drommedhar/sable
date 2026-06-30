using Sable.Plugin.Sdk.Permissions;

namespace Sable.Plugin.Sdk.Manifest;

/// <summary>
/// A validated plugin manifest (PLUGIN_SDK_PLAN.md §16). Produced by
/// <see cref="ManifestParser.Parse"/>; never construct directly from untrusted input —
/// go through the parser so required fields + capability/SDK checks run.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>Reverse-DNS unique id, e.g. "com.example.myplugin".</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Plugin's own version string (semver recommended; not enforced).</summary>
    public required string Version { get; init; }

    /// <summary>Declared SDK major (raw text from the manifest).</summary>
    public required string SdkVersion { get; init; }

    /// <summary>Parsed SDK major derived from <see cref="SdkVersion"/>.</summary>
    public required int SdkMajor { get; init; }

    /// <summary>Fully-qualified .NET type name implementing <see cref="IPlugin"/>.</summary>
    public required string Entrypoint { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }

    public required PluginPermissions Permissions { get; init; }

    public string? Author { get; init; }
    public string? Website { get; init; }
    public string? Support { get; init; }

    /// <summary>Minimum host (app) version, optional. Compared lexically by the host, not here.</summary>
    public string? MinHostVersion { get; init; }

    public bool HasCapability(string capability) => Capabilities.Contains(capability);
}
