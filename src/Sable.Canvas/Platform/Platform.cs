namespace Sable.Canvas.Platform;

/// <summary>Selects the OS canvas backend once. The rest of the canvas is platform-agnostic.</summary>
public static class CanvasPlatform
{
    private static IPlatformBackend? _current;

    /// <summary>The backend for the running OS (Windows + macOS real; Linux stub for now).</summary>
    public static IPlatformBackend Current => _current ??= Create();

    private static IPlatformBackend Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsBackend();
        if (OperatingSystem.IsMacOS()) return new MacBackend();
        if (OperatingSystem.IsLinux()) return new UnsupportedBackend("Linux");
        return new UnsupportedBackend(System.Runtime.InteropServices.RuntimeInformation.OSDescription);
    }
}
