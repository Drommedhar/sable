using System.Runtime.InteropServices;

namespace Sable.Canvas.Platform;

/// <summary>
/// Windows canvas input: subclasses the native child HWND's WndProc to capture the
/// mouse messages the embedded GPU surface receives directly (airspace), decoding them
/// into shared <see cref="ICanvasInputSink"/> calls. Surface-pixel coordinates; the
/// sink (the control) maps to document space.
/// </summary>
internal sealed class WindowsInputSource : IInputSource
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A,
        WM_MOUSEACTIVATE = 0x0021,
        // Windows Ink pointer messages (pens raise these BEFORE the synthesized legacy mouse
        // messages, so the pressure is current when the mouse path paints)
        WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247;
    private const nint MA_NOACTIVATE = 3;
    private const uint PT_PEN = 3;

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint prev, nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hWnd);
    [DllImport("user32.dll", EntryPoint = "ReleaseCapture")] private static extern bool ReleaseCaptureNative();
    [DllImport("user32.dll")] private static extern short GetKeyState(int vKey);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
    [DllImport("user32.dll")] private static extern bool GetPointerType(uint pointerId, out uint pointerType);
    [DllImport("user32.dll")] private static extern bool GetPointerPenInfo(uint pointerId, out PointerPenInfo penInfo);

    // POINTER_PEN_INFO: POINTER_INFO (96 bytes on x64) followed by the pen fields we want.
    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public uint pointerType, pointerId, frameId, pointerFlags;
        public nint sourceDevice, hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerPenInfo
    {
        public PointerInfo pointerInfo;
        public uint penFlags, penMask, pressure, rotation;
        public int tiltX, tiltY;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private POINT _savedCursor;
    private bool _cursorHidden;

    private WndProcDelegate? _wndProc;
    private nint _orig, _hwnd;
    private ICanvasInputSink? _sink;
    private double _lastX, _lastY;   // last client pos (wheel lParam is screen coords, so we use this)
    private float _pressure = 1f;    // live stylus pressure (WM_POINTER pen); 1 = mouse / pen up

    /// <summary>Stylus pressure 0..1 from Windows Ink; 1 when no pen is down.</summary>
    public float Pressure => _pressure;

    public void Attach(nint windowHandle, ICanvasInputSink sink)
    {
        _hwnd = windowHandle;
        _sink = sink;
        if (_hwnd == 0) return;
        _wndProc = WndProc;
        _orig = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));
    }

    public void Capture() { if (_hwnd != 0) SetCapture(_hwnd); }
    public void ReleaseCapture() => ReleaseCaptureNative();

    public void HideCursor()
    {
        if (_cursorHidden) return;
        GetCursorPos(out _savedCursor);
        ShowCursor(false);
        _cursorHidden = true;
    }

    public void RestoreCursor()
    {
        if (!_cursorHidden) return;
        SetCursorPos(_savedCursor.X, _savedCursor.Y);   // warp back to where the drag began
        ShowCursor(true);
        _cursorHidden = false;
    }

    public void Dispose()
    {
        if (_hwnd != 0 && _orig != 0) SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _orig);
        _orig = 0; _wndProc = null; _sink = null;
    }

    private static CanvasMods Mods()
    {
        var m = CanvasMods.None;
        if ((GetKeyState(0x10) & 0x8000) != 0) m |= CanvasMods.Shift;   // VK_SHIFT
        if ((GetKeyState(0x12) & 0x8000) != 0) m |= CanvasMods.Alt;     // VK_MENU
        if ((GetKeyState(0x11) & 0x8000) != 0) m |= CanvasMods.Ctrl;    // VK_CONTROL
        return m;
    }

    private static (double x, double y) Pt(nint lParam)
    {
        int lp = (int)lParam;
        return ((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // don't steal keyboard focus from the Avalonia window when the canvas is clicked
        if (msg == WM_MOUSEACTIVATE) return MA_NOACTIVATE;

        // Windows Ink: pens raise WM_POINTER* BEFORE the synthesized legacy mouse messages,
        // so reading the pressure here keeps it current for the mouse-driven paint path below.
        // Messages are passed through (CallWindowProc at the bottom) so legacy synthesis continues.
        if (msg is WM_POINTERDOWN or WM_POINTERUPDATE or WM_POINTERUP)
        {
            uint id = (uint)((int)wParam & 0xFFFF);
            if (msg == WM_POINTERUP) _pressure = 1f;
            else if (GetPointerType(id, out var pt) && pt == PT_PEN && GetPointerPenInfo(id, out var pen))
                _pressure = pen.pressure > 0 ? Math.Clamp(pen.pressure / 1024f, 0.01f, 1f) : 1f;
        }

        if (_sink is { } s)
        {
            switch (msg)
            {
                case WM_MOUSEMOVE: { var (x, y) = Pt(lParam); _lastX = x; _lastY = y; s.PointerMove(x, y, Mods()); break; }
                case WM_LBUTTONDOWN: { var (x, y) = Pt(lParam); s.PointerDown(CanvasButton.Left, x, y, Mods()); break; }
                case WM_LBUTTONUP: { var (x, y) = Pt(lParam); s.PointerUp(CanvasButton.Left, x, y, Mods()); break; }
                case WM_MBUTTONDOWN: { var (x, y) = Pt(lParam); s.PointerDown(CanvasButton.Middle, x, y, Mods()); break; }
                case WM_MBUTTONUP: { var (x, y) = Pt(lParam); s.PointerUp(CanvasButton.Middle, x, y, Mods()); break; }
                case WM_MOUSEWHEEL: { short d = (short)(((int)wParam >> 16) & 0xFFFF); s.Wheel(_lastX, _lastY, d, Mods()); break; }
            }
        }
        return CallWindowProc(_orig, hWnd, msg, wParam, lParam);
    }
}
