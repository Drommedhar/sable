using System.IO;
using Sable.Ai.Sidecar.Provisioning;

namespace Sable.Ai.Comfy.Provisioning;

/// <summary>A usable ComfyUI install: its directory (holds main.py) + the python that runs it.</summary>
public sealed record ComfyInstall(string ComfyDir, string PythonExe);

/// <summary>
/// Locates a runnable ComfyUI (PHASE8_AI_COMFY §2.4). Reuses the sidecar's <see cref="ComfyEnvLocator"/> for
/// venv-python candidates + the host-OS gate, so a foreign-OS ComfyUI (Linux venv on Windows) is rejected and
/// Sable provisions its own instead. Path logic is pure; existence checks are injectable for tests.
/// </summary>
public static class ComfyLocator
{
    public static string OwnComfyDir =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Sable", "comfy");

    /// <summary>The ComfyUI root for a user-picked models path (strips a trailing <c>models</c> leaf).</summary>
    public static string RootFrom(string userModelsOrRoot) => ComfyEnvLocator.RootFrom(userModelsOrRoot);

    /// <summary>
    /// Find a SAME-OS usable ComfyUI for the user's models path: its root must hold <c>main.py</c> and a
    /// host-compatible venv python. Returns null when none (→ provision own). <paramref name="exists"/>
    /// defaults to <see cref="File.Exists"/>.
    /// </summary>
    public static ComfyInstall? LocateSameOs(string userModelsOrRoot, HostOs host, System.Func<string, bool>? exists = null)
    {
        var fileExists = exists ?? File.Exists;
        var root = RootFrom(userModelsOrRoot);
        if (!fileExists(Path.Combine(root, "main.py"))) return null;
        foreach (var c in ComfyEnvLocator.Candidates(userModelsOrRoot))
        {
            if (!ComfyEnvLocator.HostCompatible(c, host)) continue;   // foreign-OS venv → skip
            if (fileExists(c.Path)) return new ComfyInstall(root, c.Path);
        }
        return null;
    }
}
