using System.Runtime.InteropServices;

namespace Sable.Canvas.Platform;

/// <summary>
/// CoreGraphics point — two CGFloats (doubles). Matches NSPoint. 16-byte struct: returned
/// in vector registers on arm64, so plain <c>objc_msgSend</c> works (no <c>_stret</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint { public double X, Y; }

/// <summary>CoreGraphics rect — origin + size as four CGFloats. Matches NSRect.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGRect { public double X, Y, Width, Height; }

/// <summary>
/// Thin Objective-C runtime + CoreGraphics interop for the macOS canvas backend. This is the
/// macOS analog of the Win32 P/Invoke that backs <see cref="WindowsBackend"/> /
/// <see cref="WindowsInputSource"/> — it lets us turn the Avalonia NSView into a wgpu
/// CAMetalLayer surface and dynamically subclass that view to capture mouse events.
///
/// ABI note: targets Apple Silicon (arm64). On arm64 every <c>objc_msgSend</c> uses the one
/// entry point — structs ≤16 bytes return in registers and larger ones (CGRect) via the x8
/// indirect-result register, both of which the .NET marshaller handles. The legacy x86_64
/// <c>objc_msgSend_stret</c> path is not needed here.
/// </summary>
internal static unsafe class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // --- class / selector / dynamic-subclass machinery ---
    [DllImport(Lib)] public static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] public static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] public static extern nint object_getClass(nint obj);
    [DllImport(Lib)] public static extern nint object_setClass(nint obj, nint cls);
    [DllImport(Lib)] public static extern nint objc_allocateClassPair(nint superclass,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extraBytes);
    [DllImport(Lib)] public static extern void objc_registerClassPair(nint cls);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool class_addMethod(nint cls, nint name, nint imp,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    /// <summary>Look up a runtime class by name (must already be loaded — AppKit is, in an Avalonia app).</summary>
    public static nint Cls(string name) => objc_getClass(name);
    /// <summary>Intern a selector.</summary>
    public static nint Sel(string name) => sel_registerName(name);

    // --- objc_msgSend, one overload per call signature we use (arm64 single entry point) ---
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nint SendPtr(nint self, nint sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(nint self, nint sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoidPtr(nint self, nint sel, nint a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoidBool(nint self, nint sel,
        [MarshalAs(UnmanagedType.U1)] bool a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoidDouble(nint self, nint sel, double a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern double SendDouble(nint self, nint sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern ulong SendULong(nint self, nint sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern CGPoint SendPoint(nint self, nint sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern CGRect SendRect(nint self, nint sel);
    // -[NSView convertPoint:fromView:] — point (HFA, v0/v1) + view ptr; returns a point.
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern CGPoint SendPointConvert(nint self, nint sel,
        CGPoint p, nint fromView);
    // -[NSTrackingArea initWithRect:options:owner:userInfo:] — rect (HFA, v0..v3) + 3 ptr/int args.
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nint SendTrackingInit(nint self, nint sel,
        CGRect rect, ulong opts, nint owner, nint userInfo);

    // --- CoreGraphics: cursor warp for the HUD brush-adjust (hide → restore at start) ---
    [DllImport(CG)] public static extern nint CGEventCreate(nint source);
    [DllImport(CG)] public static extern CGPoint CGEventGetLocation(nint @event);
    [DllImport(CG)] public static extern int CGWarpMouseCursorPosition(CGPoint newCursorPosition);
    [DllImport(CG)] public static extern int CGAssociateMouseAndMouseCursorPosition(int connected);
    [DllImport(CF)] public static extern void CFRelease(nint cf);
}
