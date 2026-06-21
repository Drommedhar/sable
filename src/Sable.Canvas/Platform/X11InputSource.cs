using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;

namespace Sable.Canvas.Platform;

/// <summary>
/// Linux/X11 canvas input. The embedded GPU surface lives in its own X11 child window, so
/// Avalonia never sees the pointer events over it ("airspace" — same problem the Windows
/// WndProc subclass solves). This opens a DEDICATED X11 <c>Display</c> connection, selects
/// pointer events on that child window, and runs an event-pump thread decoding them into the
/// shared <see cref="ICanvasInputSink"/> calls (surface pixels; the control maps to doc space).
///
/// The connection is private to the pump thread (Xlib is not thread-safe per-connection, and
/// wgpu's WSI uses the surface's separate connection). Sink calls mutate GPU/document state, so
/// — unlike the Windows WndProc which already runs on the UI thread — each decoded event is
/// marshalled onto the Avalonia UI thread via the dispatcher, keeping it ordered with the render
/// loop. Drags ride X11's implicit pointer grab (events keep flowing to the press window while a
/// button is held), so <see cref="Capture"/> is a no-op.
/// </summary>
internal sealed unsafe class X11InputSource : IInputSource
{
    // --- libX11 ---
    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint d);
    [DllImport("libX11.so.6")] private static extern int XSelectInput(nint d, nuint w, nint mask);
    [DllImport("libX11.so.6")] private static extern int XNextEvent(nint d, void* ev);
    [DllImport("libX11.so.6")] private static extern int XPending(nint d);
    [DllImport("libX11.so.6")] private static extern int XFlush(nint d);
    [DllImport("libX11.so.6")] private static extern int XWarpPointer(
        nint d, nuint src, nuint dst, int sx, int sy, uint sw, uint sh, int dx, int dy);
    [DllImport("libX11.so.6")] private static extern int XQueryPointer(
        nint d, nuint w, out nuint root, out nuint child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);
    [DllImport("libX11.so.6")] private static extern nuint XCreateBitmapFromData(nint d, nuint drawable, byte[] data, uint w, uint h);
    [DllImport("libX11.so.6")] private static extern nuint XCreatePixmapCursor(nint d, nuint src, nuint mask, ref XColor fg, ref XColor bg, uint x, uint y);
    [DllImport("libX11.so.6")] private static extern int XFreePixmap(nint d, nuint pm);
    [DllImport("libX11.so.6")] private static extern int XFreeCursor(nint d, nuint c);
    [DllImport("libX11.so.6")] private static extern int XDefineCursor(nint d, nuint w, nuint c);
    [DllImport("libX11.so.6")] private static extern int XUndefineCursor(nint d, nuint w);
    [DllImport("libX11.so.6")] private static extern nuint XDefaultRootWindow(nint d);

    // --- XDND (drag-and-drop) P/Invoke ---
    [DllImport("libX11.so.6")] private static extern nuint XInternAtom(nint d, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XChangeProperty(nint d, nuint w, nuint prop, nuint type, int format, int mode, void* data, int nelements);
    [DllImport("libX11.so.6")] private static extern int XSendEvent(nint d, nuint w, [MarshalAs(UnmanagedType.Bool)] bool propagate, nint eventMask, void* ev);
    [DllImport("libX11.so.6")] private static extern int XConvertSelection(nint d, nuint sel, nuint target, nuint property, nuint requestor, nuint time);
    [DllImport("libX11.so.6")] private static extern int XGetWindowProperty(nint d, nuint w, nuint prop, long offset, long length, [MarshalAs(UnmanagedType.Bool)] bool delete, nuint reqType, out nuint actualType, out int actualFormat, out nuint nitems, out nuint bytesAfter, out nint data);
    [DllImport("libX11.so.6")] private static extern int XDeleteProperty(nint d, nuint w, nuint prop);
    [DllImport("libX11.so.6")] private static extern int XFree(void* ptr);

    [StructLayout(LayoutKind.Sequential)]
    private struct XColor { public nuint Pixel; public ushort Red, Green, Blue; public byte Flags, Pad; }

    // X event type codes
    private const int ButtonPress = 4, ButtonRelease = 5, MotionNotify = 6;
    private const int ClientMessage = 33, SelectionNotify = 31;
    // event-mask bits
    private const nint ButtonPressMask = 1 << 2, ButtonReleaseMask = 1 << 3, PointerMotionMask = 1 << 6;
    private const nint NoEventMask = 0;
    // modifier-state bits
    private const uint ShiftMask = 1 << 0, ControlMask = 1 << 2, Mod1Mask = 1 << 3;
    // XPropModeReplace
    private const int PropModeReplace = 0;
    // XEvent union is 24 longs (192 bytes on 64-bit); fields below at fixed offsets.
    private const int EventBufBytes = 192;

    // --- XDND atoms (resolved in Attach) ---
    private nuint _atomXdndAware, _atomXdndEnter, _atomXdndPosition, _atomXdndDrop, _atomXdndLeave;
    private nuint _atomXdndStatus, _atomXdndSelection, _atomXdndActionCopy, _atomUriList;
    private nuint _atomXdndTypeList;
    // XDND drag state
    private nuint _dndSource;        // source window from XdndEnter
    private int _dndVersion;         // XDND protocol version (3 or 5)
    private nuint _dndDropProperty;  // property atom for the SelectionNotify reply
    private nuint _dndDropTime;      // timestamp from XdndDrop

    private nint _display;
    private nuint _window;
    private ICanvasInputSink? _sink;
    private Thread? _pump;
    private volatile bool _running;

    // invisible-cursor state for the brush HUD (Ctrl+Alt drag)
    private nuint _invisCursor;
    private bool _cursorHidden;
    private int _savedRootX, _savedRootY;

    public void Attach(nint windowHandle, ICanvasInputSink sink)
    {
        _sink = sink;
        if (windowHandle == 0) return;
        _display = XOpenDisplay(0);
        if (_display == 0) return;   // no X server: no input (canvas may still render via XWayland)
        _window = (nuint)windowHandle;
        XSelectInput(_display, _window, ButtonPressMask | ButtonReleaseMask | PointerMotionMask);

        // resolve XDND atoms + advertise DnD awareness (Avalonia 12.0 doesn't on X11)
        _atomXdndAware = XInternAtom(_display, "XdndAware", false);
        _atomXdndEnter = XInternAtom(_display, "XdndEnter", false);
        _atomXdndPosition = XInternAtom(_display, "XdndPosition", false);
        _atomXdndDrop = XInternAtom(_display, "XdndDrop", false);
        _atomXdndLeave = XInternAtom(_display, "XdndLeave", false);
        _atomXdndStatus = XInternAtom(_display, "XdndStatus", false);
        _atomXdndSelection = XInternAtom(_display, "XdndSelection", false);
        _atomXdndActionCopy = XInternAtom(_display, "XdndActionCopy", false);
        _atomUriList = XInternAtom(_display, "text/uri-list", false);
        _atomXdndTypeList = XInternAtom(_display, "XdndTypeList", false);
        // XA_ATOM = 4 (built-in atom, no need to intern)
        var dndVersion = 5;   // advertise XDND version 5
        XChangeProperty(_display, _window, _atomXdndAware, 4 /*XA_ATOM*/, 32, PropModeReplace, &dndVersion, 1);
        XFlush(_display);

        _running = true;
        _pump = new Thread(Pump) { IsBackground = true, Name = "Sable X11 input" };
        _pump.Start();
    }

    // X11's implicit pointer grab keeps events flowing to the press window while a button is held,
    // so an explicit grab isn't needed for canvas drags.
    public void Capture() { }
    public void ReleaseCapture() { }

    public void HideCursor()
    {
        if (_cursorHidden || _display == 0) return;
        try
        {
            XQueryPointer(_display, _window, out _, out _, out _savedRootX, out _savedRootY, out _, out _, out _);
            if (_invisCursor == 0)
            {
                var blank = new byte[8];   // 1x1 all-zero bitmap
                nuint pm = XCreateBitmapFromData(_display, _window, blank, 1, 1);
                var c = default(XColor);
                _invisCursor = XCreatePixmapCursor(_display, pm, pm, ref c, ref c, 0, 0);
                XFreePixmap(_display, pm);
            }
            if (_invisCursor != 0) XDefineCursor(_display, _window, _invisCursor);
            XFlush(_display);
            _cursorHidden = true;
        }
        catch { /* best-effort: leaving the cursor visible during the HUD is harmless */ }
    }

    public void RestoreCursor()
    {
        if (!_cursorHidden || _display == 0) return;
        try
        {
            XUndefineCursor(_display, _window);
            // warp the pointer back to where the HUD drag began
            XWarpPointer(_display, 0, XDefaultRootWindow(_display), 0, 0, 0, 0, _savedRootX, _savedRootY);
            XFlush(_display);
        }
        catch { /* ignore */ }
        _cursorHidden = false;
    }

    public void Dispose()
    {
        _running = false;
        _pump?.Join(200);
        _pump = null;
        if (_display != 0)
        {
            if (_invisCursor != 0) { XFreeCursor(_display, _invisCursor); _invisCursor = 0; }
            XCloseDisplay(_display);
            _display = 0;
        }
        _sink = null;
    }

    // Cap coalesced motion delivery to ~the canvas render cadence. X11 emits MotionNotify per mouse
    // poll (up to ~1 kHz) with NO built-in coalescing — unlike Windows' WM_MOUSEMOVE, which the OS
    // queue collapses. Posting every event floods the UI thread (each PointerMove runs tool work like
    // the SAM2 hover lookup + texture upload + recomposite), so we compress a burst to the latest
    // position and rate-limit it. ~7 ms ≈ 140 Hz, matching the render timer.
    private const long MotionMinMs = 7;

    private void Pump()
    {
        byte* buf = stackalloc byte[EventBufBytes];
        bool pendMove = false;
        int mx = 0, my = 0; CanvasMods mmods = CanvasMods.None;
        long lastMove = 0;

        while (_running)
        {
            // XPending flushes output then non-blocking-reads the connection, so queued + freshly
            // arrived events are drained without ever blocking (lets Dispose stop us promptly).
            while (_running && XPending(_display) > 0)
            {
                XNextEvent(_display, buf);
                int type = *(int*)buf;
                if (type == MotionNotify)
                {
                    // coalesce: keep only the most recent position seen this drain
                    mx = *(int*)(buf + 64); my = *(int*)(buf + 68);
                    mmods = Mods(*(uint*)(buf + 80));
                    pendMove = true;
                }
                else if (type is ButtonPress or ButtonRelease)
                {
                    // deliver the pending move first so a click lands at the current cursor position
                    if (pendMove) { var (x, y, m) = (mx, my, mmods); Post(s => s.PointerMove(x, y, m)); pendMove = false; lastMove = Environment.TickCount64; }
                    DecodeButton(type, buf);
                }
                else if (type == ClientMessage)
                {
                    HandleXdndClientMessage(buf);
                }
                else if (type == SelectionNotify)
                {
                    HandleSelectionNotify(buf);
                }
            }

            // emit at most one coalesced move per ~MotionMinMs; a held move still goes out on a later tick
            if (pendMove)
            {
                long now = Environment.TickCount64;
                if (now - lastMove >= MotionMinMs)
                {
                    var (x, y, m) = (mx, my, mmods);
                    Post(s => s.PointerMove(x, y, m));
                    pendMove = false; lastMove = now;
                }
            }
            Thread.Sleep(2);
        }
    }

    private void DecodeButton(int type, byte* ev)
    {
        int x = *(int*)(ev + 64), y = *(int*)(ev + 68);
        var mods = Mods(*(uint*)(ev + 80));
        uint button = *(uint*)(ev + 84);
        if (type == ButtonPress)
        {
            switch (button)
            {
                case 1: Post(s => s.PointerDown(CanvasButton.Left, x, y, mods)); break;
                case 2: Post(s => s.PointerDown(CanvasButton.Middle, x, y, mods)); break;
                case 3: Post(s => s.PointerDown(CanvasButton.Right, x, y, mods)); break;
                case 4: Post(s => s.Wheel(x, y, +1, mods)); break;   // wheel up
                case 5: Post(s => s.Wheel(x, y, -1, mods)); break;   // wheel down
            }
        }
        else   // ButtonRelease
        {
            switch (button)
            {
                case 1: Post(s => s.PointerUp(CanvasButton.Left, x, y, mods)); break;
                case 2: Post(s => s.PointerUp(CanvasButton.Middle, x, y, mods)); break;
                case 3: Post(s => s.PointerUp(CanvasButton.Right, x, y, mods)); break;
                // 4/5 are wheel "presses"; their releases carry no meaning
            }
        }
    }

    private static CanvasMods Mods(uint state)
    {
        var m = CanvasMods.None;
        if ((state & ShiftMask) != 0) m |= CanvasMods.Shift;
        if ((state & ControlMask) != 0) m |= CanvasMods.Ctrl;
        if ((state & Mod1Mask) != 0) m |= CanvasMods.Alt;
        return m;
    }

    /// <summary>Marshal a sink call onto the UI thread (sink mutates GPU/doc state, like the render loop).</summary>
    private void Post(Action<ICanvasInputSink> act)
    {
        if (_sink is not { } s) return;
        Dispatcher.UIThread.Post(() => { if (_running) act(s); }, DispatcherPriority.Input);
    }

    // ===== XDND (X11 drag-and-drop) — Avalonia 12.0 doesn't fire DragDrop on X11 =====

    /// <summary>Handle XdndEnter / XdndPosition / XdndDrop / XdndLeave ClientMessage events.</summary>
    private void HandleXdndClientMessage(byte* ev)
    {
        nuint msgType = *(nuint*)(ev + 40);   // message_type atom
        // data.l[0..4] at offset 56 on 64-bit (after format int + padding)
        long* data = (long*)(ev + 56);

        if (msgType == _atomXdndEnter)
        {
            _dndSource = (nuint)data[0];
            _dndVersion = (int)((data[1] >> 24) & 0xFF);
            // we accept text/uri-list regardless of the source's offered types — most file managers offer it
        }
        else if (msgType == _atomXdndPosition)
        {
            // reply XdndStatus: accept the drop, action=Copy
            SendXdndStatus((nuint)data[0], true, _atomXdndActionCopy);
        }
        else if (msgType == _atomXdndDrop)
        {
            _dndSource = (nuint)data[0];
            _dndDropTime = (nuint)data[1];
            // request the selection as text/uri-list into a property on our window
            _dndDropProperty = XInternAtom(_display, "XdndDropData", false);
            XConvertSelection(_display, _atomXdndSelection, _atomUriList, _dndDropProperty, _window, _dndDropTime);
            XFlush(_display);
        }
        else if (msgType == _atomXdndLeave)
        {
            _dndSource = 0;
        }
    }

    /// <summary>Send an XdndStatus ClientMessage to the drag source.</summary>
    private void SendXdndStatus(nuint source, bool accept, nuint action)
    {
        byte* msg = stackalloc byte[EventBufBytes];
        *(int*)msg = ClientMessage;             // type
        *(nint*)(msg + 24) = _display;          // display
        *(nuint*)(msg + 32) = source;           // window (the source)
        *(nuint*)(msg + 40) = _atomXdndStatus;  // message_type
        *(int*)(msg + 48) = 32;                 // format (32 = longs)
        long* data = (long*)(msg + 56);
        data[0] = (long)_window;                // target window (us)
        data[1] = accept ? 1 : 0;               // accept
        data[2] = 0;                            // no rect (accept everywhere)
        data[3] = 0;
        data[4] = (long)action;                 // action
        XSendEvent(_display, source, false, NoEventMask, msg);
        XFlush(_display);
    }

    /// <summary>Handle SelectionNotify (the drop data arrived in our property).</summary>
    private void HandleSelectionNotify(byte* ev)
    {
        nuint property = *(nuint*)(ev + 56);   // property atom
        if (property != _dndDropProperty || _dndDropProperty == 0) return;

        try
        {
            XGetWindowProperty(_display, _window, _dndDropProperty, 0, long.MaxValue, true, _atomUriList,
                out _, out _, out nuint nitems, out _, out var dataPtr);
            if (dataPtr != 0 && nitems > 0)
            {
                var bytes = new byte[(int)nitems];
                Marshal.Copy((nint)dataPtr, bytes, 0, (int)nitems);
                XFree((void*)dataPtr);
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                var paths = ParseUriList(text);
                if (paths.Count > 0)
                    Post(s => s.FilesDropped(paths.ToArray()));
            }
            else if (dataPtr != 0) XFree((void*)dataPtr);
            XDeleteProperty(_display, _window, _dndDropProperty);
        }
        catch { /* best-effort: a failed drop is harmless */ }
        _dndDropProperty = 0;
    }

    /// <summary>Parse an X11 text/uri-list into local file paths.</summary>
    private static System.Collections.Generic.List<string> ParseUriList(string text)
    {
        var paths = new System.Collections.Generic.List<string>();
        foreach (var line in text.Split('\n'))
        {
            var uri = line.Trim();
            if (uri.Length == 0 || uri.StartsWith("#")) continue;
            if (uri.StartsWith("file://"))
            {
                // strip "file://" and URL-decode the path
                var path = uri[7..];
                // find the start of the actual path (after host, usually empty → "file:///path")
                if (path.Length > 0 && path[0] != '/')
                {
                    int slash = path.IndexOf('/');
                    if (slash >= 0) path = path[slash..];
                }
                path = System.Uri.UnescapeDataString(path);
                paths.Add(path);
            }
            else if (System.IO.File.Exists(uri))
            {
                paths.Add(uri);
            }
        }
        return paths;
    }
}
