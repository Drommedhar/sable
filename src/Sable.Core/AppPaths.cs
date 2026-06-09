using System;
using System.IO;

namespace Sable.Core;

/// <summary>
/// Resolves content that ships beside the app (locale JSON, the AI sidecar's <c>server/</c>, …).
/// On Windows/Linux that content sits next to the executable (<see cref="AppContext.BaseDirectory"/>).
/// In a macOS <c>.app</c> bundle the executable lives in <c>Contents/MacOS</c> but non-code resources
/// must live in <c>Contents/Resources</c> — codesign refuses to seal a bundle that has non-code files
/// nested under <c>Contents/MacOS</c> ("code object is not signed at all / In subcomponent: …"). The
/// packaging step therefore drops resource directories into <c>Resources</c>, and this helper looks
/// there as a fallback so the running app still finds them.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Absolute path to a content file or directory shipped with the app. Probes the executable
    /// directory first, then the macOS bundle's sibling <c>../Resources</c>. If neither exists the
    /// primary (next-to-executable) path is returned unchanged, preserving the prior behavior.
    /// </summary>
    public static string ResolveContent(string relativePath)
    {
        var baseDir = AppContext.BaseDirectory;

        var primary = Path.Combine(baseDir, relativePath);
        if (File.Exists(primary) || Directory.Exists(primary)) return primary;

        // macOS .app: Contents/MacOS (== baseDir) holds code; resources live in Contents/Resources.
        var resources = Path.Combine(baseDir, "..", "Resources", relativePath);
        if (File.Exists(resources) || Directory.Exists(resources)) return resources;

        return primary;
    }
}
