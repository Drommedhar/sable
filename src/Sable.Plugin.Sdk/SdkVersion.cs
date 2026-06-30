namespace Sable.Plugin.Sdk;

/// <summary>
/// SDK version + compatibility negotiation (PLUGIN_SDK_PLAN.md §17).
/// The SDK uses a single integer MAJOR version. A plugin declares the major it was
/// built against in its manifest (<c>sdk_version</c>); the host accepts a plugin only
/// when <see cref="IsCompatible"/> returns true.
///
/// P0 policy: exact major match. The range is expressed as [<see cref="MinSupportedMajor"/>,
/// <see cref="Current"/>] so a future host can widen backward compatibility in one place
/// without touching call sites.
/// </summary>
public static class SdkVersion
{
    /// <summary>Current SDK major version. Bump only on a breaking contract change.</summary>
    public const int Current = 1;

    /// <summary>Oldest plugin SDK major this host still loads. P0: same as <see cref="Current"/>.</summary>
    public const int MinSupportedMajor = 1;

    /// <summary>True when a plugin built against <paramref name="pluginMajor"/> is loadable here.</summary>
    public static bool IsCompatible(int pluginMajor)
        => pluginMajor >= MinSupportedMajor && pluginMajor <= Current;

    /// <summary>
    /// Parse a manifest <c>sdk_version</c> string ("1", "1.x", " 1 ") to its major int.
    /// Returns false on null/empty/non-numeric leading token.
    /// </summary>
    public static bool TryParseMajor(string? text, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var token = text.Trim();
        var dot = token.IndexOf('.');
        if (dot >= 0) token = token[..dot];
        return int.TryParse(token, out major);
    }
}
