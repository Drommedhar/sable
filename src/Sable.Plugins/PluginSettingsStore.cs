using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Sable.Plugin.Sdk.Host;

namespace Sable.Plugins;

/// <summary>
/// Per-plugin key/value settings persisted in a namespace private to one plugin
/// (PLUGIN_SDK_PLAN.md §12 / boundary map §2.6). Each plugin gets its own JSON file under the
/// host settings dir, named by a filesystem-safe form of the plugin id, so plugins can't read
/// each other's or the host's settings. In-memory writes flush on <see cref="Save"/>.
/// </summary>
public sealed class PluginSettingsStore : IPluginSettings
{
    private readonly string _path;
    private readonly Dictionary<string, string> _values;

    public PluginSettingsStore(string settingsDir, string pluginId)
    {
        _path = Path.Combine(settingsDir, SafeFileName(pluginId) + ".json");
        _values = Load(_path);
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string? value)
    {
        if (value is null) _values.Remove(key);
        else _values[key] = value;
    }

    public bool Contains(string key) => _values.ContainsKey(key);

    public void Remove(string key) => _values.Remove(key);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_values));
        }
        catch { /* best-effort persistence */ }
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch { /* corrupt → empty */ }
        return new();
    }

    /// <summary>Map a reverse-DNS id to a safe single file name (invalid chars → '_').</summary>
    public static string SafeFileName(string pluginId)
    {
        var chars = pluginId.ToCharArray();
        foreach (var bad in Path.GetInvalidFileNameChars())
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] == bad) chars[i] = '_';
        var s = new string(chars).Trim();
        return string.IsNullOrEmpty(s) ? "plugin" : s;
    }
}
