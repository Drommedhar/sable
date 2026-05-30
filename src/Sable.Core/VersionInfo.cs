using System.Reflection;

namespace Sable.Core;

/// <summary>Application version, derived from assembly metadata (set via VersionPrefix in CI).</summary>
public static class VersionInfo
{
    private static readonly Lazy<string> _version = new(() => ReadVersion(typeof(VersionInfo).Assembly));

    /// <summary>Semantic version string (e.g. "1.2.0" or "0.1.0").</summary>
    public static string Version => _version.Value;

    /// <summary>True when running a local/dev build (no real release version).</summary>
    public static bool IsDev => Version.StartsWith("0.0", StringComparison.Ordinal);

    private static string ReadVersion(Assembly asm)
    {
        // InformationalVersion carries the SemVer (may have +commit metadata); strip build metadata.
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        var v = asm.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
