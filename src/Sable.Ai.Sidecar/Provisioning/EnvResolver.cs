using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Sable.Ai.Sidecar.Provisioning;

/// <summary>Inputs for resolving the sidecar interpreter (§3.1). <see cref="Exists"/> is injectable for tests.</summary>
public sealed record EnvResolveOptions(
    string? PinnedPython,        // SableSettings.SidecarPython (explicit user choice)
    string? ComfyModelsPath,     // the ComfyUI models folder (or root) the user added as a source
    string? OwnVenvPython,       // Sable's provisioned venv python, if it already exists
    HostOs Host,
    Func<string, bool>? Exists = null);

/// <summary>
/// Resolves which Python the sidecar runs under, in order: pinned → host-OS-compatible ComfyUI venv (probed)
/// → Sable's own venv (PHASE8_AI_SIDECAR §3.1). The ComfyUI step applies the host-OS gate first, so a
/// foreign-OS venv (Linux ComfyUI on Windows) is skipped WITHOUT probing — resolution then falls to the own
/// venv, while the ComfyUI weights remain reusable elsewhere. Returns null when nothing usable exists yet
/// (the caller then provisions via <see cref="UvEnv"/>). The probe is the only async/IO part; everything
/// else is pure + unit-tested with fakes.
/// </summary>
public static class EnvResolver
{
    public static async Task<PythonEnv?> ResolveAsync(EnvResolveOptions o, IEnvProbe probe, CancellationToken ct = default)
    {
        var exists = o.Exists ?? File.Exists;

        // 1) explicit user-pinned interpreter
        if (!string.IsNullOrWhiteSpace(o.PinnedPython) && exists(o.PinnedPython))
        {
            var caps = await probe.ProbeAsync(o.PinnedPython!, ct).ConfigureAwait(false);
            if (caps is not null && caps.IsUsable(out _)) return new PythonEnv(o.PinnedPython!, "pinned", caps);
        }

        // 2) a usable, host-OS-compatible ComfyUI venv
        if (!string.IsNullOrWhiteSpace(o.ComfyModelsPath))
        {
            foreach (var c in ComfyEnvLocator.Candidates(o.ComfyModelsPath!))
            {
                if (!ComfyEnvLocator.HostCompatible(c, o.Host)) continue;   // foreign-OS venv → never probed
                if (!exists(c.Path)) continue;
                var caps = await probe.ProbeAsync(c.Path, ct).ConfigureAwait(false);
                if (caps is not null && caps.IsUsable(out _)) return new PythonEnv(c.Path, "comfyui", caps);
            }
        }

        // 3) Sable's own provisioned venv
        if (!string.IsNullOrWhiteSpace(o.OwnVenvPython) && exists(o.OwnVenvPython))
        {
            var caps = await probe.ProbeAsync(o.OwnVenvPython!, ct).ConfigureAwait(false);
            if (caps is not null && caps.IsUsable(out _)) return new PythonEnv(o.OwnVenvPython!, "sable", caps);
        }

        return null;
    }
}
