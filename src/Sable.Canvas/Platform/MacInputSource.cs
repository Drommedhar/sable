using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Sable.Canvas.Platform;

/// <summary>
/// macOS canvas input. The Avalonia NSView that hosts the GPU surface receives mouse events
/// directly (airspace — Avalonia can't see them), so we do the macOS analog of the Windows
/// WndProc subclass: build a one-off Objective-C subclass of the view's class at runtime,
/// override the mouse/scroll methods with C# function pointers, and reclass the live view
/// into it. The IMPs decode each NSEvent into shared <see cref="ICanvasInputSink"/> calls in
/// SURFACE pixels (top-left origin); all tool logic downstream is platform-agnostic.
///
/// AppKit delivers the whole down→drag→up sequence to the view that got the mouse-down, so
/// pointer "capture" is implicit and <see cref="Capture"/>/<see cref="ReleaseCapture"/> are
/// no-ops. Hover moves (no button held) come from an <c>NSTrackingArea</c>.
/// </summary>
internal sealed unsafe class MacInputSource : IInputSource
{
    // NSEvent.modifierFlags device-independent bits.
    private const ulong NSShift = 1UL << 17, NSControl = 1UL << 18, NSOption = 1UL << 19;
    // NSTrackingArea options: mouse-moved + active while key + auto-sized to the view's visible rect.
    private const ulong TrackingOpts = 0x02 /*MouseMoved*/ | 0x20 /*ActiveInKeyWindow*/ | 0x200 /*InVisibleRect*/;

    private static readonly nint SelLocationInWindow = ObjC.Sel("locationInWindow");
    private static readonly nint SelConvertPointFromView = ObjC.Sel("convertPoint:fromView:");
    private static readonly nint SelBounds = ObjC.Sel("bounds");
    private static readonly nint SelModifierFlags = ObjC.Sel("modifierFlags");
    private static readonly nint SelScrollingDeltaY = ObjC.Sel("scrollingDeltaY");
    private static readonly nint SelMagnification = ObjC.Sel("magnification");
    private static readonly nint SelPhase = ObjC.Sel("phase");

    // One 1.1x zoom step per this much accumulated pinch magnification (~10% scale ≈ one wheel notch).
    private const double MagnifyStep = 0.08;

    // Live NSView ptr → owning source, so the static IMPs can route back to the right sink.
    private static readonly ConcurrentDictionary<nint, MacInputSource> ByView = new();
    private static nint _subclass;   // our dynamically-built NSView subclass, created once

    private nint _view;
    private ICanvasInputSink? _sink;
    private nint _trackingArea;
    private CGPoint _savedCursor;
    private bool _cursorHidden;
    private double _magAccum;   // accumulated pinch magnification, drained in MagnifyStep chunks

    public void Attach(nint windowHandle, ICanvasInputSink sink)
    {
        _view = windowHandle;
        _sink = sink;
        if (_view == 0) return;
        EnsureSubclass(_view);
        ByView[_view] = this;
        ObjC.object_setClass(_view, _subclass);   // reclass the live view → our event-overriding subclass
        AddTrackingArea();
    }

    // macOS routes drag/up to the mouse-down view automatically; nothing to capture.
    public void Capture() { }
    public void ReleaseCapture() { }

    public void HideCursor()
    {
        if (_cursorHidden) return;
        nint ev = ObjC.CGEventCreate(nint.Zero);
        _savedCursor = ObjC.CGEventGetLocation(ev);   // global, top-left origin
        if (ev != 0) ObjC.CFRelease(ev);
        ObjC.SendVoid(ObjC.Cls("NSCursor"), ObjC.Sel("hide"));
        _cursorHidden = true;
    }

