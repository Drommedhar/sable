using Sable.Engine.Layers;

namespace Sable.Engine.Clipboard;

/// <summary>
/// Process-internal clipboard (PLAN §16.2): holds either a copied pixel region
/// (RGBA8 + size, from a selection / copy-merged) or a copied whole layer (with its
/// params/effects/children). Shared across document tabs. The OS clipboard is handled
/// separately in the app layer (best-effort image interop).
/// </summary>
public static class SableClipboard
{
    public static byte[]? Pixels { get; private set; }
    public static int Width { get; private set; }
    public static int Height { get; private set; }

    /// <summary>A copied whole layer (already a clone). Paste clones it again so the clipboard keeps its copy.</summary>
    public static Layer? Layer { get; private set; }

    public static bool HasContent => Pixels is not null || Layer is not null;

    public static void SetRegion(byte[] pixels, int width, int height)
    {
        Pixels = pixels; Width = width; Height = height; Layer = null;
    }

    public static void SetLayer(Layer layer)
    {
        Layer = layer; Pixels = null;
    }

    public static void Clear() { Pixels = null; Layer = null; }
}
