using System;
using System.Collections.Generic;
using System.Linq;
using Sable.Plugin.Sdk.Manifest;
using Sable.Plugin.Sdk.Permissions;

namespace Sable.Plugins;

/// <summary>
/// User-consent bookkeeping for plugins (PLUGIN_SDK_PLAN.md §12). A plugin runs only after the
/// user approves the exact set of capabilities + permissions it requests. Approval is recorded as
/// a <see cref="Fingerprint"/> of that set, so if a later version of the plugin asks for MORE
/// access the fingerprint changes and the user is re-prompted — it can't silently widen its reach.
/// Pure logic; the host persists the id→fingerprint map (e.g. in settings).
/// </summary>
public static class PluginConsent
{
    /// <summary>A stable fingerprint of everything a plugin is asking for: its capabilities (sorted)
    /// plus its permission scopes. Same request → same string; any change → a new string.</summary>
    public static string Fingerprint(PluginManifest m)
    {
        var caps = m.Capabilities.OrderBy(c => c, StringComparer.Ordinal);
        var p = m.Permissions;
        var perms = new[]
        {
            $"fr={p.FilesystemRead}", $"fw={p.FilesystemWrite}",
            $"net={p.Network}", $"gpu={p.Gpu}", $"clip={p.Clipboard}",
            $"proc={p.ExternalProcess}", $"meta={p.DocumentMetadata}",
        };
        return string.Join("|", caps) + "#" + string.Join(",", perms);
    }

    /// <summary>True when <paramref name="approved"/> records consent matching the plugin's CURRENT request.</summary>
    public static bool IsApproved(IReadOnlyDictionary<string, string> approved, PluginManifest m)
        => approved.TryGetValue(m.Id, out var fp) && fp == Fingerprint(m);

    /// <summary>A human-readable summary of what the plugin requests, for the consent prompt.</summary>
    public static string DescribeRequest(PluginManifest m)
    {
        var lines = new List<string>();
        lines.Add("Capabilities:");
        foreach (var c in m.Capabilities.OrderBy(c => c, StringComparer.Ordinal))
            lines.Add("  • " + c);

        var p = m.Permissions;
        var perms = new List<string>();
        if (p.FilesystemRead != PermissionScope.None) perms.Add($"filesystem read ({p.FilesystemRead})");
        if (p.FilesystemWrite != PermissionScope.None) perms.Add($"filesystem write ({p.FilesystemWrite})");
        if (p.Network) perms.Add("network");
        if (p.Gpu) perms.Add("gpu");
        if (p.Clipboard) perms.Add("clipboard");
        if (p.ExternalProcess) perms.Add("launch external processes");
        if (p.DocumentMetadata) perms.Add("document metadata");
        if (perms.Count > 0)
        {
            lines.Add("");
            lines.Add("Permissions:");
            foreach (var s in perms) lines.Add("  • " + s);
        }
        return string.Join("\n", lines);
    }
}