    public void RestoreCursor()
    {
        if (!_cursorHidden) return;
        ObjC.CGWarpMouseCursorPosition(_savedCursor);             // warp back to where the drag began
        ObjC.CGAssociateMouseAndMouseCursorPosition(1);           // re-link the warped position to mouse deltas
        ObjC.SendVoid(ObjC.Cls("NSCursor"), ObjC.Sel("unhide"));
        _cursorHidden = false;
    }

    public void Dispose()
    {
        if (_view != 0)
        {
            if (_trackingArea != 0) ObjC.SendVoidPtr(_view, ObjC.Sel("removeTrackingArea:"), _trackingArea);
            ByView.TryRemove(_view, out _);
            // The view is being destroyed by NativeControlHost; no need to reclass back to super.
        }
        if (_cursorHidden) RestoreCursor();
        _trackingArea = 0; _view = 0; _sink = null;
    }

    // --- dynamic subclass build (once per process) ---

    private static void EnsureSubclass(nint view)
    {
        if (_subclass != 0) return;
        nint existing = ObjC.objc_getClass("SableCanvasNSView");
        if (existing != 0) { _subclass = existing; return; }

        nint super = ObjC.object_getClass(view);   // Avalonia's NSView class — inherit all its behaviour
        nint cls = ObjC.objc_allocateClassPair(super, "SableCanvasNSView", 0);

        AddVoid(cls, "mouseDown:", &OnMouseDown);
        AddVoid(cls, "mouseDragged:", &OnMouseMove);
        AddVoid(cls, "mouseUp:", &OnMouseUp);
        AddVoid(cls, "mouseMoved:", &OnMouseMove);          // hover (no button) — via the tracking area
        AddVoid(cls, "otherMouseDown:", &OnOtherDown);       // middle button → pan
        AddVoid(cls, "otherMouseDragged:", &OnMouseMove);
        AddVoid(cls, "otherMouseUp:", &OnOtherUp);
        AddVoid(cls, "scrollWheel:", &OnScroll);
        AddVoid(cls, "magnifyWithEvent:", &OnMagnify);       // trackpad pinch-to-zoom
        // Deliver the first click even when the window isn't key (Photoshop feel).
        ObjC.class_addMethod(cls, ObjC.Sel("acceptsFirstMouse:"),
            (nint)(delegate* unmanaged<nint, nint, nint, byte>)&OnAcceptsFirstMouse, "c@:@");

        ObjC.objc_registerClassPair(cls);
        _subclass = cls;
    }

    private static void AddVoid(nint cls, string sel, delegate* unmanaged<nint, nint, nint, void> imp)
        => ObjC.class_addMethod(cls, ObjC.Sel(sel), (nint)imp, "v@:@");

    private void AddTrackingArea()
    {
        nint alloc = ObjC.SendPtr(ObjC.Cls("NSTrackingArea"), ObjC.Sel("alloc"));
        if (alloc == 0) return;
        var zeroRect = default(CGRect);   // ignored under InVisibleRect; the area tracks the view's bounds
        _trackingArea = ObjC.SendTrackingInit(alloc, ObjC.Sel("initWithRect:options:owner:userInfo:"),
            zeroRect, TrackingOpts, _view, nint.Zero);
        if (_trackingArea != 0) ObjC.SendVoidPtr(_view, ObjC.Sel("addTrackingArea:"), _trackingArea);
    }

    // --- IMPs: signature (id self, SEL _cmd, NSEvent* event). Never let an exception unwind into AppKit. ---

    [UnmanagedCallersOnly] private static void OnMouseDown(nint self, nint cmd, nint evt) => Route(self, evt, Kind.Down, CanvasButton.Left);
    [UnmanagedCallersOnly] private static void OnMouseUp(nint self, nint cmd, nint evt) => Route(self, evt, Kind.Up, CanvasButton.Left);
    [UnmanagedCallersOnly] private static void OnOtherDown(nint self, nint cmd, nint evt) => Route(self, evt, Kind.Down, CanvasButton.Middle);
    [UnmanagedCallersOnly] private static void OnOtherUp(nint self, nint cmd, nint evt) => Route(self, evt, Kind.Up, CanvasButton.Middle);
    [UnmanagedCallersOnly] private static void OnMouseMove(nint self, nint cmd, nint evt) => Route(self, evt, Kind.Move, CanvasButton.Left);
    [UnmanagedCallersOnly] private static byte OnAcceptsFirstMouse(nint self, nint cmd, nint evt) => 1;

