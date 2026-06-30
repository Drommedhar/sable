namespace Sable.Plugin.Sdk.Manifest;

/// <summary>
/// Outcome of parsing + validating a manifest. <see cref="Ok"/> implies
/// <see cref="Manifest"/> is non-null and <see cref="Errors"/> is empty.
/// A failed result carries every problem found (not just the first) so a plugin
/// author sees all manifest issues at once.
/// </summary>
public sealed class ManifestParseResult
{
    private ManifestParseResult(PluginManifest? manifest, IReadOnlyList<string> errors)
    {
        Manifest = manifest;
        Errors = errors;
    }

    public PluginManifest? Manifest { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool Ok => Manifest is not null && Errors.Count == 0;

    public static ManifestParseResult Success(PluginManifest manifest)
        => new(manifest, Array.Empty<string>());

    public static ManifestParseResult Failure(IReadOnlyList<string> errors)
        => new(null, errors.Count == 0 ? new[] { "unknown manifest error" } : errors);

    public static ManifestParseResult Failure(string error)
        => new(null, new[] { error });
}
