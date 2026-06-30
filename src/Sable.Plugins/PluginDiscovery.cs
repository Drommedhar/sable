namespace Sable.Plugins;

/// <summary>
/// Finds plugin candidates on disk. Convention: a plugins root directory contains one
/// sub-directory per plugin, each with a <c>manifest.json</c> at its top level. Discovery does
/// NOT parse/validate — it just locates candidates; <see cref="PluginLoader"/> validates them.
/// </summary>
public static class PluginDiscovery
{
    public const string ManifestFileName = "manifest.json";

    /// <summary>
    /// Enumerate plugin candidates under <paramref name="rootDir"/>. Returns empty when the
    /// root doesn't exist. A sub-directory without a manifest is skipped silently.
    /// </summary>
    public static IReadOnlyList<LoadedPlugin> Discover(string rootDir)
    {
        var result = new List<LoadedPlugin>();
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
            return result;

        foreach (var dir in Directory.EnumerateDirectories(rootDir))
        {
            var manifestPath = Path.Combine(dir, ManifestFileName);
            if (File.Exists(manifestPath))
                result.Add(new LoadedPlugin(dir, manifestPath));
        }
        return result;
    }
}