    [UnmanagedCallersOnly]
    private static void OnScroll(nint self, nint cmd, nint evt)
    {
        try
        {
            if (!ByView.TryGetValue(self, out var s) || s._sink is not { } sink) return;
            double dy = ObjC.SendDouble(evt, SelScrollingDeltaY);
            if (dy == 0) return;
            var (sx, sy) = Locate(self, evt);
            sink.Wheel(sx, sy, dy > 0 ? 1 : -1, ReadMods(evt));   // up/away → zoom in, matching the Windows wheel sign
        }
        catch { }
    }

    [UnmanagedCallersOnly]
    private static void OnMagnify(nint self, nint cmd, nint evt)
    {
        try
        {
            if (!ByView.TryGetValue(self, out var s) || s._sink is not { } sink) return;
            // Reset the accumulator at the start of each pinch so leftover deltas don't carry over.
            if ((ObjC.SendULong(evt, SelPhase) & 1) != 0) s._magAccum = 0;   // NSEventPhaseBegan
            double mag = ObjC.SendDouble(evt, SelMagnification);             // per-event delta (~0.01–0.05)
            if (mag == 0) return;
            var (sx, sy) = Locate(self, evt);
            var mods = ReadMods(evt);
            s._magAccum += mag;
            // Each magnify event is tiny; drain the accumulator into discrete zoom-to-cursor steps.
            while (s._magAccum >= MagnifyStep) { sink.Wheel(sx, sy, 1, mods); s._magAccum -= MagnifyStep; }
            while (s._magAccum <= -MagnifyStep) { sink.Wheel(sx, sy, -1, mods); s._magAccum += MagnifyStep; }
        }
        catch { }
    }

    private enum Kind { Down, Move, Up }

    private static void Route(nint self, nint evt, Kind kind, CanvasButton button)
    {
        try
        {
            if (!ByView.TryGetValue(self, out var s) || s._sink is not { } sink) return;
            var (sx, sy) = Locate(self, evt);
            var mods = ReadMods(evt);
            switch (kind)
            {
                case Kind.Down: sink.PointerDown(button, sx, sy, mods); break;
                case Kind.Move: sink.PointerMove(sx, sy, mods); break;
                case Kind.Up: sink.PointerUp(button, sx, sy, mods); break;
            }
        }
        catch { }
    }

    /// <summary>Event location → surface pixels (top-left origin), matching the Windows client-coord convention.</summary>
    private static (double sx, double sy) Locate(nint view, nint evt)
    {
        var win = ObjC.SendPoint(evt, SelLocationInWindow);                           // window coords, bottom-left
        var local = ObjC.SendPointConvert(view, SelConvertPointFromView, win, nint.Zero);  // view coords, bottom-left
        var bounds = ObjC.SendRect(view, SelBounds);
        return (local.X, bounds.Height - local.Y);                                    // flip Y → top-left origin
    }

    private static CanvasMods ReadMods(nint evt)
    {
        ulong f = ObjC.SendULong(evt, SelModifierFlags);
        var m = CanvasMods.None;
        if ((f & NSShift) != 0) m |= CanvasMods.Shift;
        if ((f & NSOption) != 0) m |= CanvasMods.Alt;     // Option = Alt (eyedropper / subtract / HUD)
        if ((f & NSControl) != 0) m |= CanvasMods.Ctrl;   // Control = Ctrl (Ctrl+Option brush HUD, Photoshop-style)
        return m;
    }
}
