namespace Sable.Plugin.Sdk.Host;

public enum LogLevel { Debug = 0, Info = 1, Warning = 2, Error = 3 }

/// <summary>
/// Per-plugin logger (PLUGIN_SDK_PLAN.md §12.2 diagnostics). The host tags every entry with
/// the plugin id and routes it to the plugin's diagnostics log. Plugins should log here rather
/// than to Console so output is attributable and shown in the plugin manager.
/// </summary>
public interface IPluginLogger
{
    void Log(LogLevel level, string message, Exception? error = null);

    void Debug(string message) => Log(LogLevel.Debug, message);
    void Info(string message) => Log(LogLevel.Info, message);
    void Warn(string message) => Log(LogLevel.Warning, message);
    void Error(string message, Exception? error = null) => Log(LogLevel.Error, message, error);
}
