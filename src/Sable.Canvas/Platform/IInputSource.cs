namespace Sable.Canvas.Platform;

/// <summary>Canvas pointer button (only Left/Middle are used today; Right reserved).</summary>
public enum CanvasButton { Left, Middle, Right }

/// <summary>Keyboard modifiers active at a canvas input event.</summary>
[Flags]
public enum CanvasMods { None = 0, Shift = 1, Alt = 2, Ctrl = 4 }

/// <summary>
/// Shared, OS-agnostic canvas pointer handlers. Coordinates are SURFACE pixels; the
/// control maps them to document space via the viewport transform. Implemented by
/// <c>GpuSurfaceControl</c> — all tool logic lives here, identical on every platform.
/// </summary>
public interface ICanvasInputSink
{
    void PointerDown(CanvasButton button, double sx, double sy, CanvasMods mods);
    void PointerMove(double sx, double sy, CanvasMods mods);
    void PointerUp(CanvasButton button, double sx, double sy, CanvasMods mods);
    void Wheel(double sx, double sy, int delta, CanvasMods mods);
}

/// <summary>
/// Per-OS source of canvas input. Decodes native mouse/key events the embedded GPU
/// surface receives directly (Avalonia can't see them over a native surface —
/// "airspace") into <see cref="ICanvasInputSink"/> calls. Windows = WndProc subclass.
/// </summary>
public interface IInputSource : IDisposable
{
    /// <summary>Begin delivering events from <paramref name="windowHandle"/> to <paramref name="sink"/>.</summary>
    void Attach(nint windowHandle, ICanvasInputSink sink);

    /// <summary>Capture/release the pointer for the duration of a drag gesture.</summary>
    void Capture();
    void ReleaseCapture();

    /// <summary>Hide the OS cursor + remember its position (HUD brush adjust).</summary>
    void HideCursor();
    /// <summary>Restore the OS cursor and warp it back to where <see cref="HideCursor"/> hid it.</summary>
    void RestoreCursor();
}
