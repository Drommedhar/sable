using System.IO;
using System.Runtime.InteropServices;

namespace Sable.Ai.Sidecar.Provisioning;

/// <summary>Which OS-family layout a venv interpreter belongs to (Windows <c>Scripts\python.exe</c> vs Posix
/// <c>bin/python</c>).</summary>
public enum EnvOsKind { Windows, Posix }

/// <summary>A candidate interpreter path + the OS family that can actually execute it.</summary>
public sealed record EnvCandidate(string Path, EnvOsKind Kind);

/// <summary>
/// Pure enumeration of the interpreters a ComfyUI install might expose, plus the host-OS gate
/// (PHASE8_AI_SIDECAR §3.2). Given the path the user picked (the ComfyUI <c>models</c> folder, or the root),
/// list the standard venv / portable layouts. The gate (<see cref="HostCompatible"/>) is what rejects a
/// foreign-OS venv — the explicit Linux-ComfyUI-on-Windows case — BEFORE any subprocess is launched. No IO
/// here: candidate strings + the gate are pure and unit-tested; existence + probing happen in the resolver.
/// </summary>
public static class ComfyEnvLocator
{
    /// <summary>The OS this process runs on.</summary>
    public static HostOs CurrentHost()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? HostOs.Windows
         : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? HostOs.MacOS
         : HostOs.Linux;

    /// <summary>True if the host OS can execute this candidate (Windows↔Windows, Linux/macOS↔Posix).</summary>
    public static bool HostCompatible(EnvCandidate c, HostOs host)
        => host == HostOs.Windows ? c.Kind == EnvOsKind.Windows : c.Kind == EnvOsKind.Posix;

    /// <summary>
    /// Normalise the user-picked path to the ComfyUI root: if they pointed at the <c>models</c> folder, the
    /// venv lives in its parent. Otherwise treat the path itself as the root.
    /// </summary>
    public static string RootFrom(string picked)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(picked);
        var leaf = Path.GetFileName(trimmed);
        if (string.Equals(leaf, "models", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(trimmed) ?? trimmed;
        return trimmed;
    }

    /// <summary>All candidate interpreter paths for a ComfyUI install, both OS families (caller gates+exists).</summary>
    public static IReadOnlyList<EnvCandidate> Candidates(string pickedPath)
    {
        var root = RootFrom(pickedPath);
        var parent = Path.GetDirectoryName(root) ?? root;
        var list = new List<EnvCandidate>
        {
            // standard venv / .venv
            new(Path.Combine(root, "venv", "Scripts", "python.exe"), EnvOsKind.Windows),
            new(Path.Combine(root, ".venv", "Scripts", "python.exe"), EnvOsKind.Windows),
            new(Path.Combine(root, "venv", "bin", "python"), EnvOsKind.Posix),
            new(Path.Combine(root, ".venv", "bin", "python"), EnvOsKind.Posix),
            // ComfyUI_windows_portable: python_embeded sits NEXT TO the ComfyUI folder, sometimes inside it
            new(Path.Combine(parent, "python_embeded", "python.exe"), EnvOsKind.Windows),
            new(Path.Combine(root, "python_embeded", "python.exe"), EnvOsKind.Windows),
        };
        return list;
    }
}
