using System;
using System.Collections.Generic;
using Sable.Plugin.Sdk.Host;

namespace Sable.Plugins;

/// <summary>One captured log entry (host diagnostics, PLUGIN_SDK_PLAN.md §12.2).</summary>
public readonly record struct PluginLogEntry(string PluginId, LogLevel Level, string Message, Exception? Error);

/// <summary>
/// Collects log entries from every plugin, tagged with the plugin id, for the plugin manager's
/// diagnostics view. Hand each plugin a <see cref="For"/> logger; entries land in <see cref="Entries"/>
/// (capped) and are forwarded to an optional sink (e.g. Debug output).
/// </summary>
public sealed class PluginLogHub
{
    private readonly List<PluginLogEntry> _entries = new();
    private readonly Action<PluginLogEntry>? _sink;
    private readonly int _cap;

    public PluginLogHub(Action<PluginLogEntry>? sink = null, int cap = 1000)
    {
        _sink = sink;
        _cap = cap;
    }

    public IReadOnlyList<PluginLogEntry> Entries => _entries;

    /// <summary>A logger scoped to one plugin id; everything it logs is tagged with that id.</summary>
    public IPluginLogger For(string pluginId) => new Scoped(this, pluginId);

    private void Add(PluginLogEntry e)
    {
        _entries.Add(e);
        if (_entries.Count > _cap) _entries.RemoveRange(0, _entries.Count - _cap);
        _sink?.Invoke(e);
    }

    private sealed class Scoped : IPluginLogger
    {
        private readonly PluginLogHub _hub;
        private readonly string _id;
        public Scoped(PluginLogHub hub, string id) { _hub = hub; _id = id; }
        public void Log(LogLevel level, string message, Exception? error = null)
            => _hub.Add(new PluginLogEntry(_id, level, message, error));
    }
}
